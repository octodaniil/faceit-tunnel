using System;
using System.IO;
using System.Text.Json;

namespace FaceitTunnel.Core;

public enum TunnelMode
{
    /// <summary>Трафик Faceit идёт через VPN-сервер из подписки.</summary>
    Vpn,
    /// <summary>Без VPN: напрямую с фрагментацией TLS, Faceit видит ваш IP.</summary>
    Direct,
}

public sealed class AppSettings
{
    public string Subscription { get; set; } = "";
    public string SelectedServer { get; set; } = "";
    public TunnelMode Mode { get; set; } = TunnelMode.Vpn;
    public bool MinimizeToTray { get; set; } = true;
    public bool ConnectOnStart { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
        }
        catch (Exception)
        {
            // Битый файл настроек не должен мешать запуску — начинаем с чистых.
        }
        return new AppSettings();
    }

    public void Save() => File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json));
}
