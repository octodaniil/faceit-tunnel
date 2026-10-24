using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using Velopack;

namespace FaceitTunnel;

public static class Program
{
    private const string MutexName = @"Global\FaceitTunnel.SingleInstance";

    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack должен отработать первым: при установке, обновлении и удалении
        // он вызывает exe со служебными аргументами (без прав администратора) и сам завершает процесс.
        VelopackApp.Build().Run();

        if (DemoMode)
        {
            RunApp();
            return;
        }

        // Уже запущено — просто показываем окно, а не плодим второй туннель.
        if (InstanceExists())
        {
            NativeMethods.ShowExistingWindow();
            return;
        }

        // TUN-адаптер создаётся только с правами администратора — перезапускаемся с ними (окно UAC).
        if (!IsAdministrator())
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            }
            catch (Win32Exception)
            {
                // Пользователь отказался в окне UAC — без прав работать нечем.
            }
            return;
        }

        using var mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            NativeMethods.ShowExistingWindow();
            return;
        }

        RunApp();
    }

    private static void RunApp()
    {
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>Отладочный показ интерфейса без туннеля и UAC (только в Debug-сборке).</summary>
#if DEBUG
    public static string? Demo { get; } = Environment.GetEnvironmentVariable("FT_DEMO");
#else
    public static string? Demo => null;
#endif
    private static bool DemoMode => Demo != null;

    private static bool InstanceExists()
    {
        try
        {
            if (!Mutex.TryOpenExisting(MutexName, out var existing)) return false;
            existing.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Мьютекс создан копией с правами администратора — открыть нельзя, но он есть.
            return true;
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
