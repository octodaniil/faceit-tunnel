using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace FaceitTunnel;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Оранжевый акцент вместо системного цвета Windows.
        ApplicationAccentColorManager.Apply(Color.FromRgb(0xFF, 0x6A, 0x13), ApplicationTheme.Dark);
        var window = new MainWindow();
        window.Show();
    }
}
