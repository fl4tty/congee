using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;

namespace CongeeSensors;

/// <summary>
/// Reads the sensors only when someone asks: each GET /data.json refreshes them (at most about
/// once a second, however many clients ask) and returns the tree. With congee closed, nothing is
/// read and the service just waits. Listens on 127.0.0.1 only, and only answers requests whose
/// Host is 127.0.0.1 or localhost, so web pages can't read it through a look-alike domain.
/// </summary>
sealed class SensorServer : IDisposable
{
    const int MinRefreshMs = 900;

    readonly int port;
    readonly Action<string> log;
    readonly Computer computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMotherboardEnabled = true,  // motherboard sensor chip: board temperatures, fans, voltages
        IsMemoryEnabled = true,       // RAM use and DIMM temperatures
        IsStorageEnabled = true,      // drive temperatures (NVMe "Composite" etc.)
        IsControllerEnabled = true,   // fan / pump controllers
        IsPsuEnabled = true,
        IsNetworkEnabled = false,     // congee reads network speeds itself
        IsBatteryEnabled = false,     // and the battery
    };
    readonly object gate = new();
    readonly CancellationTokenSource stop = new();
    TcpListener? listener;
    Task? opened;
    byte[] json = [];
    long jsonAt = -MinRefreshMs;  // (not long.MinValue: now - MinValue overflows)

    public SensorServer(int port, Action<string> log)
    {
        this.port = port;
        this.log = log;
    }

    public void Start()
    {
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        // Finding the hardware takes a few seconds; requests meanwhile get 503 and congee retries.
        opened = Task.Run(() =>
        {
            try { computer.Open(); }
            catch (Exception e) { log("opening the sensors failed: " + e); throw; }
        });
        _ = AcceptLoop(listener, stop.Token);
    }

    public void Dispose()
    {
        stop.Cancel();
        try { listener?.Stop(); } catch (Exception) { }
        try { opened?.Wait(10000); } catch (Exception) { }
        lock (gate)
        {
            try { computer.Close(); } catch (Exception) { }
        }
    }

    async Task AcceptLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await l.AcceptTcpClientAsync(ct); }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { log("accept failed: " + e.Message); await Task.Delay(500, CancellationToken.None); continue; }
            _ = Task.Run(() => Serve(client), CancellationToken.None);
        }
    }

    // ---- HTTP (just enough of it: one GET per connection) -------------------------------------

    async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var s = client.GetStream();
                using var cts = new CancellationTokenSource(3000);
                string head = await ReadHead(s, cts.Token);
                string[] lines = head.Split("\r\n");
                string[] req = lines[0].Split(' ');
                string host = lines.Skip(1).Where(x => x.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                                   .Select(x => x[5..].Trim().ToLowerInvariant()).FirstOrDefault() ?? "";
                string hostName = host.StartsWith('[') ? host[..(host.IndexOf(']') + 1)] : host.Split(':')[0];
                if (req.Length < 2 || req[0] != "GET")
                    await Reply(s, "405 Method Not Allowed", "text/plain", "GET only\n"u8.ToArray());
                else if (hostName is not ("127.0.0.1" or "localhost" or "[::1]" or ""))
                    await Reply(s, "403 Forbidden", "text/plain", "wrong host\n"u8.ToArray());
                else if (req[1].Split('?')[0] is not ("/data.json" or "/"))
                    await Reply(s, "404 Not Found", "text/plain", "try /data.json\n"u8.ToArray());
                else if (opened is not { IsCompletedSuccessfully: true })
                    await Reply(s, "503 Service Unavailable", "text/plain",
                        Encoding.UTF8.GetBytes(opened is { IsFaulted: true } ? "sensors failed to open\n" : "starting up\n"));
                else
                    await Reply(s, "200 OK", "application/json; charset=utf-8", Snapshot());
            }
            catch (Exception) { }  // client went away, timed out, sent garbage
        }
    }

    static async Task<string> ReadHead(NetworkStream s, CancellationToken ct)
    {
        var buf = new byte[4096];
        int n = 0;
        while (n < buf.Length)
        {
            int r = await s.ReadAsync(buf.AsMemory(n), ct);
            if (r == 0) break;
            n += r;
            if (buf.AsSpan(0, n).IndexOf("\r\n\r\n"u8) >= 0) break;
        }
        return Encoding.ASCII.GetString(buf, 0, n);
    }

    static async Task Reply(NetworkStream s, string status, string type, byte[] body)
    {
        string head = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n" +
                      "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        await s.WriteAsync(body);
    }

    // ---- reading and the JSON tree -------------------------------------------------------------

    byte[] Snapshot()
    {
        lock (gate)
        {
            long now = Environment.TickCount64;
            if (now - jsonAt >= MinRefreshMs)
            {
                foreach (var hw in computer.Hardware) Update(hw);
                json = Build();
                jsonAt = Environment.TickCount64;
            }
            return json;
        }
    }

    void Update(IHardware hw)
    {
        try { hw.Update(); } catch (Exception e) { log($"reading {hw.Name} failed: {e.Message}"); }
        foreach (var sub in hw.SubHardware) Update(sub);
    }

    // Same shape as LibreHardwareMonitor's web server: Sensor > computer > hardware > (sub-hardware)
    // > sensor-type groups > sensors, with the same names, ids and value text, so congee's parser
    // and any sensor paths in people's configs work with either.
    static readonly SensorType[] GroupOrder =
    [
        SensorType.Voltage, SensorType.Current, SensorType.Power, SensorType.Clock, SensorType.Temperature,
        SensorType.Load, SensorType.Frequency, SensorType.Fan, SensorType.Flow, SensorType.Control,
        SensorType.Level, SensorType.Factor, SensorType.Data, SensorType.SmallData, SensorType.Throughput,
        SensorType.TimeSpan, SensorType.Timing, SensorType.Energy, SensorType.Noise, SensorType.Conductivity,
        SensorType.Humidity,
    ];

    byte[] Build()
    {
        using var ms = new MemoryStream(64 * 1024);
        using (var w = new Utf8JsonWriter(ms))
        {
            int id = 0;
            Node(w, ref id, "Sensor", min: "Min", value: "Value", max: "Max");
            w.WriteStartArray("Children");
            Node(w, ref id, Environment.MachineName);
            w.WriteStartArray("Children");
            foreach (var hw in computer.Hardware) Hardware(w, ref id, hw);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    static void Node(Utf8JsonWriter w, ref int id, string text, string min = "", string value = "", string max = "")
    {
        w.WriteStartObject();
        w.WriteNumber("id", id++);
        w.WriteString("Text", text);
        w.WriteString("Min", min);
        w.WriteString("Value", value);
        w.WriteString("Max", max);
    }

    static void Hardware(Utf8JsonWriter w, ref int id, IHardware hw)
    {
        Node(w, ref id, hw.Name);
        w.WriteString("HardwareId", hw.Identifier.ToString());
        w.WriteStartArray("Children");
        foreach (var sub in hw.SubHardware) Hardware(w, ref id, sub);
        var sensors = hw.Sensors;
        foreach (var type in GroupOrder.Concat(Enum.GetValues<SensorType>().Except(GroupOrder)))
        {
            var group = sensors.Where(x => x.SensorType == type).OrderBy(x => x.Index).ToList();
            if (group.Count == 0) continue;
            Node(w, ref id, GroupName(type));
            w.WriteStartArray("Children");
            foreach (var s in group)
            {
                Node(w, ref id, s.Name, Format(s.SensorType, s.Min, false), Format(s.SensorType, s.Value, false),
                     Format(s.SensorType, s.Max, false));
                w.WriteString("SensorId", s.Identifier.ToString());
                w.WriteString("Type", s.SensorType.ToString());
                w.WriteString("RawMin", Format(s.SensorType, s.Min, true));
                w.WriteString("RawValue", Format(s.SensorType, s.Value, true));
                w.WriteString("RawMax", Format(s.SensorType, s.Max, true));
                w.WriteStartArray("Children");
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    static string GroupName(SensorType t) => t switch
    {
        SensorType.Voltage => "Voltages", SensorType.Current => "Currents", SensorType.Power => "Powers",
        SensorType.Clock => "Clocks", SensorType.Temperature => "Temperatures", SensorType.Load => "Load",
        SensorType.Frequency => "Frequencies", SensorType.Fan => "Fans", SensorType.Flow => "Flows",
        SensorType.Control => "Controls", SensorType.Level => "Levels", SensorType.Factor => "Factors",
        SensorType.Data or SensorType.SmallData => "Data", SensorType.Throughput => "Throughput",
        SensorType.TimeSpan => "Times", SensorType.Timing => "Timings", SensorType.Energy => "Capacities",
        SensorType.Noise => "Noise Levels", SensorType.Conductivity => "Conductivities",
        SensorType.Humidity => "Humidity Levels", _ => t.ToString(),
    };

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Value text as LibreHardwareMonitor shows it ("45.0 °C", "1236 RPM"); raw keeps
    /// throughput in B/s instead of scaling it.</summary>
    static string Format(SensorType t, float? value, bool raw)
    {
        if (value is not float f || float.IsNaN(f)) return "-";
        return t switch
        {
            SensorType.Voltage => f.ToString("F3", Inv) + " V",
            SensorType.Current => f.ToString("F3", Inv) + " A",
            SensorType.Power => f.ToString("F1", Inv) + " W",
            SensorType.Clock => f.ToString("F1", Inv) + " MHz",
            SensorType.Temperature => f.ToString("F1", Inv) + " °C",
            SensorType.Load or SensorType.Control or SensorType.Level => f.ToString("F1", Inv) + " %",
            SensorType.Frequency => f.ToString("F1", Inv) + " Hz",
            SensorType.Fan => f.ToString("F0", Inv) + " RPM",
            SensorType.Flow => f.ToString("F1", Inv) + " L/h",
            SensorType.Factor => f.ToString("F3", Inv),
            SensorType.Data => f.ToString("F1", Inv) + " GB",
            SensorType.SmallData => f.ToString("F1", Inv) + " MB",
            SensorType.Throughput when raw => f.ToString("F1", Inv) + " B/s",
            SensorType.Throughput => f < 1024 * 1024 ? (f / 1024).ToString("F1", Inv) + " KB/s" : (f / 1024 / 1024).ToString("F1", Inv) + " MB/s",
            SensorType.TimeSpan => TimeSpan.FromSeconds(f).ToString("g", Inv),
            SensorType.Timing => f.ToString("F3", Inv) + " ns",
            SensorType.Energy => f.ToString("F0", Inv) + " mWh",
            SensorType.Noise => f.ToString("F0", Inv) + " dBA",
            SensorType.Conductivity => f.ToString("F1", Inv) + " µS/cm",
            SensorType.Humidity => f.ToString("F0", Inv) + " %",
            _ => f.ToString("F2", Inv),
        };
    }
}
