using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Congee;

public record Sensor(string Type, string Name, string Path, double Value, string Display, string HwId, string Hardware);

/// <summary>
/// Reads temperatures, fans, etc. as JSON over localhost from congee's sensor service
/// (congee-sensors, a Windows service setup installs; see ..\sensors). The service runs with admin
/// rights and does the hardware access, so congee itself needs none. It uses LibreHardwareMonitorLib
/// and serves LibreHardwareMonitor's data.json format, so sensor names follow LHM's.
/// </summary>
public sealed class Sensors
{
    static readonly Dictionary<string, string> GroupTypes = new()
    {
        ["temperatures"] = "temperature", ["fans"] = "fan", ["load"] = "load", ["clocks"] = "clock",
        ["powers"] = "power", ["voltages"] = "voltage", ["currents"] = "current", ["controls"] = "control",
        ["data"] = "data", ["smalldata"] = "data", ["throughput"] = "throughput", ["levels"] = "level",
        ["factors"] = "factor", ["energy"] = "energy", ["noise"] = "noise",
    };
    static readonly Regex NumRe = new(@"-?\d+(?:[.,]\d+)?");
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    const double StaleSeconds = 10;

    public const string ServiceUrl = "http://127.0.0.1:8086/data.json";

    public string Url { get; private set; } = Config.Defaults["sensors_url"];
    bool? wasOk;  // last state written to the log
    double interval = 1.0;
    volatile List<Sensor> items = [];
    Dictionary<(string, string), (Sensor s, double t)> seen = [];
    double last = -1e9;
    int busy;

    public bool Ok { get; private set; }
    public string Error { get; private set; } = "not polled yet";

    public void Configure(string url, double updateInterval)
    {
        url = url.Trim();
        if (url.Length == 0 || url.Equals("auto", StringComparison.OrdinalIgnoreCase)) url = ServiceUrl;
        if (url != Url) { items = []; Ok = false; last = -1e9; seen = []; wasOk = null; }
        Url = url;
        interval = Math.Max(0.5, updateInterval);
    }

    static double Now => Environment.TickCount64 / 1000.0;

    /// <summary>Latest readings; starts a background refresh when stale.</summary>
    public List<Sensor> Get()
    {
        if (Now - last >= interval && Interlocked.CompareExchange(ref busy, 1, 0) == 0)
        {
            last = Now;
            _ = Task.Run(RefreshAsync);
        }
        return items;
    }

    public void RefreshNow()
    {
        Interlocked.Exchange(ref busy, 1);
        RefreshAsync().GetAwaiter().GetResult();
        last = Now;
    }

    async Task RefreshAsync()
    {
        try
        {
            string json = await Http.GetStringAsync(Url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var fresh = new List<Sensor>();
            Walk(doc.RootElement, [], null, fresh, ("", ""));
            // Some sensors (e.g. AMD GPU ones) briefly vanish from LHM's feed;
            // keep a sensor's last reading for a few seconds instead of N/A.
            double now = Now;
            var next = new Dictionary<(string, string), (Sensor, double)>(seen);
            foreach (var s in fresh) next[(s.Type, s.Path)] = (s, now);
            seen = next.Where(kv => now - kv.Value.Item2 <= StaleSeconds)
                       .ToDictionary(kv => kv.Key, kv => kv.Value);
            items = seen.Values.Select(v => v.s).ToList();
            Ok = true;
            Error = "";
            LogState(true);
        }
        catch (Exception e)
        {
            items = [];
            Ok = false;
            Error = e.Message;
            LogState(false);
        }
        finally
        {
            Interlocked.Exchange(ref busy, 0);
        }
    }

    /// <summary>Logs when sensor data starts or stops arriving (not every poll). Not from --sensors,
    /// which never logs a start: a line from it would look like a run that never stopped.</summary>
    void LogState(bool ok)
    {
        if (wasOk == ok || !Log.IsStarted) return;
        wasOk = ok;
        Log.Write(ok ? "sensors: reading " + Url
                     : "sensors: no data from " + Url + " (" + Error + "). Is the congee sensor service installed and running?");
    }

    static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    void Walk(JsonElement node, List<string> path, string? group, List<Sensor> outp, (string id, string name) hw)
    {
        string text = Str(node, "Text");
        string hwId = Str(node, "HardwareId");
        if (hwId.Length > 0) hw = (hwId.ToLowerInvariant(), text);  // LHM hardware node, e.g. /gpu-nvidia/0
        if (node.TryGetProperty("Children", out var ch) && ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0)
        {
            string? gtype = GroupTypes.GetValueOrDefault(text.ToLowerInvariant().Replace(" ", ""), group!) ?? group;
            var sub = text.Length > 0 ? [.. path, text] : path;
            foreach (var c in ch.EnumerateArray()) Walk(c, sub, gtype, outp, hw);
            return;
        }
        if (hw.id.Length == 0 && Str(node, "SensorId") is { Length: > 0 } sid)  // older LHM: /gpu-amd/0/temperature/1
            hw = (string.Join("/", sid.ToLowerInvariant().Split('/').Take(3)), path.LastOrDefault(p => !GroupTypes.ContainsKey(p.ToLowerInvariant().Replace(" ", ""))) ?? "");
        string raw = Str(node, "RawValue");
        if (raw.Length == 0) raw = Str(node, "Value");
        var m = NumRe.Match(raw);
        string type = Str(node, "Type");
        if (!m.Success || (type.Length == 0 && group == null)) return;
        type = (type.Length > 0 ? type : group!).ToLowerInvariant();
        double val = double.Parse(m.Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        string fullPath = string.Join("/", path.Skip(1).Append(text));  // drop the root node
        outp.Add(new Sensor(type, text, fullPath, val, Str(node, "Value"), hw.id, hw.name));
    }

    public Sensor? Find(string type, string query, string? hwId = null) => Find(type, [query], hwId);

    /// <summary>First sensor of a type whose name (exact) or path (substring) matches,
    /// trying each query in turn; hwId limits the search to one device. "" = any.</summary>
    public Sensor? Find(string type, IEnumerable<string> queries, string? hwId = null)
    {
        var cands = Get().Where(s => s.Type == type && (hwId == null || s.HwId == hwId)).ToList();
        foreach (var query in queries)
        {
            string q = query.Trim().ToLowerInvariant();
            if (q.Length == 0)
            {
                if (cands.Count > 0) return cands[0];
                continue;
            }
            var hit = cands.FirstOrDefault(s => s.Name.ToLowerInvariant() == q)
                   ?? cands.FirstOrDefault(s => s.Path.ToLowerInvariant().Contains(q));
            if (hit != null) return hit;
        }
        return null;
    }

    // ---- device selection (GPU / CPU) ---------------------------------------------
    /// <summary>(hwId, name) of every device whose id starts with prefix, in LHM order.</summary>
    public List<(string id, string name)> Devices(string prefix)
    {
        var res = new List<(string, string)>();
        foreach (var s in Get())
            if (s.HwId.StartsWith(prefix) && !res.Any(r => r.Item1 == s.HwId)) res.Add((s.HwId, s.Hardware));
        return res;
    }

    static readonly Regex IntegratedGpu = new(@"integrated|\b(uhd|iris|hd) graphics|radeon\(tm\) graphics$|radeon graphics$|vega \d+ graphics",
        RegexOptions.IgnoreCase);

    /// <summary>GPU memory used/total in bytes. LHM reports SmallData (MB) on some
    /// cards and Data (GB) on others, so accept either.</summary>
    public double? Vram(string hwId, string which)
    {
        foreach (var name in new[] { $"GPU Memory {which}", $"D3D Dedicated Memory {which}" })
            foreach (var (type, mult) in new[] { ("smalldata", 1024.0 * 1024), ("data", 1024.0 * 1024 * 1024) })
                if (Find(type, name, hwId) is { } s && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return s.Value * mult;
        return null;
    }

    /// <summary>`auto` prefers a discrete card (most VRAM); a number picks by
    /// position; other text is matched against the GPU's name or id.</summary>
    public (string id, string name)? SelectGpu(string pref)
    {
        var gpus = Devices("/gpu");
        if (gpus.Count == 0) return null;
        pref = string.IsNullOrWhiteSpace(pref) ? "auto" : pref.Trim().ToLowerInvariant();
        if (int.TryParse(pref, out int idx)) return idx >= 0 && idx < gpus.Count ? gpus[idx] : null;
        if (pref != "auto")
        {
            foreach (var g in gpus)
                if (g.name.ToLowerInvariant().Contains(pref) || g.id.Contains(pref)) return g;
            return null;
        }
        (string, string) best = gpus[0];
        (bool, double) bestScore = (false, -1);
        foreach (var g in gpus)
        {
            var score = (!IntegratedGpu.IsMatch(g.id + " " + g.name), Vram(g.id, "Total") ?? 0);
            if (score.Item1 && !bestScore.Item1 || score.Item1 == bestScore.Item1 && score.Item2 > bestScore.Item2)
            {
                best = g;
                bestScore = score;
            }
        }
        return best;
    }
}
