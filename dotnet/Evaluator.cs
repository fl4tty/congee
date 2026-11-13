using System.Text.RegularExpressions;

namespace Congee;

// ---- segments produced by evaluating the template ------------------------------
public abstract record Seg;
public sealed record TextSeg(string S, string Color, string Font) : Seg;
public sealed record BarSeg(double Frac, double H, double W, string Color) : Seg;      // W = 0 -> fill
public sealed record GraphSeg(double[] Vals, double H, double W, string Color, string? Color2) : Seg;
public sealed record HrSeg(double H, string Color, bool Dash) : Seg;
public sealed record GotoSeg(double X) : Seg;
public sealed record OffsetSeg(double X) : Seg;
public sealed record AlignSeg(bool Right) : Seg;                                      // alignr / alignc

/// <summary>Turns tokens + live stats into lines of segments.</summary>
public sealed class Evaluator(Stats st, Dictionary<string, string> cfg, double scale, Func<string, bool> isColor)
{
    static readonly Regex SizeRe = new(@"^\d+(,\d+)?$");
    static readonly Dictionary<string, string> SensorWidgetTypes = new()
    {
        ["tempbar"] = "temperature", ["tempgraph"] = "temperature", ["fangraph"] = "fan",
        ["loadbar"] = "load", ["loadgraph"] = "load", ["controlbar"] = "control",
    };

    // Device-scoped variables: (sensor type, names to try in order). The lists cover
    // how LibreHardwareMonitor names things on AMD, NVIDIA and Intel hardware;
    // "" means "first sensor of that type on the device".
    static readonly Dictionary<string, (string type, string[] names)> DeviceVars = new()
    {
        ["gpu_temp"] = ("temperature", ["GPU Core", "GPU", ""]),
        ["gpu_hotspot"] = ("temperature", ["GPU Hot Spot", "Hot Spot"]),
        ["gpu_memtemp"] = ("temperature", ["GPU Memory Junction", "GPU Memory"]),
        ["gpu_load"] = ("load", ["GPU Core", "D3D 3D", ""]),
        ["gpu_clock"] = ("clock", ["GPU Core", ""]),
        ["gpu_memclock"] = ("clock", ["GPU Memory"]),
        ["gpu_power"] = ("power", ["GPU Package", "GPU Power", ""]),
        ["gpu_fan"] = ("fan", ["GPU Fan", ""]),
        ["gpu_fan_perc"] = ("control", ["GPU Fan", ""]),
        // AMD Zen2+: "Core (Tctl/Tdie)", Zen1: "Core (Tctl)"; Intel: "CPU Package"
        ["cpu_temp"] = ("temperature", ["Core (Tctl/Tdie)", "Core (Tctl)", "CPU Package", "Package", "Core Max", "Core Average", ""]),
        // AMD: "Package"; Intel: "CPU Package"
        ["cpu_power"] = ("power", ["Package", "CPU Package", ""]),
    };
    static readonly Dictionary<string, string> DeviceWidgets = new()
    {
        ["gpu_loadbar"] = "gpu_load", ["gpu_loadgraph"] = "gpu_load",
        ["gpu_tempbar"] = "gpu_temp", ["gpu_tempgraph"] = "gpu_temp",
        ["gpu_fangraph"] = "gpu_fan", ["cpu_tempbar"] = "cpu_temp", ["cpu_tempgraph"] = "cpu_temp",
    };

    readonly bool fahrenheit = cfg.GetValueOrDefault("temperature_unit", "celsius").Trim().ToLowerInvariant() is "fahrenheit" or "f";

    /// <summary>Sensor value for display: temperatures follow temperature_unit (LHM reports °C).
    /// Bars and graphs keep using °C, so temp_bar_max stays in °C.</summary>
    double Deg(double v, string type) => fahrenheit && type == "temperature" ? v * 9 / 5 + 32 : v;

    (string id, string name)? Gpu() => st.Cached("gpu", () => st.Sensors.SelectGpu(cfg.GetValueOrDefault("gpu", "auto")));

    string? CpuHw() => st.Cached("cpu_hw", () =>
    {
        var cpus = st.Sensors.Devices("/amdcpu").Concat(st.Sensors.Devices("/intelcpu")).Concat(st.Sensors.Devices("/cpu")).ToList();
        return cpus.Count > 0 ? cpus[0].id : null;
    });

    Sensor? DeviceSensor(string var, string overrideName = "")
    {
        var (type, names) = DeviceVars[var];
        string? hw = var.StartsWith("cpu_") ? CpuHw() : Gpu()?.id;
        if (hw == null) return null;
        return st.Sensors.Find(type, overrideName.Trim().Length > 0 ? [overrideName] : names, hw);
    }

    double Px(string v) => double.TryParse(v, System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var d) ? d * scale : 0;
    double Px(double v) => v * scale;

    // ---- conditions -------------------------------------------------------------------
    bool Condition(string name, string args)
    {
        string a = args.Trim();
        switch (name)
        {
            case "if_up": return st.IfUp(a);
            case "if_running":
                string want = a.ToLowerInvariant();
                if (want.EndsWith(".exe")) want = want[..^4];
                return st.Top().Any(r => r.Name.Equals(want, StringComparison.OrdinalIgnoreCase));
            case "if_existing":
            case "if_mounted": return File.Exists(a) || Directory.Exists(a);
            case "if_battery": return st.Battery() != null;
            case "if_sensors": st.Sensors.Get(); return st.Sensors.Ok;
            case "if_gpu": return Gpu() != null;
            case "if_sensor" or "if_gpu_sensor":
            {
                int sp = a.IndexOf(' ');
                string type = (sp < 0 ? a : a[..sp]).ToLowerInvariant(), q = sp < 0 ? "" : a[(sp + 1)..];
                if (name == "if_sensor") return st.Sensors.Find(type, q) != null;
                return Gpu() is { } g && st.Sensors.Find(type, q, g.id) != null;
            }
            default: return false;
        }
    }

    // ---- bars / graphs ---------------------------------------------------------------------
    (double h, double w, List<string> colors, string src) WidgetArgs(string args, double defaultH)
    {
        double h = defaultH, w = 0;
        var colors = new List<string>();
        var rest = new List<string>();
        bool sizeSeen = false;
        foreach (var t in Config.SplitArgs(args))
        {
            if (t.StartsWith('-')) continue;
            if (!sizeSeen && SizeRe.IsMatch(t))
            {
                var p = t.Split(',');
                h = Px(p[0]);
                w = p.Length > 1 ? Px(p[1]) : 0;
                sizeSeen = true;
            }
            else if (sizeSeen && isColor(Config.NormColor(t))) colors.Add(Config.NormColor(t));
            else rest.Add(t);
        }
        return (h, w, colors, string.Join(" ", rest));
    }

    Seg? Widget(string name, string args, string color)
    {
        double barH = Px(cfg["default_bar_height"]), graphH = Px(cfg["default_graph_height"]);
        double tempMax = Config.Num(cfg, "temp_bar_max");

        if (name.EndsWith("bar"))
        {
            var (h, w, _, src) = WidgetArgs(args, barH);
            double? frac = name switch
            {
                "cpubar" => st.CpuValue(src) / 100,
                "membar" => st.Mem().percent / 100,
                "swapbar" => st.Swap().percent / 100,
                "fs_bar" => st.Disk(src) is { } d ? 1 - (double)d.AvailableFreeSpace / d.TotalSize : 0,
                "fs_bar_free" => st.Disk(src) is { } d2 ? (double)d2.AvailableFreeSpace / d2.TotalSize : 0,
                "battery_bar" => (st.Battery()?.percent ?? 0) / 100.0,
                "tempbar" => (st.Sensors.Find("temperature", src)?.Value ?? 0) / tempMax,
                "loadbar" or "controlbar" => (st.Sensors.Find(SensorWidgetTypes[name], src)?.Value ?? 0) / 100,
                _ when DeviceWidgets.TryGetValue(name, out var dv) =>
                    (DeviceSensor(dv, src)?.Value ?? 0) / (name.Contains("temp") ? tempMax : 100),
                "gpu_vrambar" => Gpu() is { } g && st.Sensors.Vram(g.id, "Used") is { } u && st.Sensors.Vram(g.id, "Total") is { } t && t > 0 ? u / t : 0,
                _ => null,
            };
            return frac is { } f ? new BarSeg(Math.Clamp(f, 0, 1), h, w, color) : null;
        }

        if (name.EndsWith("graph"))
        {
            var (h, w, colors, src) = WidgetArgs(args, graphH);
            string c1 = colors.Count > 0 ? colors[0] : color;
            string? c2 = colors.Count > 1 ? colors[1] : null;
            string key; double val; double? top;
            switch (name)
            {
                case "cpugraph": key = "cpu:" + (src.Length > 0 ? src.ToLowerInvariant() : "cpu0"); val = st.CpuValue(src); top = 100; break;
                case "memgraph": key = "mem"; val = st.Mem().percent; top = 100; break;
                case "downspeedgraph": key = name + src; val = st.Net(src).down; top = null; break;
                case "upspeedgraph": key = name + src; val = st.Net(src).up; top = null; break;
                case "diskiograph": key = "diskio"; val = st.DiskRate.read + st.DiskRate.write; top = null; break;
                case "tempgraph" or "fangraph" or "loadgraph":
                    key = name + ":" + src.ToLowerInvariant();
                    val = st.Sensors.Find(SensorWidgetTypes[name], src)?.Value ?? 0;
                    top = name == "tempgraph" ? tempMax : name == "loadgraph" ? 100 : null;
                    break;
                default:
                    if (!DeviceWidgets.TryGetValue(name, out var dv)) return null;
                    key = name + ":" + src.ToLowerInvariant();
                    val = DeviceSensor(dv, src)?.Value ?? 0;
                    top = name.Contains("temp") ? tempMax : name.Contains("load") ? 100 : null;
                    break;
            }
            var hist = st.PushHistory(key, val);
            double peak = top ?? Math.Max(hist.Max(), name.EndsWith("fangraph") ? 1.0 : 1024.0);
            return new GraphSeg(hist.Select(v => v / peak).ToArray(), h, w, c1, c2);
        }
        return null;
    }

    // ---- plain text variables --------------------------------------------------------------------
    static string F0(double v) => Math.Round(v).ToString("0");

    string? TextValue(string name, string args)
    {
        string a = args.Trim();
        switch (name)
        {
            case "cpu": return F0(st.CpuValue(a));
            case "freq": return F0(st.CpuMhz);
            case "freq_g": return (st.CpuMhz / 1000).ToString("0.00");
            case "cpu_name": return st.CpuName;
            case "cpu_cores": return st.PhysicalCores.ToString();
            case "cpu_threads": return st.CpuCount.ToString();
            case "mem": return Config.FmtBytes(st.Mem().used);
            case "memmax": return Config.FmtBytes(st.Mem().total);
            case "memfree" or "memeasyfree": return Config.FmtBytes(st.Mem().avail);
            case "memperc": return F0(st.Mem().percent);
            case "swap": return Config.FmtBytes(st.Swap().used);
            case "swapmax": return Config.FmtBytes(st.Swap().total);
            case "swapperc": return F0(st.Swap().percent);
            case "fs_used" or "fs_size" or "fs_free" or "fs_used_perc" or "fs_free_perc":
                if (st.Disk(a) is not { } d) return "N/A";
                double used = d.TotalSize - d.TotalFreeSpace, pct = 100.0 * used / d.TotalSize;
                return name switch
                {
                    "fs_used" => Config.FmtBytes(used),
                    "fs_size" => Config.FmtBytes(d.TotalSize),
                    "fs_free" => Config.FmtBytes(d.AvailableFreeSpace),
                    "fs_used_perc" => F0(pct),
                    _ => F0(100 - pct),
                };
            case "diskio": return Config.FmtBytes(st.DiskRate.read + st.DiskRate.write);
            case "diskio_read": return Config.FmtBytes(st.DiskRate.read);
            case "diskio_write": return Config.FmtBytes(st.DiskRate.write);
            case "downspeed": return Config.FmtBytes(st.Net(a).down);
            case "upspeed": return Config.FmtBytes(st.Net(a).up);
            case "downspeedf": return (st.Net(a).down / 1024).ToString("0.0");
            case "upspeedf": return (st.Net(a).up / 1024).ToString("0.0");
            case "totaldown": return Config.FmtBytes(st.NetTotal(a).rx);
            case "totalup": return Config.FmtBytes(st.NetTotal(a).tx);
            case "addr": return st.Addr(a);
            case "uptime": return Config.FmtDuration(Environment.TickCount64 / 1000.0);
            case "uptime_short": return Config.FmtDuration(Environment.TickCount64 / 1000.0, true);
            case "time": return Strftime.Format(DateTime.Now, args.Length > 0 ? args : "%c");
            case "utime": return Strftime.Format(DateTime.UtcNow, args.Length > 0 ? args : "%c");
            case "nodename": return System.Net.Dns.GetHostName();
            case "user_names": return Environment.UserName;
            case "sysname": return "Windows";
            case "kernel": return Environment.OSVersion.Version.ToString(3);
            case "os": return Environment.OSVersion.Version.Build >= 22000 ? "Windows 11" : "Windows 10";
            case "machine": return Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "unknown";
            case "processes": return st.ProcessCount().ToString();
            case "battery_percent" or "battery_status" or "battery_time":
                if (st.Battery() is not { } b) return "N/A";
                if (name == "battery_percent") return b.percent.ToString();
                if (name == "battery_status") return b.plugged ? (b.percent >= 99 ? "full" : "charging") : "discharging";
                return b.plugged || b.secsLeft < 0 ? "" : Config.FmtDuration(b.secsLeft, true);
            case "top" or "top_mem":
            {
                var parts = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !int.TryParse(parts[1], out int n)) return "?";
                var rows = name == "top"
                    ? st.Top().OrderByDescending(r => r.Cpu).ToList()
                    : st.Top().OrderByDescending(r => r.Rss).ToList();
                if (n < 1 || n > rows.Count) return "";
                var r = rows[n - 1];
                return parts[0] switch
                {
                    "name" => r.Name.Length > 18 ? r.Name[..18] : r.Name,
                    "pid" => r.Pid.ToString(),
                    "cpu" => r.Cpu.ToString("0.0"),
                    "mem" => r.Mem.ToString("0.0"),
                    "mem_res" => Config.FmtBytes(r.Rss),
                    _ => "?",
                };
            }
            case var dvar when DeviceVars.ContainsKey(dvar):
                return DeviceSensor(dvar, a) is { } ds ? F0(Deg(ds.Value, DeviceVars[dvar].type)) : "N/A";
            case "temp_unit": return fahrenheit ? "°F" : "°C";
            case "gpu_name": return Gpu()?.name ?? "No GPU found";
            case "gpu_vram_used" or "gpu_vram_total" or "gpu_vram_perc":
            {
                var g = Gpu();
                double? vUsed = g is { } g1 ? st.Sensors.Vram(g1.id, "Used") : null;
                double? vTotal = g is { } g2 ? st.Sensors.Vram(g2.id, "Total") : null;
                return name switch
                {
                    "gpu_vram_used" => vUsed is { } uu ? Config.FmtBytes(uu) : "N/A",
                    "gpu_vram_total" => vTotal is { } tt && tt > 0 ? Config.FmtBytes(tt) : "N/A",
                    _ => vUsed is { } u && vTotal is { } t && t > 0 ? F0(u * 100 / t) : "N/A",
                };
            }
            case "gpu_sensor":
            {
                int sp = a.IndexOf(' ');
                string type = (sp < 0 ? a : a[..sp]).ToLowerInvariant(), q = sp < 0 ? "" : a[(sp + 1)..];
                if (Gpu() is not { } g || st.Sensors.Find(type, q, g.id) is not { } gs) return "N/A";
                double v = Deg(gs.Value, type);
                return Math.Abs(v) >= 100 || v == Math.Floor(v) ? F0(v) : v.ToString("0.0");
            }
            case "temp" or "fan":
                var s = st.Sensors.Find(name == "temp" ? "temperature" : "fan", a);
                return s is null ? "N/A" : F0(Deg(s.Value, s.Type));
            case "sensor":
            {
                int sp = a.IndexOf(' ');
                string type = (sp < 0 ? a : a[..sp]).ToLowerInvariant(), q = sp < 0 ? "" : a[(sp + 1)..];
                var sv = st.Sensors.Find(type, q);
                if (sv is null) return "N/A";
                double v = Deg(sv.Value, type);
                return Math.Abs(v) >= 100 || v == Math.Floor(v) ? F0(v) : v.ToString("0.0");
            }
            case "exec": return st.RunExec(args, Config.Num(cfg, "update_interval"));
            case "execi":
            {
                int sp = args.IndexOf(' ');
                if (sp < 0 || !double.TryParse(args[..sp], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double iv))
                    return "[execi needs: interval command]";
                return st.RunExec(args[(sp + 1)..].Trim(), iv);
            }
        }
        return null;
    }

    // ---- whole template -> lines of segments ------------------------------------------------------------
    public List<List<Seg>> Build(List<Config.Token> tokens)
    {
        string defaultColor = Config.NormColor(cfg["default_color"]);
        string defaultFont = cfg["font"];
        bool upper = Config.Yes(cfg["uppercase"]);
        string color = defaultColor, font = defaultFont;
        var lines = new List<List<Seg>> { new() };
        var stack = new Stack<(bool parent, bool taken)>();
        bool active = true;

        void AddText(string s)
        {
            var parts = s.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) lines.Add([]);
                if (parts[i].Length > 0)
                    lines[^1].Add(new TextSeg(upper ? parts[i].ToUpperInvariant() : parts[i], color, font));
            }
        }

        foreach (var tok in tokens)
        {
            if (tok.IsLiteral) { if (active) AddText(tok.Text); continue; }
            string name = tok.Name, args = tok.Args;

            if (name.StartsWith("if_"))
            {
                bool taken = active && Condition(name, args);
                stack.Push((active, taken));
                active = taken;
                continue;
            }
            if (name == "else")
            {
                if (stack.Count > 0)
                {
                    var (parent, taken) = stack.Pop();
                    active = parent && !taken;
                    stack.Push((parent, true));
                }
                continue;
            }
            if (name == "endif")
            {
                if (stack.Count > 0) active = stack.Pop().parent;
                continue;
            }
            if (!active) continue;

            if (name == "color") color = args.Length > 0 ? Config.NormColor(args) : defaultColor;
            else if (Regex.IsMatch(name, @"^color\d$")) color = Config.NormColor(cfg.GetValueOrDefault(name, defaultColor));
            else if (name == "font") font = args.Length > 0 ? args : defaultFont;
            else if (name == "alignr") lines[^1].Add(new AlignSeg(true));
            else if (name == "alignc") lines[^1].Add(new AlignSeg(false));
            else if (name == "goto") lines[^1].Add(new GotoSeg(Px(args.Length > 0 ? args : "0")));
            else if (name == "offset") lines[^1].Add(new OffsetSeg(Px(args.Length > 0 ? args : "0")));
            else if (name is "hr" or "stippled_hr")
                lines[^1].Add(new HrSeg(Math.Max(1, Px(args.Length > 0 ? args : "1")), color, name == "stippled_hr"));
            else if (name.EndsWith("bar") || name.EndsWith("graph"))
            {
                var seg = Widget(name, args, color);
                if (seg is null) AddText(tok.Text); else lines[^1].Add(seg);
            }
            else AddText(TextValue(name, args) ?? tok.Text);
        }
        return lines;
    }
}
