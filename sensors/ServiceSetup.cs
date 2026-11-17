using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;

namespace CongeeSensors;

/// <summary>
/// --install / --uninstall, run (as administrator) by congee's setup. The service runs as
/// LocalSystem, so its files live under Program Files, where only administrators can change them:
/// a copy in the user's own folder could be swapped by any program the user runs.
/// Exit codes: 0 done, 1 failed, 5 not running as administrator.
/// </summary>
static class ServiceSetup
{
    static string Target => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "congee", "sensors");
    static string TargetExe => Path.Combine(Target, "congee-sensors.exe");

    static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    static ServiceController? Find() =>
        ServiceController.GetServices().FirstOrDefault(s => s.ServiceName.Equals(Program.ServiceName, StringComparison.OrdinalIgnoreCase));

    public static int Install()
    {
        if (!IsAdmin()) { Console.Error.WriteLine("Run --install as administrator."); return 5; }
        try
        {
            using (var existing = Find())
                if (existing != null) StopService(existing);

            string source = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');
            if (!source.Equals(Target, StringComparison.OrdinalIgnoreCase))
            {
                // Fresh copy: remove what's there (old versions' files) and copy this folder.
                if (Directory.Exists(Target)) Directory.Delete(Target, true);
                CopyDir(source, Target);
            }

            using (var existing = Find())
            {
                if (existing == null)
                    Sc($"create {Program.ServiceName} binPath= \"\\\"{TargetExe}\\\"\" start= auto DisplayName= \"congee sensors\"");
                else
                    Sc($"config {Program.ServiceName} binPath= \"\\\"{TargetExe}\\\"\" start= auto DisplayName= \"congee sensors\"");
            }
            Sc($"description {Program.ServiceName} \"Reads temperatures, fans and GPU stats for congee (LibreHardwareMonitorLib), " +
               $"served to this PC only on 127.0.0.1:{Program.Port}.\"");
            Sc($"failure {Program.ServiceName} reset= 86400 actions= restart/5000/restart/30000//");

            using var svc = Find() ?? throw new InvalidOperationException("the service wasn't created");
            svc.Start();
            svc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            Console.WriteLine("Installed and started " + Program.ServiceName + " from " + Target);
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Install failed: " + e.Message);
            return 1;
        }
    }

    public static int Uninstall()
    {
        if (!IsAdmin()) { Console.Error.WriteLine("Run --uninstall as administrator."); return 5; }
        try
        {
            using (var svc = Find())
            {
                if (svc != null)
                {
                    StopService(svc);
                    Sc($"delete {Program.ServiceName}");
                }
            }
            if (Directory.Exists(Target))
            {
                string self = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');
                if (self.Equals(Target, StringComparison.OrdinalIgnoreCase))
                    // Can't delete our own running exe: remove the folder just after we exit.
                    Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{Target}\"")
                        { CreateNoWindow = true, UseShellExecute = false });
                else
                    Directory.Delete(Target, true);
            }
            string parent = Path.GetDirectoryName(Target)!;
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any(e => !e.Equals(Target, StringComparison.OrdinalIgnoreCase)))
                try { if (!Directory.Exists(Target)) Directory.Delete(parent); } catch (IOException) { }
            Console.WriteLine("Removed " + Program.ServiceName);
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Uninstall failed: " + e.Message);
            return 1;
        }
    }

    static void StopService(ServiceController svc)
    {
        svc.Refresh();
        if (svc.Status is ServiceControllerStatus.Stopped) return;
        if (svc.Status is not ServiceControllerStatus.StopPending) svc.Stop();
        svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
    }

    static void Sc(string args)
    {
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), args)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"sc {args.Split(' ')[0]} failed ({p.ExitCode}): {output.Trim()}");
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (string d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
