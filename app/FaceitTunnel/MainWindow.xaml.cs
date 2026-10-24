using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using FaceitTunnel.Core;
using Wpf.Ui.Controls;
using Forms = System.Windows.Forms;

namespace FaceitTunnel;

public sealed record LogItem(string Time, string Text, Brush Brush);

public sealed record SubscriptionRow(SubscriptionEntry Entry, string Name, string Detail);

public partial class MainWindow : FluentWindow
{
    private const int MaxLogItems = 300;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly TunnelService _tunnel = new();
    private readonly Updater _updater = new();
    private readonly ObservableCollection<LogItem> _log = new();
    private Rules _rules = Rules.LoadCached();
    private List<ServerInfo> _servers = new();
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _trayConnectItem;
    private bool _exiting;
    private bool _trayHintShown;
    private bool _loadingUi;

    private static readonly Brush Orange = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x1F)));
    private static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C)));
    private static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0x55, 0x5A)));
    private static readonly Brush Gray = Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x76)));
    private static readonly Brush Idle = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)));

    public MainWindow()
    {
        InitializeComponent();
        InitVisuals();
        LogList.ItemsSource = _log;

        _loadingUi = true;
        ModeVpn.IsChecked = _settings.Mode == TunnelMode.Vpn;
        ModeDirect.IsChecked = _settings.Mode == TunnelMode.Direct;
        TrayToggle.IsChecked = _settings.MinimizeToTray;
        AutoConnectToggle.IsChecked = _settings.ConnectOnStart;
        VersionText.Text = $"Версия {_updater.CurrentVersion}";
        _loadingUi = false;

        _tunnel.StateChanged += state => Dispatcher.Invoke(() => OnTunnelStateChanged(state));
        _tunnel.Log += entry => Dispatcher.Invoke(() => AddLog(entry));

        CreateTray();
        UpdateModeUi();
        UpdateConnectUi();
        UpdateZapretHint();

        ApplyDemo();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.AllowShowMessage(hwnd);
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        };
        Loaded += async (_, _) => await OnLoadedAsync();
    }

    partial void ApplyDemo();

    private async Task OnLoadedAsync()
    {
        if (Program.Demo != null) return;
        var remote = await Rules.FetchRemoteAsync();
        if (remote != null) _rules = remote;

        await LoadServersAsync();
        if (_settings.ConnectOnStart) Connect();

        await CheckUpdatesAsync(silent: true);
    }

    // ---------- Подключение ----------

    private void OnConnectClick(object sender, RoutedEventArgs e) => ToggleConnection();

    private void ToggleConnection()
    {
        if (_tunnel.State == TunnelState.Disconnected) Connect();
        else _tunnel.Stop();
    }

    private void Connect()
    {
        ServerInfo? server = null;
        if (_settings.Mode == TunnelMode.Vpn)
        {
            server = ServerCombo.SelectedItem as ServerInfo;
            if (server == null)
            {
                ShowStatusError("Нет сервера. Вставьте подписку на вкладке «Настройки».");
                return;
            }
        }
        try
        {
            _tunnel.Start(server, _rules);
        }
        catch (Exception ex)
        {
            ShowStatusError(ex.Message);
        }
    }

    private async void OnTunnelStateChanged(TunnelState state)
    {
        UpdateConnectUi();
        if (state == TunnelState.Connected)
        {
            await Task.Delay(1500);
            await CheckStatusAsync();
        }
        else if (state == TunnelState.Disconnected)
        {
            ResetStatusTiles();
            if (_tunnel.LastError != null) ShowStatusError(_tunnel.LastError);
        }
    }

    private void ResetStatusTiles()
    {
        CountryText.Text = IpText.Text = SiteText.Text = AntiCheatText.Text = "—";
        CountryText.Foreground = SiteText.Foreground = AntiCheatText.Foreground = (Brush)FindResource("Text2Brush");
        SiteDot.Fill = AntiCheatDot.Fill = Idle;
    }

    private void UpdateConnectUi()
    {
        switch (_tunnel.State)
        {
            case TunnelState.Disconnected:
                ConnectLabel.Text = "Подключить";
                StatusText.Text = "Не подключено";
                StatusSub.Text = _settings.Mode == TunnelMode.Vpn
                    ? "Трафик Faceit пойдёт через VPN, игра — напрямую."
                    : "Трафик Faceit пойдёт напрямую с обходом блокировки.";
                break;
            case TunnelState.Connecting:
                ConnectLabel.Text = "Отменить";
                StatusText.Text = "Подключение…";
                StatusSub.Text = "Запускаю туннель.";
                break;
            case TunnelState.Connected:
                ConnectLabel.Text = "Отключить";
                StatusText.Text = "Подключено";
                StatusSub.Text = _settings.Mode == TunnelMode.Vpn
                    ? $"Faceit через {(ServerCombo.SelectedItem as ServerInfo)?.Name}. Игра идёт напрямую."
                    : "Faceit напрямую с обходом блокировки.";
                break;
        }
        SetStateVisuals(_tunnel.State);
        ModeVpn.IsEnabled = ModeDirect.IsEnabled = ServerCombo.IsEnabled = _tunnel.State == TunnelState.Disconnected
            || _tunnel.State == TunnelState.Connected;
        if (_trayConnectItem != null)
            _trayConnectItem.Text = _tunnel.State == TunnelState.Disconnected ? "Подключить" : "Отключить";
        if (_tray != null)
            _tray.Text = $"Faceit Tunnel — {StatusText.Text.ToLowerInvariant()}";
    }

    private void ShowStatusError(string message)
    {
        StatusSub.Text = message;
        StatusSub.Foreground = Red;
        Task.Delay(8000).ContinueWith(_ => Dispatcher.Invoke(() => StatusSub.ClearValue(ForegroundProperty)));
    }

    // ---------- Проверка Faceit ----------

    private async void OnCheckClick(object sender, RoutedEventArgs e) => await CheckStatusAsync();

    private async Task CheckStatusAsync()
    {
        CheckButton.IsEnabled = false;
        CountryText.Text = IpText.Text = SiteText.Text = AntiCheatText.Text = "Проверяю…";
        CountryText.Foreground = SiteText.Foreground = AntiCheatText.Foreground = Gray;
        SiteDot.Fill = AntiCheatDot.Fill = Idle;
        try
        {
            var status = await StatusChecker.CheckAsync();
            CountryText.Text = status.Country != null ? $"{CountryName(status.Country)} ({status.Country})" : "Нет ответа";
            CountryText.Foreground = status.Country != null ? (Brush)FindResource("TextBrush") : Red;
            IpText.Text = status.Ip ?? "—";
            SetCheck(SiteText, SiteDot, status.SiteOk);
            SetCheck(AntiCheatText, AntiCheatDot, status.AntiCheatOk);
            CrossFade(CountryText);
            CrossFade(IpText);
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    private static void SetCheck(System.Windows.Controls.TextBlock text, System.Windows.Shapes.Ellipse dot, bool ok)
    {
        text.Text = ok ? "Доступен" : "Недоступен";
        text.Foreground = ok ? (Brush)Application.Current.FindResource("TextBrush") : Red;
        dot.Fill = ok ? Green : Red;
        CrossFade(text);
    }

    private static string CountryName(string code) => code switch
    {
        "RU" => "Россия",
        "KZ" => "Казахстан",
        "US" => "США",
        "DE" => "Германия",
        "NL" => "Нидерланды",
        "FI" => "Финляндия",
        "SE" => "Швеция",
        "PL" => "Польша",
        "BY" => "Беларусь",
        "UA" => "Украина",
        _ => code,
    };

    // ---------- Режим и серверы ----------

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        _settings.Mode = ModeDirect.IsChecked == true ? TunnelMode.Direct : TunnelMode.Vpn;
        _settings.Save();
        UpdateModeUi();
        ReconnectIfConnected();
    }

    private void UpdateModeUi()
    {
        ServerCard.Opacity = _settings.Mode == TunnelMode.Vpn ? 1 : 0.45;
        ServerCard.IsHitTestVisible = _settings.Mode == TunnelMode.Vpn;
        UpdateConnectUi();
    }

    private void OnServerChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingUi || ServerCombo.SelectedItem is not ServerInfo server) return;
        _settings.SelectedServer = server.Key;
        _settings.Save();
        UpdateZapretHint();
        if (_settings.Mode == TunnelMode.Vpn) ReconnectIfConnected();
    }

    private void ReconnectIfConnected()
    {
        if (_tunnel.State != TunnelState.Connected) return;
        _tunnel.Stop();
        Connect();
    }

    private async void OnRefreshServersClick(object sender, RoutedEventArgs e) => await LoadServersAsync();

    /// <summary>Собирает серверы из всех подписок; одна сломанная подписка не мешает остальным.</summary>
    private async Task LoadServersAsync()
    {
        RefreshSubscriptionList();
        if (_settings.Subscriptions.Count == 0)
        {
            _servers = new List<ServerInfo>();
            ServerCombo.ItemsSource = _servers;
            ServerHint.Text = "Добавьте подписку или ключ на вкладке «Настройки».";
            UpdateZapretHint();
            return;
        }

        RefreshButton.IsEnabled = false;
        ServerHint.Text = "Загружаю подписки…";
        var servers = new List<ServerInfo>();
        var failed = new List<string>();
        foreach (var entry in _settings.Subscriptions)
        {
            try
            {
                var loaded = await Subscription.LoadAsync(entry.Source);
                servers.AddRange(loaded.Select(s => s with { Group = entry.Name }));
            }
            catch (Exception)
            {
                failed.Add(entry.Name);
            }
        }

        _servers = servers;
        _loadingUi = true;
        ServerCombo.ItemsSource = _servers;
        // Старые настройки хранили только имя сервера, поэтому ищем и по нему.
        ServerCombo.SelectedItem = _servers.FirstOrDefault(s => s.Key == _settings.SelectedServer)
            ?? _servers.FirstOrDefault(s => s.Name == _settings.SelectedServer)
            ?? _servers.FirstOrDefault();
        _loadingUi = false;
        if (ServerCombo.SelectedItem is ServerInfo selected && selected.Key != _settings.SelectedServer)
        {
            _settings.SelectedServer = selected.Key;
            _settings.Save();
        }

        var hint = _servers.Count == 0
            ? "В подписках нет серверов VLESS."
            : $"Серверов: {_servers.Count} из подписок: {_settings.Subscriptions.Count}. Лучше выбрать ближайший к вам.";
        if (failed.Count > 0) hint += $"\nНе загрузилось: {string.Join(", ", failed)}.";
        ServerHint.Text = hint;
        RefreshButton.IsEnabled = true;
        UpdateZapretHint();
    }

    private void UpdateZapretHint()
    {
        var server = ServerCombo.SelectedItem as ServerInfo;
        // Пустой адрес Dns.GetHostAddresses превращает в адреса этого компьютера — такой подсказке верить нельзя.
        var vpnPart = string.IsNullOrWhiteSpace(server?.Server)
            ? "В режиме VPN добавьте IP своего VPN-сервера в lists\\ipset-exclude.txt."
            : $"В режиме VPN добавьте {ResolveIp(server!.Server)} в lists\\ipset-exclude.txt и перезапустите zapret.";
        ZapretHint.Text = vpnPart + "\nFaceit не должен быть в списках zapret (list-general.txt, ipset-all.txt) — им занимается Faceit Tunnel.";
    }

    private static string ResolveIp(string host)
    {
        if (IPAddress.TryParse(host, out _)) return host;
        try
        {
            return Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString() ?? host;
        }
        catch (Exception)
        {
            return host;
        }
    }

    // ---------- Настройки ----------

    private void RefreshSubscriptionList()
    {
        SubscriptionList.ItemsSource = _settings.Subscriptions
            .Select(e => new SubscriptionRow(e, e.Name, Subscription.Describe(e.Source)))
            .ToList();
        SubscriptionsEmpty.Visibility = _settings.Subscriptions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnAddSubscriptionClick(object sender, RoutedEventArgs e)
    {
        var source = NewSubscriptionLink.Password.Trim();
        if (!Regex.IsMatch(source, "^(https?|vless)://", RegexOptions.IgnoreCase))
        {
            ShowAddError("Вставьте ссылку, которая начинается с https:// или vless://.");
            return;
        }
        if (_settings.Subscriptions.Any(s => s.Source == source))
        {
            ShowAddError("Эта подписка уже добавлена.");
            return;
        }

        AddSubscriptionButton.IsEnabled = false;
        AddSubscriptionHint.Text = "Проверяю ссылку…";
        AddSubscriptionHint.Foreground = Gray;
        try
        {
            // Сначала убеждаемся, что ссылка рабочая, и только потом сохраняем.
            var servers = await Subscription.LoadAsync(source);
            if (servers.Count == 0)
            {
                ShowAddError("По ссылке нет серверов VLESS.");
                return;
            }

            var name = NewSubscriptionName.Text.Trim();
            if (name.Length == 0) name = Subscription.Describe(source);
            var baseName = name;
            for (var i = 2; _settings.Subscriptions.Any(s => s.Name == name); i++) name = $"{baseName} {i}";

            _settings.Subscriptions.Add(new SubscriptionEntry { Name = name, Source = source });
            _settings.Save();
            NewSubscriptionName.Text = "";
            NewSubscriptionLink.Password = "";
            AddSubscriptionHint.Text = $"Добавлено: {name}, серверов: {servers.Count}.";
            AddSubscriptionHint.Foreground = Green;
            await LoadServersAsync();
        }
        catch (Exception ex)
        {
            ShowAddError("Не удалось загрузить ссылку: " + ex.Message);
        }
        finally
        {
            AddSubscriptionButton.IsEnabled = true;
        }
    }

    private void ShowAddError(string message)
    {
        AddSubscriptionHint.Text = message;
        AddSubscriptionHint.Foreground = Red;
    }

    private async void OnRemoveSubscriptionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SubscriptionEntry entry) return;
        _settings.Subscriptions.Remove(entry);
        _settings.Save();
        AddSubscriptionHint.Text = $"Удалено: {entry.Name}.";
        AddSubscriptionHint.Foreground = Gray;
        await LoadServersAsync();
    }

    private void OnBehaviorChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        _settings.MinimizeToTray = TrayToggle.IsChecked == true;
        _settings.ConnectOnStart = AutoConnectToggle.IsChecked == true;
        _settings.Save();
    }

    private void OnOpenDataClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.DataDir) { UseShellExecute = true });

    // ---------- Журнал ----------

    private void AddLog(LogEntry entry)
    {
        var brush = entry.Kind switch
        {
            LogKind.Vpn => Orange,
            LogKind.Direct => Green,
            LogKind.Error => Red,
            _ => Gray,
        };
        _log.Insert(0, new LogItem(entry.Time.ToString("HH:mm:ss"), entry.Text, brush));
        while (_log.Count > MaxLogItems) _log.RemoveAt(_log.Count - 1);
        LogEmpty.Visibility = Visibility.Collapsed;
    }

    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        if (System.IO.File.Exists(AppPaths.LogFile))
            Process.Start(new ProcessStartInfo("notepad.exe", AppPaths.LogFile) { UseShellExecute = true });
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e)
    {
        _log.Clear();
        LogEmpty.Visibility = Visibility.Visible;
    }

    // ---------- Обновления ----------

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(silent: false);

    private async Task CheckUpdatesAsync(bool silent)
    {
        CheckUpdatesButton.IsEnabled = false;
        try
        {
            if (!_updater.IsInstalled)
            {
                if (!silent) VersionText.Text = $"Faceit Tunnel {_updater.CurrentVersion} · обновления работают только в установленной версии";
                return;
            }
            var version = await _updater.CheckAsync();
            if (version != null)
            {
                UpdateText.Text = $"Доступна новая версия {version}.";
                UpdateBanner.Visibility = Visibility.Visible;
            }
            else if (!silent)
            {
                VersionText.Text = $"Faceit Tunnel {_updater.CurrentVersion} · установлена последняя версия";
            }
        }
        catch (Exception ex)
        {
            if (!silent) VersionText.Text = "Не удалось проверить обновления: " + ex.Message;
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        try
        {
            _tunnel.Stop();
            await _updater.DownloadAndRestartAsync(p => Dispatcher.Invoke(() => UpdateText.Text = $"Скачиваю обновление… {p}%"));
        }
        catch (Exception ex)
        {
            UpdateText.Text = "Не удалось обновиться: " + ex.Message;
            UpdateButton.IsEnabled = true;
        }
    }

    // ---------- Трей и закрытие ----------

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => ShowFromTray());
        _trayConnectItem = new Forms.ToolStripMenuItem("Подключить", null, (_, _) => Dispatcher.Invoke(ToggleConnection));
        menu.Items.Add(_trayConnectItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(ExitApp));

        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = "Faceit Tunnel",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private void ShowFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        });
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == NativeMethods.ShowMessage)
        {
            ShowFromTray();
            handled = true;
        }
        return IntPtr.Zero;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && _settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown && _tray != null)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(3000, "Faceit Tunnel работает в трее",
                    "Туннель не отключён. Выход — правой кнопкой по значку.", Forms.ToolTipIcon.Info);
            }
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _tunnel.Dispose();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        Application.Current.Shutdown();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
