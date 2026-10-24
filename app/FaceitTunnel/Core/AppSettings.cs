using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FaceitTunnel.Core;

public enum TunnelMode
{
    /// <summary>Трафик Faceit идёт через VPN-сервер из подписки.</summary>
    Vpn,
    /// <summary>Без VPN: напрямую с фрагментацией TLS, Faceit видит ваш IP.</summary>
    Direct,
}

/// <summary>Подписка или одиночный ключ vless://, который пользователь добавил в приложение.</summary>
public sealed class SubscriptionEntry
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
}

public sealed class AppSettings
{
    public List<SubscriptionEntry> Subscriptions { get; set; } = new();
    /// <summary>Ключ выбранного сервера: «подписка|сервер» (см. <see cref="ServerInfo.Key"/>).</summary>
    public string SelectedServer { get; set; } = "";
    public TunnelMode Mode { get; set; } = TunnelMode.Vpn;
    public bool MinimizeToTray { get; set; } = true;
    public bool ConnectOnStart { get; set; }

    /// <summary>Единственная подписка из версий до списка подписок — переносится при загрузке.</summary>
    [JsonPropertyName("Subscription")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacySubscription { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(AppPaths.SettingsFile)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception)
        {
            // Битый файл настроек не должен мешать запуску — начинаем с чистых.
            settings = new AppSettings();
        }

        if (!string.IsNullOrWhiteSpace(settings.LegacySubscription) && settings.Subscriptions.Count == 0)
        {
            settings.Subscriptions.Add(new SubscriptionEntry { Name = "Основная", Source = settings.LegacySubscription.Trim() });
            settings.LegacySubscription = null;
            settings.Save();
        }
        return settings;
    }

    public void Save() => File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json));
}
