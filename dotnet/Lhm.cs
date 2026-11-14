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

    public static bool IsRunning() => Process.GetProcessesByName("LibreHardwareMonitor").Length > 0;

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
    public static (bool start, bool remember) Ask()
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
        bool start = false;
        var remember = new CheckBox { Content = "Remember my choice", Margin = new Thickness(0, 14, 0, 0) };
        var yes = new Button { Content = "Start it", IsDefault = true, MinWidth = 90, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        var no = new Button { Content = "Not now", IsCancel = true, MinWidth = 90, Padding = new Thickness(10, 3, 10, 3) };
        yes.Click += (_, _) => { start = true; win.Close(); };
        no.Click += (_, _) => win.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        var panel = new StackPanel { Margin = new Thickness(20), MaxWidth = 440 };
        panel.Children.Add(new TextBlock { Text = "Start LibreHardwareMonitor?", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "LibreHardwareMonitor isn't running. congee needs it for temperatures, fan speeds, and GPU load, " +
                   "clocks, power and memory. Everything else works without it.\n\n" +
                   "It needs administrator rights to read the hardware sensors, so Windows will ask for permission.",
        });
        panel.Children.Add(remember);
        panel.Children.Add(buttons);
        win.Content = panel;
        win.ShowDialog();
        return (start, remember.IsChecked == true);
    }
}
