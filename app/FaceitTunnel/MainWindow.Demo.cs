#if DEBUG
using System;
using FaceitTunnel.Core;

namespace FaceitTunnel;

/// <summary>Только для Debug: FT_DEMO=connected|connecting показывает интерфейс в этом состоянии без туннеля.</summary>
public partial class MainWindow
{
    partial void ApplyDemo()
    {
        if (Program.Demo == null) return;
        Loaded += (_, _) =>
        {
            switch (Environment.GetEnvironmentVariable("FT_TAB"))
            {
                case "log": NavLog.IsChecked = true; break;
                case "settings": NavSettings.IsChecked = true; break;
            }
        };
        var state = Program.Demo switch
        {
            "connected" => TunnelState.Connected,
            "connecting" => TunnelState.Connecting,
            _ => TunnelState.Disconnected,
        };
        if (state == TunnelState.Disconnected) return;

        ConnectLabel.Text = state == TunnelState.Connected ? "Отключить" : "Отменить";
        StatusText.Text = state == TunnelState.Connected ? "Подключено" : "Подключение…";
        StatusSub.Text = "Faceit через home. Игра идёт напрямую.";
        SetStateVisuals(state);
        if (state != TunnelState.Connected) return;

        CountryText.Text = "Россия (RU)";
        IpText.Text = "188.134.83.31";
        SetCheck(SiteText, SiteDot, true);
        SetCheck(AntiCheatText, AntiCheatDot, true);
        var now = DateTime.Now;
        AddLog(new LogEntry(now.AddSeconds(-9), LogKind.Info, "Туннель запущен: Faceit идёт через VPN"));
        AddLog(new LogEntry(now.AddSeconds(-7), LogKind.Vpn, "VPN → ac-client-ws.faceit.com:443 · faceitservice.exe"));
        AddLog(new LogEntry(now.AddSeconds(-5), LogKind.Vpn, "VPN → www.faceit.com:443 · FACEIT.exe"));
        AddLog(new LogEntry(now.AddSeconds(-4), LogKind.Vpn, "VPN → api.faceit.com:443 · FACEIT.exe"));
        AddLog(new LogEntry(now.AddSeconds(-2), LogKind.Error, "open connection to 193.41.200.101:443: i/o timeout"));
    }
}
#endif
