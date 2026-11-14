using System.Globalization;
using System.Windows;

namespace Congee;

public static class Program
{
    const string Help = """
        congee - conky-style desktop system monitor for Windows (.NET edition)

        Usage: congee [options]
          -c, --config FILE   config file (default: first found in the search list below)
          --once              print one evaluated frame as plain text and exit
          --sensors           list LibreHardwareMonitor sensors and exit
          --interfaces        list network interface names and exit
          --create-config     copy the bundled default config to %APPDATA%\congee\congee.conf
          --which-config      print the config file that would be used and exit
          -h, --help          show this help

        Config search order:
        """;

    [STAThread]
    public static int Main(string[] argv)
    {
        // Numbers/dates format the same everywhere (and match the Python version).
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        string? config = null;
        string? mode = null;
        for (int i = 0; i < argv.Length; i++)
        {
            switch (argv[i])
            {
                case "-c" or "--config" when i + 1 < argv.Length: config = argv[++i]; break;
                case "--once" or "--sensors" or "--interfaces" or "--create-config" or "--which-config":
                    mode = argv[i]; break;
                case "-h" or "--help" or "/?": mode = "--help"; break;
                default:
                    Native.AttachConsole(-1);
                    Console.Error.WriteLine($"congee: unknown option {argv[i]} (try --help)");
                    return 2;
            }
        }
        config ??= Config.Find();

        if (mode != null)
        {
            Native.AttachConsole(-1);  // WinExe: borrow the parent's console for CLI output
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine();
            return RunCli(mode, config);
        }

        bool logging = true;
        try { logging = Config.Yes(Config.Parse(config).settings["logging"]); } catch { }
        Log.Started(Path.GetFullPath(config), logging);
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.SessionEnding += (_, e) =>
        {
            Log.StopReason($"Windows is {(e.ReasonSessionEnding == ReasonSessionEnding.Shutdown ? "shutting down" : "signing out")}");
            Log.Stopped(0);
        };
        var win = new CongeeWindow(config);
        app.MainWindow = win;
        win.Show();
        int code = app.Run();
        Log.Stopped(code);
        return code;
    }

    static int RunCli(string mode, string config)
    {
        switch (mode)
        {
            case "--help":
                Console.WriteLine(Help);
                foreach (var p in Config.SearchPaths) Console.WriteLine("  " + p);
                return 0;

            case "--which-config":
                Console.WriteLine(Path.GetFullPath(config));
                return 0;

            case "--create-config":
                if (File.Exists(Config.UserConfig))
                {
                    Console.WriteLine($"Already exists, not overwriting: {Config.UserConfig}");
                    return 0;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Config.UserConfig)!);
                File.Copy(Config.BundledConfig, Config.UserConfig);
                Console.WriteLine($"Created {Config.UserConfig}");
                return 0;

            case "--interfaces":
            {
                var st = new Stats();
                foreach (var (name, up, ip) in st.Interfaces())
                    Console.WriteLine($"{"'" + name + "'",-40} {(up ? "up" : "down"),-5} {ip}");
                return 0;
            }

            case "--sensors":
            {
                var settings = File.Exists(config) ? Config.Parse(config).settings : new(Config.Defaults);
                var sens = new Sensors();
                sens.Configure(settings["sensors_url"], 1.0);
                sens.RefreshNow();
                if (!sens.Ok)
                {
                    Console.WriteLine($"Can't reach LibreHardwareMonitor at {sens.Url}: {sens.Error}");
                    Console.WriteLine("Start LibreHardwareMonitor and enable Options > Remote Web Server > Run.");
                    return 1;
                }
                foreach (var s in sens.Get())
                    Console.WriteLine($"{s.Type,-12} {s.Display,14}   {s.Path}");
                var gpus = sens.Devices("/gpu");
                var sel = sens.SelectGpu(settings.GetValueOrDefault("gpu", "auto"));
                Console.WriteLine();
                for (int i = 0; i < gpus.Count; i++)
                    Console.WriteLine($"GPU {i}: {gpus[i].name}  ({gpus[i].id}){(sel?.id == gpus[i].id ? "  <- used by gpu_* variables" : "")}");
                if (gpus.Count == 0) Console.WriteLine("No GPU found in LibreHardwareMonitor's data.");
                var cpus = sens.Devices("/amdcpu").Concat(sens.Devices("/intelcpu")).Concat(sens.Devices("/cpu")).ToList();
                Console.WriteLine(cpus.Count > 0 ? $"CPU: {cpus[0].name}  ({cpus[0].id})" : "No CPU sensors found.");
                return 0;
            }

            case "--once":
            {
                var (settings, text) = Config.Parse(config);
                var st = new Stats();
                st.Sensors.Configure(settings["sensors_url"], 1.0);
                st.Sensors.RefreshNow();
                st.WaitForDrives(TimeSpan.FromSeconds(15));  // include network/DVD drives, scanned in the background
                var ev = new Evaluator(st, settings, 1.0, Config.IsHex6);
                var tokens = Config.Tokenize(text);
                st.Sample();
                ev.Build(tokens);  // primes process CPU counters and exec commands
                Thread.Sleep(1000);
                st.Sample();
                Console.WriteLine(CongeeWindow.RenderText(ev.Build(tokens)));
                return 0;
            }
        }
        return 0;
    }
}
