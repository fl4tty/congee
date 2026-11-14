using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Congee;

/// <summary>
/// congee.log next to the user's config (%APPDATA%\congee): starts and stops with the reason,
/// crashes, sleep/wake, lock/unlock and display errors, plus an hourly "still running" line, so
/// that after congee disappears you can tell what happened and roughly when. Both editions write
/// the same file (lines are tagged with the edition and process id). Kept under ~1 MB: the older
/// half is moved to congee.log.1. Logging never throws.
/// </summary>
static class Log
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "congee", "congee.log");
    const long MaxBytes = 1 << 20;
    const string StopMarker = "stopped: ";
    static readonly object gate = new();
    static readonly int pid = Environment.ProcessId;
    static readonly HashSet<string> seen = [];
    static string? stopReason;
    static Timer? heartbeat;
    static readonly DateTime started = DateTime.Now;

    public static void Write(string message)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [.NET {pid}] {message}{Environment.NewLine}");
            }
            catch { }
        }
    }

    /// <summary>Logs a message once per run (for errors that would otherwise repeat every tick).</summary>
    public static void Once(string message)
    {
        lock (seen) { if (!seen.Add(message)) return; }
        Write(message);
    }

    /// <summary>Remembers why congee is about to stop; the first reason given wins.</summary>
    public static void StopReason(string reason) => Interlocked.CompareExchange(ref stopReason, reason, null);

    public static void Started(string configPath)
    {
        ReportUncleanPreviousRun();
        string version = typeof(Log).Assembly.GetName().Version?.ToString(3) ?? "?";
        Write($"started: congee {version}, config {configPath}, Windows {Environment.OSVersion.Version}");
        heartbeat = new Timer(_ => Write($"still running (up {Uptime()})"), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            StopReason("crashed");
            Write("CRASH: " + e.ExceptionObject);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => Once("background error: " + e.Exception.GetBaseException().Message);
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Suspend) Write("PC going to sleep");
            else if (e.Mode == Microsoft.Win32.PowerModes.Resume) Write("PC woke up");
        };
        Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock) Write("screen locked");
            else if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock) Write("screen unlocked");
        };
    }

    static int stoppedLogged;

    /// <summary>Writes the "stopped:" line (once; on sign-out it's written early, as Windows may
    /// end the process before Main returns).</summary>
    public static void Stopped(int exitCode)
    {
        if (Interlocked.Exchange(ref stoppedLogged, 1) == 1) return;
        heartbeat?.Dispose();
        Write($"{StopMarker}{stopReason ?? "widget window closed"} (exit code {exitCode}, up {Uptime()})");
    }

    static string Uptime()
    {
        var t = DateTime.Now - started;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s";
    }

    /// <summary>If the last run's final line isn't a "stopped:" line, it was killed or the PC lost
    /// power: say so, with its last sign of life. Skipped when that process is still running.</summary>
    static void ReportUncleanPreviousRun()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            string last = "";
            using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(Math.Max(0, fs.Length - 4096), SeekOrigin.Begin);
                using var sr = new StreamReader(fs);
                foreach (var line in sr.ReadToEnd().Split('\n'))
                    if (line.Trim().Length > 0) last = line.Trim();
            }
            var m = Regex.Match(last, @"^(\S+ \S+) \[(\S+) (\d+)\] (.*)$");
            if (!m.Success || m.Groups[4].Value.StartsWith(StopMarker)) return;
            int prev = int.Parse(m.Groups[3].Value);
            try
            {
                using var p = Process.GetProcessById(prev);  // still running (e.g. the other edition), unless the id was reused
                if (!p.HasExited && (p.ProcessName.Equals("congee", StringComparison.OrdinalIgnoreCase) ||
                                     p.ProcessName.StartsWith("python", StringComparison.OrdinalIgnoreCase))) return;
            }
            catch (ArgumentException) { }
            Write($"previous run ({m.Groups[2].Value} {prev}) ended without logging a stop: it was killed (e.g. End task) " +
                  $"or the PC lost power. Its last entry was at {m.Groups[1].Value}: {m.Groups[4].Value}");
        }
        catch { }
    }
}
