using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Congee;

/// <summary>
/// Notification-area (systray) icon with a native right-click menu. Uses Shell_NotifyIcon
/// directly instead of WinForms' NotifyIcon so the app stays WPF-only. The icon is re-added
/// if Explorer restarts.
/// </summary>
sealed class TrayIcon : IDisposable
{
    public sealed record Item(string Text, Action? Action, Func<string>? DynamicText = null, Func<bool>? Checked = null);  // Action null = separator

    const int WM_TRAY = 0x8000 + 1, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_NULL = 0;
    const uint NIM_ADD = 0, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    const uint MF_STRING = 0, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800, TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80;

    readonly HwndSource source;
    readonly uint taskbarCreated;
    readonly List<Item> items;
    readonly string tip;
    IntPtr hIcon;
    bool added;

    public TrayIcon(string tip, List<Item> items)
    {
        this.tip = tip;
        this.items = items;
        // An invisible top-level window (not message-only, so it still gets the TaskbarCreated broadcast)
        source = new HwndSource(new HwndSourceParameters("congee tray") { Width = 0, Height = 0, WindowStyle = 0 });
        source.AddHook(WndProc);
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        // Follow the Windows light/dark app setting for the popup menu (uxtheme ordinal 135,
        // SetPreferredAppMode(AllowDark); used by Explorer, Notepad++, etc. Ignored if missing).
        try { SetPreferredAppMode(1); FlushMenuThemes(); } catch (Exception) { }
        hIcon = LoadAppIcon();
        Add();
    }

    static IntPtr LoadAppIcon()
    {
        int size = GetSystemMetricsForDpi(49 /* SM_CXSMICON */, GetDpiForSystem());
        if (Environment.ProcessPath is { } exe &&
            PrivateExtractIcons(exe, 0, size, size, out var h, IntPtr.Zero, 1, 0) == 1 && h != IntPtr.Zero)
            return h;
        return LoadIcon(IntPtr.Zero, (IntPtr)32512 /* IDI_APPLICATION */);
    }

    NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = source.Handle,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_TRAY,
        hIcon = hIcon,
        szTip = tip,
    };

    void Add()
    {
        var d = Data();
        added = Shell_NotifyIcon(NIM_ADD, ref d);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAY)
        {
            int ev = (int)(lParam.ToInt64() & 0xFFFF);
            if (ev is WM_RBUTTONUP or WM_LBUTTONUP) { ShowMenu(); handled = true; }
        }
        else if (msg == (int)taskbarCreated)
        {
            Add();  // Explorer restarted: the old icon is gone
        }
        else if (msg == 0x0010 /* WM_CLOSE */)
        {
            // Setup, uninstall and stop-congee.ps1 close congee this way (then WM_DESTROY follows)
            Log.StopReason("asked to close by another program (setup, uninstall or stop-congee.ps1)");
        }
        else if (msg == 0x0002 /* WM_DESTROY */)
        {
            Remove();  // e.g. taskkill sends WM_CLOSE to this window too: don't leave a dead icon
            System.Windows.Application.Current?.Shutdown();
        }
        return IntPtr.Zero;
    }

    void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.Action == null) AppendMenu(menu, MF_SEPARATOR, 0, null);
            else AppendMenu(menu, MF_STRING | (it.Checked?.Invoke() == true ? MF_CHECKED : 0), (UIntPtr)(i + 1), it.DynamicText?.Invoke() ?? it.Text);
        }
        GetCursorPos(out var pt);
        SetForegroundWindow(source.Handle);  // otherwise the menu won't close when clicking elsewhere
        int cmd = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, pt.X, pt.Y, source.Handle, IntPtr.Zero);
        PostMessage(source.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd > 0 && cmd <= items.Count) items[cmd - 1].Action?.Invoke();
    }

    void Remove()
    {
        if (!added) return;
        var d = Data();
        Shell_NotifyIcon(NIM_DELETE, ref d);
        added = false;
    }

    public void Dispose()
    {
        Remove();
        if (hIcon != IntPtr.Zero) { DestroyIcon(hIcon); hIcon = IntPtr.Zero; }
        source.Dispose();
    }

    // ---- interop ---------------------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int PrivateExtractIcons(string file, int index, int cx, int cy, out IntPtr icon, IntPtr ids, uint count, uint flags);
    [DllImport("user32.dll")] static extern IntPtr LoadIcon(IntPtr inst, IntPtr name);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
}
