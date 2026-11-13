using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Congee;

/// <summary>System statistics, sampled once per tick (the .NET twin of psutil).</summary>
public sealed class Stats
{
    // ---- Win32 -------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [StructLayout(LayoutKind.Sequential)]
    struct CPU_PERF { public long Idle, Kernel, User, Dpc, Interrupt; public int InterruptCount; int pad; }
    [DllImport("ntdll.dll")]
    static extern int NtQuerySystemInformation(int cls, [Out] CPU_PERF[] info, int len, out int retLen);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref int length);

    static int CountPhysicalCores()
    {
        int len = 0;
        GetLogicalProcessorInformationEx(0 /* RelationProcessorCore */, IntPtr.Zero, ref len);
        if (len == 0) return Environment.ProcessorCount;
        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buf, ref len)) return Environment.ProcessorCount;
            int count = 0, off = 0;
            while (off < len)
            {
                count++;
                off += Marshal.ReadInt32(buf, off + 4);  // record Size follows Relationship
            }
            return count;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("kernel32.dll")]
    static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    // ---- state ---------------------------------------------------------------------
    public long Tick { get; private set; }
    public readonly int CpuCount = Environment.ProcessorCount;
    public double[] CpuPer;
    public double CpuTotal;
    public readonly string CpuName;
    public readonly double CpuMhz;
    public readonly int PhysicalCores = CountPhysicalCores();
    public readonly Sensors Sensors = new();

    CPU_PERF[] cpuPrev;
    double lastT;
    Dictionary<string, (long rx, long tx)> netPrev = [];
    public Dictionary<string, (double down, double up)> NetRate = [];
    public Dictionary<string, (long rx, long tx)> NetTotals = [];
    NetworkInterface[] nics = [];
    public (double read, double write) DiskRate;
    PerformanceCounter? diskRead, diskWrite, pageUsage;
    readonly Dictionary<string, Queue<double>> history = [];
    readonly Dictionary<string, long> historyTick = [];
    readonly Dictionary<int, TimeSpan> procPrev = [];
    double procPrevT;
    readonly Dictionary<string, object?> cache = [];
    readonly Dictionary<string, ExecEntry> execCache = [];

    public record ProcRow(string Name, int Pid, double Cpu, long Rss, double Mem);
    sealed class ExecEntry { public double T = -1e9; public string Out = ""; public int Busy; }

    static double Now => Environment.TickCount64 / 1000.0;

    public Stats()
    {
        CpuPer = new double[CpuCount];
        cpuPrev = ReadCpu();
        lastT = Now;
        using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        CpuName = string.Join(" ", ((k?.GetValue("ProcessorNameString") as string) ?? "Unknown CPU")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        CpuMhz = Convert.ToDouble(k?.GetValue("~MHz") ?? 0);
        SampleNet(1);
        try
        {
            diskRead = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
            diskWrite = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
            pageUsage = new PerformanceCounter("Paging File", "% Usage", "_Total");
            diskRead.NextValue(); diskWrite.NextValue();
        }
        catch { /* counters disabled on this machine: disk I/O shows 0 */ }
    }

    CPU_PERF[] ReadCpu()
    {
        var arr = new CPU_PERF[CpuCount];
        NtQuerySystemInformation(8 /* SystemProcessorPerformanceInformation */, arr,
            Marshal.SizeOf<CPU_PERF>() * CpuCount, out _);
        return arr;
    }

    public void Sample()
    {
        double now = Now, dt = Math.Max(now - lastT, 1e-3);
        lastT = now;
        Tick++;
        cache.Clear();

        var cur = ReadCpu();
        double sum = 0;
        for (int i = 0; i < CpuCount; i++)
        {
            long total = (cur[i].Kernel - cpuPrev[i].Kernel) + (cur[i].User - cpuPrev[i].User); // kernel includes idle
            long idle = cur[i].Idle - cpuPrev[i].Idle;
            CpuPer[i] = total > 0 ? Math.Clamp(100.0 * (total - idle) / total, 0, 100) : 0;
            sum += CpuPer[i];
        }
        CpuTotal = sum / CpuCount;
        cpuPrev = cur;

        SampleNet(dt);
        try { DiskRate = (diskRead?.NextValue() ?? 0, diskWrite?.NextValue() ?? 0); } catch { DiskRate = (0, 0); }
    }

    // .NET also lists the hidden filter layers Windows stacks on each adapter
    // ("Ethernet-WFP Native MAC Layer LightWeight Filter-0000", "...-QoS Packet
    // Scheduler-0000"). They mirror the adapter's own byte counters, so summing
    // them would count the same traffic several times.
    static readonly System.Text.RegularExpressions.Regex FilterLayer = new(@"-\d{4}$|\(Kernel Debugger\)$");

    static NetworkInterface[] RealInterfaces()
    {
        try { return NetworkInterface.GetAllNetworkInterfaces().Where(n => !FilterLayer.IsMatch(n.Name)).ToArray(); }
        catch { return []; }
    }

    void SampleNet(double dt)
    {
        nics = RealInterfaces();
        var totals = new Dictionary<string, (long, long)>();
        var rates = new Dictionary<string, (double, double)>();
        foreach (var n in nics)
        {
            try
            {
                var s = n.GetIPStatistics();
                totals[n.Name] = (s.BytesReceived, s.BytesSent);
                if (netPrev.TryGetValue(n.Name, out var p))
                    rates[n.Name] = (Math.Max(s.BytesReceived - p.rx, 0) / dt, Math.Max(s.BytesSent - p.tx, 0) / dt);
            }
            catch { }
        }
        netPrev = totals;
        NetTotals = totals;
        NetRate = rates;
    }

    public T Cached<T>(string key, Func<T> fn)
    {
        if (!cache.TryGetValue(key, out var v)) cache[key] = v = fn();
        return (T)v!;
    }

    // ---- memory ------------------------------------------------------------------------
    public (ulong total, ulong avail, ulong used, double percent) Mem() => Cached("mem", () =>
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref m);
        ulong used = m.ullTotalPhys - m.ullAvailPhys;
        return (m.ullTotalPhys, m.ullAvailPhys, used, 100.0 * used / Math.Max(1, m.ullTotalPhys));
    });

    public (double total, double used, double percent) Swap() => Cached("swap", () =>
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref m);
        double total = m.ullTotalPageFile > m.ullTotalPhys ? m.ullTotalPageFile - m.ullTotalPhys : 0;
        double pct = 0;
        try { pct = pageUsage?.NextValue() ?? 0; } catch { }
        return (total, total * pct / 100, pct);
    });

    // ---- disks / battery -------------------------------------------------------------------
    public DriveInfo? Disk(string path) => Cached("fs:" + path, () =>
    {
        try
        {
            var d = new DriveInfo(string.IsNullOrWhiteSpace(path) ? "C:\\" : path);
            return d.IsReady ? d : null;
        }
        catch { return null; }
    });

    public (int percent, bool plugged, int secsLeft)? Battery() => Cached("bat", () =>
    {
        if (!GetSystemPowerStatus(out var s) || (s.BatteryFlag & 128) != 0 || s.BatteryFlag == 255)
            return ((int, bool, int)?)null;
        return (s.BatteryLifePercent, s.ACLineStatus == 1, s.BatteryLifeTime);
    });

    // ---- cpu / network helpers ------------------------------------------------------------------
    public double CpuValue(string arg)
    {
        arg = arg.Trim().ToLowerInvariant();
        if (arg.StartsWith("cpu") && int.TryParse(arg[3..], out int n))
        {
            if (n == 0) return CpuTotal;
            if (n >= 1 && n <= CpuCount) return CpuPer[n - 1];
        }
        return CpuTotal;
    }

    static bool RealNic(string name) => !name.Contains("loopback", StringComparison.OrdinalIgnoreCase);

    public (double down, double up) Net(string iface)
    {
        if (iface.Length > 0) return NetRate.GetValueOrDefault(iface);
        double d = 0, u = 0;
        foreach (var (n, r) in NetRate) if (RealNic(n)) { d += r.down; u += r.up; }
        return (d, u);
    }

    public (long rx, long tx) NetTotal(string iface)
    {
        if (iface.Length > 0) return NetTotals.GetValueOrDefault(iface);
        long rx = 0, tx = 0;
        foreach (var (n, t) in NetTotals) if (RealNic(n)) { rx += t.rx; tx += t.tx; }
        return (rx, tx);
    }

    public bool IfUp(string iface) =>
        nics.Any(n => n.Name == iface && n.OperationalStatus == OperationalStatus.Up);

    public string Addr(string iface)
    {
        foreach (var n in nics)
        {
            if (iface.Length > 0 ? n.Name != iface : (!RealNic(n.Name) || n.OperationalStatus != OperationalStatus.Up))
                continue;
            try
            {
                foreach (var a in n.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string s = a.Address.ToString();
                    if (!s.StartsWith("169.254.")) return s;
                }
            }
            catch { }
        }
        return "No address";
    }

    public IEnumerable<(string name, bool up, string ipv4)> Interfaces()
    {
        foreach (var n in RealInterfaces())
        {
            string ip = "";
            try
            {
                ip = string.Join(", ", n.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString()));
            }
            catch { }
            yield return (n.Name, n.OperationalStatus == OperationalStatus.Up, ip);
        }
    }

    // ---- processes -------------------------------------------------------------------------------
    public List<ProcRow> Top() => Cached("top", () =>
    {
        double now = Now, dt = Math.Max(now - procPrevT, 1e-3);
        bool first = procPrevT == 0;
        procPrevT = now;
        ulong memTotal = Mem().total;
        var rows = new List<ProcRow>();
        var seen = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == 0) continue;  // System Idle Process
                try
                {
                    var cpuTime = p.TotalProcessorTime;
                    long rss = p.WorkingSet64;
                    double cpu = 0;
                    if (!first && procPrev.TryGetValue(p.Id, out var prev))
                        cpu = Math.Max(0, (cpuTime - prev).TotalSeconds / dt / CpuCount * 100);
                    procPrev[p.Id] = cpuTime;
                    seen.Add(p.Id);
                    rows.Add(new ProcRow(p.ProcessName, p.Id, cpu, rss, 100.0 * rss / Math.Max(1, memTotal)));
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        foreach (var pid in procPrev.Keys.Where(k => !seen.Contains(k)).ToList()) procPrev.Remove(pid);
        return rows;
    });

    public int ProcessCount() => Cached("nproc", () => Process.GetProcesses().Length);

    // ---- graph history ----------------------------------------------------------------------------
    public Queue<double> PushHistory(string key, double value, int maxLen = 2000)
    {
        if (!history.TryGetValue(key, out var q)) history[key] = q = new Queue<double>();
        if (historyTick.GetValueOrDefault(key, -1) != Tick)
        {
            q.Enqueue(value);
            while (q.Count > maxLen) q.Dequeue();
            historyTick[key] = Tick;
        }
        return q;
    }

    // ---- exec / execi -------------------------------------------------------------------------------
    public string RunExec(string cmd, double interval)
    {
        lock (execCache)
        {
            if (!execCache.TryGetValue(cmd, out var e)) execCache[cmd] = e = new ExecEntry();
            if (Now - e.T >= interval && Interlocked.CompareExchange(ref e.Busy, 1, 0) == 0)
            {
                e.T = Now;
                _ = Task.Run(() => ExecWorker(cmd, e));
            }
            return e.Out;
        }
    }

    static void ExecWorker(string cmd, ExecEntry e)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c " + cmd)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(30_000)) { try { p.Kill(true); } catch { } }
            e.Out = outTask.Result.TrimEnd('\r', '\n').Replace("\r\n", "\n");
        }
        catch (Exception ex) { e.Out = $"[exec error: {ex.Message}]"; }
        finally { Interlocked.Exchange(ref e.Busy, 0); }
    }
}
