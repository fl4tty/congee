using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Congee;

/// <summary>Installed display driver, read from the registry.</summary>
public sealed record GpuDriver(string Desc, string Provider, string Version, string Date, string RadeonVersion, string MatchingId)
{
    /// <summary>The version number people know: AMD Software (Adrenalin) version, NVIDIA's release
    /// number (Windows version 32.0.15.8157 -> 581.57), otherwise the Windows driver version.</summary>
    public string Release
    {
        get
        {
            if (RadeonVersion.Length > 0) return RadeonVersion;
            if (Provider.Contains("nvidia", StringComparison.OrdinalIgnoreCase) ||
                MatchingId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Version.Split('.');
                if (parts.Length == 4 && parts[2].All(char.IsAsciiDigit) && parts[3].All(char.IsAsciiDigit) && parts[2].Length > 0 && parts[3].Length > 0)
                {
                    string digits = parts[2] + parts[3].PadLeft(4, '0');
                    digits = digits[^5..];
                    return $"{int.Parse(digits[..3])}.{digits[3..]}";
                }
            }
            return Version;
        }
    }

    /// <summary>Registry dates are M-D-YYYY; show them as YYYY-MM-DD.</summary>
    public string IsoDate
    {
        get
        {
            var m = Regex.Match(Date, @"^(\d{1,2})-(\d{1,2})-(\d{4})$");
            return m.Success ? $"{m.Groups[3].Value}-{int.Parse(m.Groups[1].Value):00}-{int.Parse(m.Groups[2].Value):00}" : Date;
        }
    }
}

/// <summary>A drive letter: root ("C:\\"), kind (local, removable, network, optical, ram), volume
/// label (for mapped drives the \\server\share path).</summary>
public sealed record DriveEntry(string Root, string Kind, string Label);

public sealed record DiskUsage(long Total, long Free, long Avail);

/// <summary>Re-scans the drive letters every 5 s on a background thread, together with the space on
/// network and optical drives, so a slow or unreachable NAS never freezes the widget. Until the
/// first scan finishes, local drives are listed directly (fast).</summary>
public sealed class DriveScanner
{
    const long IntervalMs = 5000;
    volatile List<DriveEntry>? drives;
    volatile Dictionary<string, DiskUsage> usage = new(StringComparer.OrdinalIgnoreCase);
    int busy;
    long last = long.MinValue / 2;
    readonly ManualResetEventSlim scanned = new();  // set once the first full scan is done

    /// <summary>Start a scan if none ran yet and wait for it (for --once, which prints a single frame).</summary>
    public void Wait(TimeSpan timeout) { Get(); scanned.Wait(timeout); }

    public List<DriveEntry> Get()
    {
        if (Environment.TickCount64 - Interlocked.Read(ref last) >= IntervalMs && Interlocked.CompareExchange(ref busy, 1, 0) == 0)
            Task.Run(Scan);
        return drives ?? Hardware.ScanDrives(slow: false);
    }

    /// <summary>Space on a network/optical drive from the last background scan (never blocks).</summary>
    public DiskUsage? Usage(string root) => usage.GetValueOrDefault(root);

    void Scan()
    {
        try
        {
            var found = Hardware.ScanDrives(slow: true);
            var sizes = new Dictionary<string, DiskUsage>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in found)
                if (!Hardware.IsLocalKind(d.Kind) && Hardware.Usage(d.Root) is { } u)
                    sizes[d.Root] = u;
            usage = sizes;
            drives = found;
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref last, Environment.TickCount64);  // next scan 5 s after this one ends
            Interlocked.Exchange(ref busy, 0);
            scanned.Set();
        }
    }
}

/// <summary>Drive and display-driver discovery (the .NET twin of scan_drives / read_gpu_drivers).</summary>
public static class Hardware
{
    [DllImport("kernel32.dll")] static extern uint GetLogicalDrives();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetDriveType(string root);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetVolumeInformation(string root, StringBuilder label, int labelLen, out uint serial,
        IntPtr maxComponent, IntPtr flags, IntPtr fsName, int fsNameLen);
    [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

    static readonly Dictionary<uint, string> DriveKinds = new()
    {
        [2] = "removable", [3] = "local", [4] = "network", [5] = "optical", [6] = "ram",
    };
    public static bool IsLocalKind(string kind) => kind is "removable" or "local" or "ram";

    /// <summary>The kind (local, removable, network, optical, ram) of the drive a path is on, or null.</summary>
    public static string? DriveKind(string root) => DriveKinds.GetValueOrDefault(GetDriveType(root));

    /// <summary>(\\server\share, connected) for a mapped drive (SMB, NFS, WebDAV...). Windows also
    /// lists remembered mappings that aren't connected right now; asking those for volume info
    /// only times out after ~10 s, so ScanDrives skips that for them.</summary>
    public static (string unc, bool connected) NetworkPath(string letterColon)
    {
        var buf = new StringBuilder(1024);
        int len = buf.Capacity;
        int rc = WNetGetConnection(letterColon, buf, ref len);
        if (rc == 1201) return (buf.ToString(), false);  // ERROR_CONNECTION_UNAVAIL: remembered, not connected
        return (rc == 0 ? buf.ToString() : "", true);
    }

    /// <summary>Every usable drive letter. Mapped drives that are disconnected or unreachable,
    /// empty card readers and optical drives without a disc are left out. The same local volume under a
    /// second letter (subst, extra mount point) is listed once. slow=false leaves out network and
    /// optical drives entirely: they can take seconds to answer.</summary>
    public static List<DriveEntry> ScanDrives(bool slow)
    {
        var result = new List<DriveEntry>();
        var seen = new HashSet<uint>();
        uint mask = GetLogicalDrives();
        uint oldMode = SetErrorMode(0x0001);  // SEM_FAILCRITICALERRORS: no "insert a disk" dialogs
        try
        {
            for (int i = 0; i < 26; i++)
            {
                if ((mask >> i & 1) == 0) continue;
                string root = $"{(char)('A' + i)}:\\";
                if (DriveKind(root) is not { } kind || (!slow && !IsLocalKind(kind))) continue;
                var label = new StringBuilder(261);
                uint serial;
                if (kind == "network")
                {
                    var (unc, connected) = NetworkPath(root[..2]);
                    if (connected && GetVolumeInformation(root, label, label.Capacity, out serial, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
                        result.Add(new DriveEntry(root, kind, unc.Length > 0 ? unc : label.ToString()));
                    continue;  // disconnected or unreachable: not shown
                }
                if (!GetVolumeInformation(root, label, label.Capacity, out serial, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
                    continue;  // not ready
                if (IsLocalKind(kind) && serial != 0 && !seen.Add(serial)) continue;
                result.Add(new DriveEntry(root, kind, label.ToString()));
            }
        }
        finally { SetErrorMode(oldMode); }
        return result;
    }

    /// <summary>Size and free space of the drive a path is on, or null if it isn't ready.</summary>
    public static DiskUsage? Usage(string path)
    {
        try
        {
            var d = new DriveInfo(path);
            return d.IsReady ? new DiskUsage(d.TotalSize, d.TotalFreeSpace, d.AvailableFreeSpace) : null;
        }
        catch { return null; }
    }

    const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>Installed display drivers from the registry's display-adapter class key.
    /// Works for any vendor and needs no admin rights or LibreHardwareMonitor.</summary>
    public static List<GpuDriver> ReadGpuDrivers()
    {
        var result = new List<GpuDriver>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (cls == null) return result;
            foreach (var sub in cls.GetSubKeyNames())
            {
                if (!sub.All(char.IsAsciiDigit)) continue;  // 0000, 0001, ... (skips "Properties", "Configuration")
                try
                {
                    using var k = cls.OpenSubKey(sub);
                    if (k == null) continue;
                    string V(string n) => k.GetValue(n)?.ToString()?.Trim() ?? "";
                    var d = new GpuDriver(V("DriverDesc"), V("ProviderName"), V("DriverVersion"), V("DriverDate"),
                                          V("RadeonSoftwareVersion"), V("MatchingDeviceId"));
                    if (d.Desc.Length > 0 && d.Version.Length > 0) result.Add(d);
                }
                catch (System.Security.SecurityException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (System.Security.SecurityException) { }
        return result;
    }
}
