using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace FaceitTunnel.Core;

public sealed record FaceitStatus(string? Ip, string? Country, bool SiteOk, bool AntiCheatOk);

/// <summary>Проверяет доступность Faceit тем же путём, что и приложение Faceit (через туннель).</summary>
public static class StatusChecker
{
    public static async Task<FaceitStatus> CheckAsync()
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(12) };

        string? ip = null, country = null;
        var site = false;
        try
        {
            // Cloudflare отдаёт адрес и страну, которые видит сервер Faceit.
            var trace = await http.GetStringAsync("https://www.faceit.com/cdn-cgi/trace");
            foreach (var line in trace.Split('\n'))
            {
                if (line.StartsWith("ip=")) ip = line[3..].Trim();
                if (line.StartsWith("loc=")) country = line[4..].Trim();
            }
            site = true;
        }
        catch (Exception)
        {
        }

        var antiCheat = await RespondsAsync(http, "https://ac-client-ws.faceit.com/");
        return new FaceitStatus(ip, country, site, antiCheat);
    }

    // Любой HTTP-ответ (даже 404) значит, что сервер достижим и блокировка обойдена.
    private static async Task<bool> RespondsAsync(HttpClient http, string url)
    {
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
