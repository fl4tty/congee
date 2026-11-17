using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Congee;

/// <summary>
/// Finds and starts LibreHardwareMonitor, which congee needs for temperatures, fans and GPU
/// stats. It must run as administrator to read the sensors, so starting it shows Windows' admin
/// prompt. Whether congee offers to start it is the start_lhm setting: ask, yes or no.
/// </summary>
static class Lhm
{
    const string ExeName = "LibreHardwareMonitor.exe";

    // CONGEE_TEST_LHM_PROCESS=<name> makes congee treat that process as LHM, to test start/stop prompts
    // without touching the real one.
    static string ProcName => Environment.GetEnvironmentVariable("CONGEE_TEST_LHM_PROCESS") is { Length: > 0 } n ? n : "LibreHardwareMonitor";

    public static bool IsRunning() => Process.GetProcessesByName(ProcName).Length > 0;

    /// <summary>LibreHardwareMonitor.exe from a winget install (per user or machine), Program Files,
    /// or wherever Windows' installed-apps list says it is; null if it isn't installed.</summary>
    public static string? FindExe()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (string packages in new[] { Path.Combine(local, @"Microsoft\WinGet\Packages"), Path.Combine(pf, @"WinGet\Packages") })
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(packages, "LibreHardwareMonitor.LibreHardwareMonitor_*"))
                    if (File.Exists(Path.Combine(dir, ExeName))) return Path.Combine(dir, ExeName);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (File.Exists(Path.Combine(pf, "LibreHardwareMonitor", ExeName))) return Path.Combine(pf, "LibreHardwareMonitor", ExeName);
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var un = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (un == null) continue;
                foreach (string name in un.GetSubKeyNames())
                {
                    using var k = un.OpenSubKey(name);
                    if (k?.GetValue("DisplayName") is string dn && dn.StartsWith("LibreHardwareMonitor", StringComparison.OrdinalIgnoreCase) &&
                        k.GetValue("InstallLocation") is string loc && File.Exists(Path.Combine(loc, ExeName)))
                        return Path.Combine(loc, ExeName);
                }
            }
            catch (System.Security.SecurityException) { }
        }
        return null;
    }

    /// <summary>Starts it as administrator (Windows asks for permission). False if that was declined or failed.</summary>
    public static bool Start(string exe)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(exe) });
            Log.Write("started LibreHardwareMonitor");
            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)  // ERROR_CANCELLED: "No" on the admin prompt
        {
            Log.Write("LibreHardwareMonitor not started: the admin prompt was declined");
        }
        catch (Exception e)
        {
            Log.Write("couldn't start LibreHardwareMonitor: " + e.Message);
        }
        return false;
    }

    /// <summary>True when sensors_url points at this PC (otherwise LHM runs somewhere else).</summary>
    public static bool IsLocal(string sensorsUrl) =>
        Uri.TryCreate(sensorsUrl.Trim(), UriKind.Absolute, out var u) &&
        (u.IsLoopback || u.Host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase));

    /// <summary>"Start LibreHardwareMonitor?" dialog. Returns (start, remember).</summary>
    public static (bool start, bool remember) Ask() => Dialog(
        "Start LibreHardwareMonitor?",
        "LibreHardwareMonitor isn't running. congee needs it for temperatures, fan speeds, and GPU load, " +
        "clocks, power and memory. Everything else works without it.\n\n" +
        "It needs administrator rights to read the hardware sensors, so Windows will ask for permission.",
        "Start it");

    // ---- stopping it -------------------------------------------------------------------------

    /// <summary>"Stop LibreHardwareMonitor too?" dialog, shown on Exit congee. Returns (stop, remember).</summary>
    public static (bool stop, bool remember) AskStop() => Dialog(
        "Stop LibreHardwareMonitor too?",
        "congee is closing. LibreHardwareMonitor is still running in the background to read temperatures, fans " +
        "and GPU stats. Stop it as well?\n\n" +
        "Keep it running if other apps use it. If Windows doesn't let congee stop it, Windows asks for " +
        "administrator permission.",
        "Stop it", "Keep it running");

    /// <summary>Ends every LibreHardwareMonitor process. It closes to the tray instead of exiting when
    /// asked nicely, so it is ended directly; if Windows won't allow that (it runs as administrator),
    /// taskkill is run as administrator, which shows the admin prompt. False if it's still running.</summary>
    public static bool Stop()
    {
        bool denied = false;
        foreach (var p in Process.GetProcessesByName(ProcName))
        {
            try { p.Kill(); p.WaitForExit(3000); }
            catch (Win32Exception) { denied = true; }
            catch (InvalidOperationException) { }  // already gone
            finally { p.Dispose(); }
        }
        if (denied)
        {
            try
            {
                using var tk = Process.Start(new ProcessStartInfo("taskkill.exe", "/F /IM " + ProcName + ".exe")
                    { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
                tk?.WaitForExit(10000);
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223)
            {
                Log.Write("LibreHardwareMonitor not stopped: the admin prompt was declined");
                return false;
            }
            catch (Exception e)
            {
                Log.Write("couldn't stop LibreHardwareMonitor: " + e.Message);
                return false;
            }
        }
        bool stopped = !IsRunning();
        Log.Write(stopped ? "stopped LibreHardwareMonitor" : "couldn't stop LibreHardwareMonitor");
        return stopped;
    }

    // ---- installing it ----------------------------------------------------------------------

    public const string WingetId = "LibreHardwareMonitor.LibreHardwareMonitor";
    public const string ReleasesUrl = "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases";
    const string WingetArgs = "install --id " + WingetId + " -e --source winget --accept-package-agreements " +
                              "--accept-source-agreements --disable-interactivity";

    /// <summary>winget.exe (the App Installer alias), or null if it isn't there.</summary>
    public static string? WingetExe()
    {
        string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
        return File.Exists(p) ? p : null;
    }

    /// <summary>"Install LibreHardwareMonitor?" dialog. Returns (install, remember). Without winget
    /// the yes button opens LHM's download page instead.</summary>
    public static (bool install, bool remember) AskInstall(bool haveWinget) => Dialog(
        "Install LibreHardwareMonitor?",
        "LibreHardwareMonitor isn't installed. congee needs it for temperatures, fan speeds, and GPU load, " +
        "clocks, power and memory. Everything else works without it. It's free and open source.\n\n" +
        (haveWinget
            ? "congee can install it for you with winget (Windows' package manager) and set it up to share its " +
              "sensors with congee. It needs administrator rights to read the hardware sensors, so Windows will " +
              "ask for permission when it starts."
            : "winget (Windows' package manager) isn't available to install it automatically. You can download it " +
              "from its GitHub releases page, unzip it, and run LibreHardwareMonitor.exe as administrator. Turn on " +
              "Options > Remote Web Server > Run so congee can read it."),
        haveWinget ? "Install" : "Open download page");

    /// <summary>Installs LHM with winget, hidden, while a small "Installing..." window shows.
    /// Returns the exe, or null after showing why it failed.</summary>
    public static async Task<string?> InstallAsync(string winget)
    {
        Log.Write("installing LibreHardwareMonitor with winget");
        var progress = new Window
        {
            Title = "congee",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
        };
        var pp = new StackPanel { Margin = new Thickness(20), Width = 360 };
        pp.Children.Add(new TextBlock { Text = "Installing LibreHardwareMonitor...", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        pp.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 14 });
        pp.Children.Add(new TextBlock { Text = "This usually takes under a minute.", Margin = new Thickness(0, 8, 0, 0), Foreground = System.Windows.Media.Brushes.Gray });
        progress.Content = pp;
        progress.Show();

        int code = -1;
        string output = "";
        try
        {
            (code, output) = await Task.Run(() =>
            {
                var psi = new ProcessStartInfo(winget, WingetArgs)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi)!;
                var err = p.StandardError.ReadToEndAsync();
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return (p.ExitCode, o + err.Result);
            });
        }
        catch (Exception e) { output = e.Message; }
        progress.Close();

        // 0 = installed; these two mean it was there already, which is fine too.
        bool ok = code is 0 or unchecked((int)0x8A15002B) /* no upgrade available */ or unchecked((int)0x8A150061) /* already installed */;
        string? exe = FindExe();
        if (ok && exe != null)
        {
            Log.Write("installed LibreHardwareMonitor: " + exe);
            EnsureWebServerConfig(exe);
            if (code == 0) RecordInstalled();  // newly installed (not "already there"): uninstall offers to remove it
            return exe;
        }
        string why = ok ? "winget finished, but LibreHardwareMonitor.exe couldn't be found afterwards."
                        : $"winget failed (exit code 0x{code:X8}).";
        Log.Write("LibreHardwareMonitor install failed: " + why + " " + LastLine(output));
        MessageBox.Show(
            "Couldn't install LibreHardwareMonitor. " + why + "\n\n" + LastLine(output) +
            "\n\nYou can install it yourself from a terminal:\n    winget install --id " + WingetId +
            "\nor download it from:\n    " + ReleasesUrl,
            "congee", MessageBoxButton.OK, MessageBoxImage.Warning);
        return null;
    }

    /// <summary>Notes that congee installed LHM, where congee's uninstaller looks (same key setup uses).</summary>
    static void RecordInstalled()
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\congee\InstalledDeps");
            k.SetValue(WingetId, "LibreHardwareMonitor");
        }
        catch (Exception e) { Log.Write("couldn't record the LibreHardwareMonitor install: " + e.Message); }
    }

    static string LastLine(string output) =>
        output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l.Any(char.IsLetter)).LastOrDefault() ?? "";

    /// <summary>Writes LibreHardwareMonitor.config next to the exe, if there isn't one, so it runs
    /// its web server on port 8085 (what congee reads) and starts/closes to the tray. Same as setup.</summary>
    public static void EnsureWebServerConfig(string exe)
    {
        string cfg = Path.Combine(Path.GetDirectoryName(exe)!, "LibreHardwareMonitor.config");
        if (File.Exists(cfg)) return;
        try
        {
            File.WriteAllText(cfg,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                "<configuration>\r\n" +
                "  <appSettings>\r\n" +
                "    <add key=\"runWebServerMenuItem\" value=\"true\" />\r\n" +
                "    <add key=\"listenerPort\" value=\"8085\" />\r\n" +
                "    <add key=\"startMinMenuItem\" value=\"true\" />\r\n" +
                "    <add key=\"minTrayMenuItem\" value=\"true\" />\r\n" +
                "    <add key=\"minCloseMenuItem\" value=\"true\" />\r\n" +
                "  </appSettings>\r\n" +
                "</configuration>\r\n");
            Log.Write("wrote " + cfg + " (web server on port 8085)");
        }
        catch (Exception e) { Log.Write("couldn't write " + cfg + ": " + e.Message); }
    }

    // ---- dialog -------------------------------------------------------------------------------

    /// <summary>Heading, text, "Remember my choice", and yes / no buttons. Returns (yes, remember).</summary>
    static (bool yes, bool remember) Dialog(string heading, string text, string yesLabel, string noLabel = "Not now")
    {
        var win = new Window
        {
            Title = "congee",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ShowInTaskbar = true,
        };
        bool chosen = false;
        var remember = new CheckBox { Content = "Remember my choice", Margin = new Thickness(0, 14, 0, 0) };
        var yes = new Button { Content = yesLabel, IsDefault = true, MinWidth = 90, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        var no = new Button { Content = noLabel, IsCancel = true, MinWidth = 90, Padding = new Thickness(10, 3, 10, 3) };
        yes.Click += (_, _) => { chosen = true; win.Close(); };
        no.Click += (_, _) => win.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        var panel = new StackPanel { Margin = new Thickness(20), MaxWidth = 440 };
        panel.Children.Add(new TextBlock { Text = heading, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = text });
        panel.Children.Add(remember);
        panel.Children.Add(buttons);
        win.Content = panel;
        win.ShowDialog();
        return (chosen, remember.IsChecked == true);
    }
}
