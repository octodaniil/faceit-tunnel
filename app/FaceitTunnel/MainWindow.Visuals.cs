using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FaceitTunnel.Core;

namespace FaceitTunnel;

/// <summary>Анимации: смена состояния, полоса подключения, вкладки, появление окна.</summary>
public partial class MainWindow
{
    private static readonly Color AccentColor = Color.FromRgb(0xFF, 0x5A, 0x1F);
    private static readonly Color AmberColor = Color.FromRgb(0xF5, 0xA5, 0x24);
    private static readonly Color GreenColor = Color.FromRgb(0x3D, 0xD6, 0x8C);
    private static readonly Color IdleColor = Color.FromRgb(0x6B, 0x6B, 0x76);
    private static readonly Color TextColor = Color.FromRgb(0xED, 0xED, 0xEF);
    private static readonly Color SurfaceColor = Color.FromRgb(0x1B, 0x1B, 0x1F);

    private static readonly Duration Fade = TimeSpan.FromMilliseconds(350);
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    // Не замороженные кисти: их цвет плавно меняется вместе с состоянием.
    private readonly SolidColorBrush _dotBrush = new(IdleColor);
    private readonly SolidColorBrush _statusBrush = new(TextColor);
    private readonly SolidColorBrush _buttonBrush = new(AccentColor);

    private int _page;
    private TunnelState? _shownState;

    private void InitVisuals()
    {
        StatusDot.Fill = _dotBrush;
        StatusText.Foreground = _statusBrush;
        ConnectButton.Background = _buttonBrush;

        Root.Opacity = 0;
        Loaded += (_, _) =>
        {
            // Фокус на строке режима при запуске прокручивает страницу — возвращаем её наверх.
            Dispatcher.BeginInvoke(PageHome.ScrollToTop, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)));
            ContentT.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(500)) { EasingFunction = Ease });
        };
        NavGrid.SizeChanged += (_, _) => MoveNavIndicator(animate: false);
    }

    private void SetStateVisuals(TunnelState state)
    {
        if (_shownState == state) return;
        _shownState = state;

        var (dot, text, button, chip, buttonBorder) = state switch
        {
            TunnelState.Connecting => (AmberColor, TextColor, SurfaceColor, "Подключение", true),
            TunnelState.Connected => (GreenColor, GreenColor, SurfaceColor, "Подключено", true),
            _ => (IdleColor, TextColor, AccentColor, "Не подключено", false),
        };

        FadeColor(_dotBrush, dot);
        FadeColor(_statusBrush, text);
        FadeColor(_buttonBrush, button);
        // Подключено: кнопка становится спокойной, «вторичной», с тонкой рамкой.
        ConnectButton.BorderThickness = new Thickness(buttonBorder ? 1 : 0);
        ConnectButton.BorderBrush = (Brush)FindResource("LineBrush");

        StatusChipText.Text = chip;

        // Мягкое «дыхание» точки статуса, пока туннель работает.
        StatusDot.BeginAnimation(OpacityProperty, state == TunnelState.Disconnected
            ? new DoubleAnimation(1, TimeSpan.FromMilliseconds(200))
            : new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(state == TunnelState.Connecting ? 0.6 : 1.6))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });

        SetProgress(state == TunnelState.Connecting);
        CrossFade(StatusText);
        CrossFade(ConnectLabel);
    }

    /// <summary>Тонкая бегущая полоса под статусом, пока идёт подключение.</summary>
    private void SetProgress(bool running)
    {
        ProgressTrack.BeginAnimation(OpacityProperty, new DoubleAnimation(running ? 1 : 0, TimeSpan.FromMilliseconds(250)));
        if (running)
        {
            var width = Math.Max(ProgressTrack.ActualWidth, 300);
            ProgressT.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-ProgressBar.Width, width, TimeSpan.FromSeconds(1.1))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }
        else
        {
            ProgressT.BeginAnimation(TranslateTransform.XProperty, null);
        }
    }

    private static void FadeColor(SolidColorBrush brush, Color to) =>
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, Fade) { EasingFunction = Ease });

    /// <summary>Новый текст проявляется с лёгким подъёмом.</summary>
    private static void CrossFade(UIElement element)
    {
        if (element.RenderTransform is not TranslateTransform t)
        {
            t = new TranslateTransform();
            element.RenderTransform = t;
        }
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = Ease });
    }

    // ---------- Вкладки ----------

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        var index = sender == NavLog ? 1 : sender == NavSettings ? 2 : 0;
        if (index == _page) return;
        _page = index;

        var pages = new FrameworkElement[] { PageHome, PageLog, PageSettings };
        for (var i = 0; i < pages.Length; i++)
        {
            if (i != index)
            {
                pages[i].Visibility = Visibility.Collapsed;
                continue;
            }
            var page = pages[i];
            page.Visibility = Visibility.Visible;
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            if (page.RenderTransform is TranslateTransform t)
                t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = Ease });
        }
        MoveNavIndicator(animate: true);
    }

    private void MoveNavIndicator(bool animate)
    {
        var x = _page * NavGrid.ActualWidth / 3;
        if (animate)
        {
            NavIndicatorT.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(x, TimeSpan.FromMilliseconds(260)) { EasingFunction = Ease });
        }
        else
        {
            NavIndicatorT.BeginAnimation(TranslateTransform.XProperty, null);
            NavIndicatorT.X = x;
        }
    }
}
