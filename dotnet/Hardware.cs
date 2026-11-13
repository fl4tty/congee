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

/// <summary>Drive and display-driver discovery (the .NET twin of list_drives / read_gpu_drivers).</summary>
public static class Hardware
{
    [DllImport("kernel32.dll")] static extern uint GetLogicalDrives();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetDriveType(string root);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetVolumeInformation(string root, StringBuilder label, int labelLen, out uint serial,
        IntPtr maxComponent, IntPtr flags, IntPtr fsName, int fsNameLen);
    [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);

    /// <summary>Ready drives as (root, volume label): fixed, removable (USB) and RAM disks;
    /// includeAll adds network and optical drives. Empty card readers and the same volume
    /// under a second letter (subst, extra mount point) are skipped.</summary>
    public static List<(string root, string label)> ListDrives(bool includeAll)
    {
        var wanted = includeAll ? new uint[] { 2, 3, 4, 5, 6 } : [2, 3, 6];  // REMOVABLE, FIXED, (REMOTE, CDROM,) RAMDISK
        var result = new List<(string, string)>();
        var seen = new HashSet<uint>();
        uint mask = GetLogicalDrives();
        uint oldMode = SetErrorMode(0x0001);  // SEM_FAILCRITICALERRORS: no "insert a disk" dialogs
        try
        {
            for (int i = 0; i < 26; i++)
            {
                if ((mask >> i & 1) == 0) continue;
                string root = $"{(char)('A' + i)}:\\";
                if (!wanted.Contains(GetDriveType(root))) continue;
                var label = new StringBuilder(261);
                if (!GetVolumeInformation(root, label, label.Capacity, out uint serial, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
                    continue;  // not ready
                if (serial != 0 && !seen.Add(serial)) continue;
                result.Add((root, label.ToString()));
            }
        }
        finally { SetErrorMode(oldMode); }
        return result;
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
