using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace FaceitTunnel.Core;

/// <summary>Обновления из GitHub Releases через Velopack.</summary>
public sealed class Updater
{
    private const string RepoUrl = "https://github.com/octodaniil/faceit-tunnel";

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, null, false));
    private UpdateInfo? _pending;

    /// <summary>false, если приложение запущено не из установки (например, из папки сборки).</summary>
    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion => _manager.CurrentVersion?.ToString()
        ?? typeof(Updater).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>Номер новой версии или null, если обновлений нет.</summary>
    public async Task<string?> CheckAsync()
    {
        if (!_manager.IsInstalled) return null;
        _pending = await _manager.CheckForUpdatesAsync();
        return _pending?.TargetFullRelease.Version.ToString();
    }

    public async Task DownloadAndRestartAsync(Action<int>? progress = null)
    {
        if (_pending == null) return;
        await _manager.DownloadUpdatesAsync(_pending, progress);
        _manager.ApplyUpdatesAndRestart(_pending);
    }
}
