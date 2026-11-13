using System.Runtime.InteropServices;

namespace Congee;

static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000,
                     WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    public const uint GW_HWNDPREV = 3;
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int idx);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int idx, int value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("kernel32.dll")] public static extern bool AttachConsole(int pid);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hMon, int type, out uint dpiX, out uint dpiY);

    /// <summary>Work areas (excluding the taskbar) of each monitor in physical pixels, primary first, with DPI scale.</summary>
    public static List<(RECT work, double scale)> MonitorWorkAreas()
    {
        var found = new List<(bool primary, RECT work, double scale)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(h, ref mi))
            {
                double scale = GetDpiForMonitor(h, 0, out uint dx, out _) == 0 ? dx / 96.0 : 1.0;
                found.Add(((mi.dwFlags & 1) != 0, mi.rcWork, scale));
            }
            return true;
        }, IntPtr.Zero);
        return found.OrderBy(f => f.primary ? 0 : 1).Select(f => (f.work, f.scale)).ToList();
    }
}
