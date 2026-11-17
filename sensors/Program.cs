using System.ServiceProcess;

namespace CongeeSensors;

/// <summary>
/// congee-sensors: reads temperatures, fans and GPU stats with LibreHardwareMonitorLib (which
/// needs administrator rights and the PawnIO driver for CPU and motherboard sensors) and serves
/// them to congee on http://127.0.0.1:8086/data.json, in the same JSON format as
/// LibreHardwareMonitor's own web server. Runs as a Windows service, so congee itself never
/// needs admin rights and there is no extra window or tray icon.
///
///   congee-sensors.exe               run as the service (started by Windows)
///   congee-sensors.exe --console     run in this window instead, for testing (as administrator)
///   congee-sensors.exe --install     copy to Program Files and install + start the service (admin)
///   congee-sensors.exe --uninstall   stop and remove the service and its files (admin)
/// </summary>
static class Program
{
    public const string ServiceName = "congee-sensors";
    public const int Port = 8086;

    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        int port = Port;
        int pi = Array.FindIndex(args, a => a.Equals("--port", StringComparison.OrdinalIgnoreCase));
        if (pi >= 0 && pi + 1 < args.Length && int.TryParse(args[pi + 1], out int p)) port = p;

        switch (mode)
        {
            case "--install": return ServiceSetup.Install();
            case "--uninstall": return ServiceSetup.Uninstall();
            case "--console":
                using (var server = new SensorServer(port, Console.WriteLine))
                {
                    server.Start();
                    Console.WriteLine($"Serving http://127.0.0.1:{port}/data.json - press Enter to stop.");
                    if (Console.ReadLine() == null) Thread.Sleep(Timeout.Infinite);  // no input attached: run until ended
                }
                return 0;
            default:
                if (Environment.UserInteractive)
                {
                    Console.WriteLine("congee-sensors is a Windows service. Options: --console, --install, --uninstall");
                    return 2;
                }
                ServiceBase.Run(new SensorService(port));
                return 0;
        }
    }
}

sealed class SensorService : ServiceBase
{
    readonly int port;
    SensorServer? server;

    public SensorService(int port)
    {
        this.port = port;
        ServiceName = Program.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = true;  // start / stop / failures go to the Application event log
    }

    protected override void OnStart(string[] args)
    {
        server = new SensorServer(port, msg => EventLog.WriteEntry(msg, System.Diagnostics.EventLogEntryType.Warning));
        server.Start();
    }

    protected override void OnStop() { server?.Dispose(); server = null; }
    protected override void OnShutdown() => OnStop();
}
