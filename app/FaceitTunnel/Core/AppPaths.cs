using System;
using System.IO;

namespace FaceitTunnel.Core;

/// <summary>Где что лежит. Настройки и ключи — в %AppData%, чтобы обновления их не трогали.</summary>
public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FaceitTunnel");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string RulesCacheFile => Path.Combine(DataDir, "rules.json");
    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string LogFile => Path.Combine(DataDir, "sing-box.log");

    public static string SingBoxExe => Path.Combine(AppContext.BaseDirectory, "bin", "sing-box.exe");

    static AppPaths() => Directory.CreateDirectory(DataDir);
}
