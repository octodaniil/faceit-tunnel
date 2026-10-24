using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FaceitTunnel.Core;

public enum TunnelState { Disconnected, Connecting, Connected }

public enum LogKind { Info, Vpn, Direct, Error }

public sealed record LogEntry(DateTime Time, LogKind Kind, string Text);

/// <summary>Запускает sing-box, следит за ним и переводит его лог в понятные записи.</summary>
public sealed class TunnelService : IDisposable
{
    private Process? _process;
    private StreamWriter? _log;
    private TunnelMode _mode;
    private Rules _rules = new();
    // sing-box пишет процесс и само соединение разными строками с общим id — связываем их.
    private readonly ConcurrentDictionary<string, string> _processById = new();

    public TunnelState State { get; private set; } = TunnelState.Disconnected;
    public string? LastError { get; private set; }

    public event Action<TunnelState>? StateChanged;
    public event Action<LogEntry>? Log;

    public void Start(ServerInfo? server, Rules rules)
    {
        if (State != TunnelState.Disconnected) return;
        if (!File.Exists(AppPaths.SingBoxExe))
            throw new FileNotFoundException("Не найден sing-box.exe рядом с приложением. Переустановите Faceit Tunnel.");

        _mode = server == null ? TunnelMode.Direct : TunnelMode.Vpn;
        _rules = rules;
        LastError = null;
        _processById.Clear();
        KillStale();

        var config = ConfigBuilder.Build(server, rules);
        // Через Utf8JsonWriter: ToJsonString(options) в .NET 8 падает без TypeInfoResolver.
        using (var file = File.Create(AppPaths.ConfigFile))
        using (var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
            config.WriteTo(writer);

        _log = new StreamWriter(AppPaths.LogFile, false, new UTF8Encoding(false)) { AutoFlush = true };
        var psi = new ProcessStartInfo(AppPaths.SingBoxExe)
        {
            ArgumentList = { "run", "-c", AppPaths.ConfigFile, "--disable-color" },
            WorkingDirectory = AppPaths.DataDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        _process.OutputDataReceived += (_, e) => OnLine(e.Data);
        _process.Exited += (_, _) => OnExited();

        SetState(TunnelState.Connecting);
        _process.Start();
        _process.BeginErrorReadLine();
        _process.BeginOutputReadLine();
    }

    public void Stop()
    {
        var p = _process;
        if (p == null) return;
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // Уже завершился.
        }
    }

    private void OnExited()
    {
        var code = _process?.ExitCode ?? 0;
        _process?.Dispose();
        _process = null;
        _log?.Dispose();
        _log = null;
        if (State == TunnelState.Connecting && LastError == null)
            LastError = $"sing-box завершился с кодом {code}. Подробности — в журнале.";
        Emit(LogKind.Info, "Туннель остановлен");
        SetState(TunnelState.Disconnected);
    }

    private static readonly Regex Ansi = new(@"\x1b\[[0-9;]*m");
    private static readonly Regex IdRegex = new(@"\[(\d+) [^\]]*\]");
    private static readonly Regex ProcessRegex = new(@"found process path: .*\\([^\\]+\.exe)", RegexOptions.IgnoreCase);
    private static readonly Regex ProxyConnRegex = new(@"outbound/\w+\[proxy\]: outbound (?:packet )?connection to (\S+)");
    private static readonly Regex DirectConnRegex = new(@"outbound/direct\[direct\]: outbound (?:packet )?connection to (\S+)");

    private void OnLine(string? raw)
    {
        if (raw == null) return;
        var line = Ansi.Replace(raw, "");
        _log?.WriteLine(line);

        var idMatch = IdRegex.Match(line);
        var id = idMatch.Success ? idMatch.Groups[1].Value : null;

        if (line.Contains("sing-box started"))
        {
            SetState(TunnelState.Connected);
            Emit(LogKind.Info, _mode == TunnelMode.Vpn ? "Туннель запущен: Faceit идёт через VPN" : "Туннель запущен: Faceit идёт напрямую с фрагментацией");
            return;
        }
        if (line.Contains("FATAL"))
        {
            LastError = Shorten(line);
            Emit(LogKind.Error, LastError);
            return;
        }

        var pm = ProcessRegex.Match(line);
        if (pm.Success && id != null)
        {
            _processById[id] = pm.Groups[1].Value;
            return;
        }

        var proxy = ProxyConnRegex.Match(line);
        if (proxy.Success)
        {
            Emit(LogKind.Vpn, Describe("VPN", proxy.Groups[1].Value, id));
            return;
        }

        if (_mode == TunnelMode.Direct)
        {
            var direct = DirectConnRegex.Match(line);
            if (direct.Success && IsFaceit(direct.Groups[1].Value, id))
            {
                Emit(LogKind.Direct, Describe("Напрямую", direct.Groups[1].Value, id));
                return;
            }
        }

        // Ошибки чужих программ (Radmin, Windows и т. п.) не показываем — только связанные с Faceit или VPN.
        if (line.Contains("ERROR") && (line.Contains("[proxy]") || IsFaceit(line, id)))
            Emit(LogKind.Error, Shorten(line));
    }

    private string Describe(string via, string target, string? id)
    {
        var proc = id != null && _processById.TryGetValue(id, out var p) ? $" · {p}" : "";
        return $"{via} → {target}{proc}";
    }

    private bool IsFaceit(string text, string? id)
    {
        if (id != null && _processById.TryGetValue(id, out var proc)
            && _rules.Processes.Any(x => x.Equals(proc, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (_rules.Domains.Any(d => text.Contains(d, StringComparison.OrdinalIgnoreCase))) return true;
        return text.Contains("193.41.200.");
    }

    private static string Shorten(string line)
    {
        // Убираем дату и id соединения — в окне они только мешают.
        var idx = line.IndexOf("] ", StringComparison.Ordinal);
        if (idx < 0) idx = line.IndexOf("FATAL", StringComparison.Ordinal) - 2;
        return (idx >= 0 ? line[(idx + 2)..] : line).Trim();
    }

    private void Emit(LogKind kind, string text) => Log?.Invoke(new LogEntry(DateTime.Now, kind, text));

    private void SetState(TunnelState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    /// <summary>sing-box от прошлого запуска мог остаться и держать TUN-адаптер.</summary>
    private static void KillStale()
    {
        foreach (var p in Process.GetProcessesByName("sing-box"))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, AppPaths.SingBoxExe, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
            }
            catch (Exception)
            {
                // Чужой процесс без доступа — не наш.
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _log?.Dispose();
    }
}
