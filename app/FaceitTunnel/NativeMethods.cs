using System;
using System.Runtime.InteropServices;

namespace FaceitTunnel;

internal static class NativeMethods
{
    // Второй запуск рассылает это сообщение всем окнам; уже работающая копия по нему выходит из трея.
    public const string ShowMessageName = "FaceitTunnel.ShowWindow";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, IntPtr changeInfo);

    private const uint MsgfltAllow = 1;
    private static readonly IntPtr Broadcast = new(0xffff);

    public static uint ShowMessage { get; } = RegisterWindowMessage(ShowMessageName);

    /// <summary>Окно работает с правами администратора; без этого Windows не пропустит к нему сообщение от обычного процесса.</summary>
    public static void AllowShowMessage(IntPtr hwnd) => ChangeWindowMessageFilterEx(hwnd, ShowMessage, MsgfltAllow, IntPtr.Zero);

    public static void ShowExistingWindow() => PostMessage(Broadcast, ShowMessage, IntPtr.Zero, IntPtr.Zero);
}
