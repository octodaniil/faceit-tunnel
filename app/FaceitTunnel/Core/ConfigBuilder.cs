using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;

namespace FaceitTunnel.Core;

/// <summary>Собирает config.json для sing-box — то же, что делал faceit-tunnel.ps1.</summary>
public static class ConfigBuilder
{
    // Адреса TUN-адаптера: берётся первый, не занятый другим VPN
    // (Happ и многие клиенты на sing-box по умолчанию используют 172.19.0.1).
    private static readonly string[] TunAddresses = { "172.23.233.1/30", "10.211.233.1/30", "192.168.233.1/30" };

    private static readonly string[] Fingerprints =
        { "chrome", "firefox", "edge", "safari", "360", "qq", "ios", "android", "random", "randomized" };

    /// <param name="server">Сервер из подписки (режим VPN) или null (режим без VPN).</param>
    public static JsonObject Build(ServerInfo? server, Rules rules)
    {
        var rulesArray = new JsonArray
        {
            new JsonObject { ["action"] = "sniff" },
            new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" },
        };

        JsonArray outbounds;
        if (server != null)
        {
            outbounds = new JsonArray { BuildProxyOutbound(server), Direct() };
            foreach (var matcher in Matchers(rules))
            {
                matcher["outbound"] = "proxy";
                rulesArray.Add(matcher);
            }
        }
        else
        {
            outbounds = new JsonArray { Direct() };
            // QUIC так не фрагментируется — отклоняем его, браузер сам перейдёт на TCP.
            rulesArray.Add(new JsonObject { ["protocol"] = "quic", ["domain_suffix"] = Array(rules.Domains), ["action"] = "reject" });
            rulesArray.Add(new JsonObject { ["protocol"] = "quic", ["ip_cidr"] = Array(rules.Cidrs), ["action"] = "reject" });
            foreach (var matcher in Matchers(rules))
            {
                matcher["action"] = "route";
                matcher["outbound"] = "direct";
                matcher["tls_fragment"] = true;
                matcher["tls_record_fragment"] = true;
                rulesArray.Add(matcher);
            }
        }

        return new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "info", ["timestamp"] = true },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray { new JsonObject { ["type"] = "udp", ["tag"] = "dns-direct", ["server"] = "1.1.1.1" } },
                ["final"] = "dns-direct",
            },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "tun",
                    ["tag"] = "tun-in",
                    ["interface_name"] = "faceit-tunnel",
                    ["address"] = new JsonArray { FreeTunAddress() },
                    ["mtu"] = 9000,
                    ["auto_route"] = true,
                    ["strict_route"] = false,
                    ["stack"] = "mixed",
                },
            },
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject
            {
                ["rules"] = rulesArray,
                ["final"] = "direct",
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = "dns-direct",
            },
        };
    }

    private static JsonObject[] Matchers(Rules rules) => new[]
    {
        new JsonObject { ["process_name"] = Array(rules.Processes) },
        new JsonObject { ["domain_suffix"] = Array(rules.Domains) },
        new JsonObject { ["ip_cidr"] = Array(rules.Cidrs) },
    };

    private static JsonObject Direct() => new() { ["type"] = "direct", ["tag"] = "direct" };

    private static JsonArray Array(string[] values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    private static JsonObject BuildProxyOutbound(ServerInfo s)
    {
        var outbound = new JsonObject
        {
            ["type"] = "vless",
            ["tag"] = "proxy",
            ["server"] = s.Server,
            ["server_port"] = s.Port,
            ["uuid"] = s.Uuid,
        };
        if (!string.IsNullOrEmpty(s.Flow)) outbound["flow"] = s.Flow;

        if (s.Security is "tls" or "reality")
        {
            var fp = Fingerprints.Contains(s.Fingerprint) ? s.Fingerprint : "chrome";
            var tls = new JsonObject
            {
                ["enabled"] = true,
                ["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fp },
            };
            if (!string.IsNullOrEmpty(s.Sni)) tls["server_name"] = s.Sni;
            if (s.Security == "reality")
            {
                tls["reality"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["public_key"] = s.PublicKey,
                    ["short_id"] = s.ShortId ?? "",
                };
            }
            outbound["tls"] = tls;
        }

        switch (s.Network)
        {
            case "tcp":
            case "raw":
                break;
            case "ws":
                var ws = new JsonObject { ["type"] = "ws", ["path"] = string.IsNullOrEmpty(s.Path) ? "/" : s.Path };
                if (!string.IsNullOrEmpty(s.HostHeader)) ws["headers"] = new JsonObject { ["Host"] = s.HostHeader };
                outbound["transport"] = ws;
                break;
            case "grpc":
                outbound["transport"] = new JsonObject { ["type"] = "grpc", ["service_name"] = s.ServiceName ?? "" };
                break;
            default:
                throw new InvalidOperationException($"Транспорт «{s.Network}» не поддерживается. Выберите сервер с TCP, WS или gRPC.");
        }
        return outbound;
    }

    private static string FreeTunAddress()
    {
        var used = NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address.ToString())
            .ToHashSet();
        foreach (var cidr in TunAddresses)
            if (!used.Contains(cidr.Split('/')[0])) return cidr;
        throw new InvalidOperationException("Все адреса для TUN заняты другими сетевыми адаптерами. Отключите другие VPN.");
    }
}
