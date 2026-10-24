using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FaceitTunnel.Core;

public sealed record ServerInfo
{
    public string Name { get; init; } = "";
    public string Server { get; init; } = "";
    public int Port { get; init; }
    public string Uuid { get; init; } = "";
    public string? Flow { get; init; }
    public string? Security { get; init; }
    public string? Sni { get; init; }
    public string? Fingerprint { get; init; }
    public string? PublicKey { get; init; }
    public string? ShortId { get; init; }
    public string Network { get; init; } = "tcp";
    public string? Path { get; init; }
    public string? HostHeader { get; init; }
    public string? ServiceName { get; init; }

    /// <summary>Название подписки, из которой взят сервер.</summary>
    public string Group { get; init; } = "";

    /// <summary>Уникален среди всех подписок: одинаковые названия серверов в разных подписках не путаются.</summary>
    public string Key => $"{Group}|{Name}";

    public override string ToString() => Name;
}

/// <summary>Загрузка подписки: ссылка https://, одиночная vless://, base64-список или JSON-конфиги Xray.</summary>
public static class Subscription
{
    public static async Task<List<ServerInfo>> LoadAsync(string source)
    {
        source = source.Trim();
        string text = source;
        if (Regex.IsMatch(source, "^https?://", RegexOptions.IgnoreCase))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            // С таким User-Agent панели обычно отдают обычный список ссылок, а не формат конкретного клиента.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("v2rayN/7.10");
            text = await http.GetStringAsync(source);
        }
        return Parse(text);
    }

    /// <summary>Короткое описание источника без секретов: адрес подписки или сервера.</summary>
    public static string Describe(string source)
    {
        source = source.Trim();
        if (source.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
            return ParseVlessLink(source) is { } s ? $"VLESS · {s.Server}" : "VLESS";
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
            return uri.Host;
        return "Список серверов";
    }

    public static List<ServerInfo> Parse(string text)
    {
        text = text.Trim().TrimStart('﻿');
        if (!text.Contains("://") && !text.StartsWith('{') && !text.StartsWith('['))
        {
            try { text = DecodeBase64Loose(text).Trim(); }
            catch (FormatException) { throw new InvalidOperationException("Не удалось разобрать подписку: это не ссылки, не base64 и не JSON."); }
        }
        if (text.StartsWith('{') || text.StartsWith('['))
            return ParseXrayJson(JsonNode.Parse(text)).ToList();

        return text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
            .Select(ParseVlessLink)
            .Where(s => s != null)
            .Select(s => s!)
            .ToList();
    }

    private static string DecodeBase64Loose(string s)
    {
        var t = Regex.Replace(s, @"\s", "").Replace('-', '+').Replace('_', '/');
        t = (t.Length % 4) switch { 2 => t + "==", 3 => t + "=", _ => t };
        return Encoding.UTF8.GetString(Convert.FromBase64String(t));
    }

    private static readonly Regex VlessRegex = new(
        @"^vless://(?<uuid>[^@]+)@(?<host>\[[^\]]+\]|[^:/?#]+):(?<port>\d+)/?(?<query>\?[^#]*)?(?<name>#.*)?$",
        RegexOptions.IgnoreCase);

    public static ServerInfo? ParseVlessLink(string link)
    {
        var m = VlessRegex.Match(link);
        if (!m.Success) return null;
        var q = ParseQuery(m.Groups["query"].Value);
        var host = m.Groups["host"].Value;
        var name = m.Groups["name"].Success && m.Groups["name"].Value.Length > 1
            ? Uri.UnescapeDataString(m.Groups["name"].Value[1..])
            : host;
        return new ServerInfo
        {
            Name = name,
            Server = host.Trim('[', ']'),
            Port = int.Parse(m.Groups["port"].Value),
            Uuid = Uri.UnescapeDataString(m.Groups["uuid"].Value),
            Flow = q.GetValueOrDefault("flow"),
            Security = q.GetValueOrDefault("security"),
            Sni = q.GetValueOrDefault("sni"),
            Fingerprint = q.GetValueOrDefault("fp"),
            PublicKey = q.GetValueOrDefault("pbk"),
            ShortId = q.GetValueOrDefault("sid"),
            Network = q.GetValueOrDefault("type") is { Length: > 0 } type ? type : "tcp",
            Path = q.GetValueOrDefault("path"),
            HostHeader = q.GetValueOrDefault("host"),
            ServiceName = q.GetValueOrDefault("serviceName"),
        };
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            result[kv[0]] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
        }
        return result;
    }

    // Подписки для Happ и похожих клиентов иногда отдают готовые конфиги Xray.
    private static IEnumerable<ServerInfo> ParseXrayJson(JsonNode? root)
    {
        var configs = root is JsonArray arr ? arr.Select(n => n) : new[] { root };
        foreach (var cfg in configs)
        {
            if (cfg?["outbounds"] is not JsonArray outbounds) continue;
            foreach (var ob in outbounds)
            {
                if ((string?)ob?["protocol"] != "vless") continue;
                var st = ob["settings"];
                string? server, uuid, flow;
                int port;
                if (st?["vnext"] is JsonArray vnext && vnext.Count > 0)
                {
                    var v = vnext[0]!;
                    var user = v["users"]![0]!;
                    server = (string?)v["address"]; port = (int?)v["port"] ?? 443;
                    uuid = (string?)user["id"]; flow = (string?)user["flow"];
                }
                else
                {
                    server = (string?)st?["address"]; port = (int?)st?["port"] ?? 443;
                    uuid = (string?)st?["id"]; flow = (string?)st?["flow"];
                }
                if (server == null || uuid == null) continue;

                var ss = ob["streamSettings"];
                var security = (string?)ss?["security"];
                var tls = security == "reality" ? ss?["realitySettings"] : ss?["tlsSettings"];
                var ws = ss?["wsSettings"];
                yield return new ServerInfo
                {
                    Name = (string?)cfg["remarks"] ?? server,
                    Server = server,
                    Port = port,
                    Uuid = uuid,
                    Flow = flow,
                    Security = security,
                    Sni = (string?)tls?["serverName"],
                    Fingerprint = (string?)tls?["fingerprint"],
                    PublicKey = (string?)ss?["realitySettings"]?["publicKey"],
                    ShortId = (string?)ss?["realitySettings"]?["shortId"],
                    Network = (string?)ss?["network"] ?? "tcp",
                    Path = (string?)ws?["path"],
                    HostHeader = (string?)ws?["host"] ?? (string?)ws?["headers"]?["Host"],
                    ServiceName = (string?)ss?["grpcSettings"]?["serviceName"],
                };
            }
        }
    }
}
