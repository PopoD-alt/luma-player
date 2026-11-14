using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Luma.WindowsApp;
internal static class NativeWindow
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    public static void Style(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle; if (hwnd == 0) return;
        var mode = dark ? 1 : 0; _ = DwmSetWindowAttribute(hwnd, 20, ref mode, sizeof(int));
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        { var mica = 2; _ = DwmSetWindowAttribute(hwnd, 38, ref mica, sizeof(int)); }
    }
    public static void FillMonitor(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor))
        {
            var r = monitor.Monitor;
            _ = SetWindowPos(hwnd, new nint(-1), r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, 0x0040 | 0x0020);
        }
        else window.WindowState = WindowState.Maximized;
    }
}
