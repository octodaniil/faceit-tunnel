using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace FaceitTunnel.Core;

/// <summary>
/// Что пускать через туннель. Список подтягивается из репозитория, поэтому при смене адресов
/// Faceit хватает поправить rules.json на GitHub — без новой версии приложения.
/// </summary>
public sealed class Rules
{
    private const string RemoteUrl = "https://raw.githubusercontent.com/octodaniil/faceit-tunnel/main/rules.json";

    public string[] Domains { get; set; } = { "faceit.com", "faceit-cdn.net", "faceit.gg", "challenges.cloudflare.com" };
    public string[] Cidrs { get; set; } = { "193.41.200.0/24" };
    public string[] Processes { get; set; } = { "FACEIT.exe", "faceitclient.exe", "faceitservice.exe" };

    /// <summary>
    /// Адреса этих хостов определяются при подключении и вместе с <see cref="Cidrs"/> становятся
    /// единственными маршрутами в туннель — игра и остальной трафик идут мимо sing-box.
    /// </summary>
    public string[] Hosts { get; set; } =
    {
        "www.faceit.com", "api.faceit.com", "open.faceit.com", "edge.faceit.com", "accounts.faceit.com",
        "ac-client-ws.faceit.com", "cdn.faceit.com", "cdn-frontend.faceit-cdn.net", "assets.faceit-cdn.net",
        "faceit.gg", "challenges.cloudflare.com",
    };

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Кэш с прошлого запуска, иначе встроенные значения.</summary>
    public static Rules LoadCached()
    {
        try
        {
            if (File.Exists(AppPaths.RulesCacheFile))
                return Parse(File.ReadAllText(AppPaths.RulesCacheFile)) ?? new Rules();
        }
        catch (Exception)
        {
        }
        return new Rules();
    }

    /// <summary>Свежий список с GitHub; null, если не удалось — тогда остаётся кэш.</summary>
    public static async Task<Rules?> FetchRemoteAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var text = await http.GetStringAsync(RemoteUrl);
            var rules = Parse(text);
            if (rules != null) File.WriteAllText(AppPaths.RulesCacheFile, text);
            return rules;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Rules? Parse(string text)
    {
        var rules = JsonSerializer.Deserialize<Rules>(text, Json);
        if (rules == null || rules.Domains.Length == 0) return null;
        return rules;
    }
}
