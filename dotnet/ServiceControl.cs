using System.Runtime.InteropServices;

namespace Congee;

/// <summary>
/// Starts and stops the congee sensor service, so it only runs while congee does. Setup installs
/// the service as manual-start and lets signed-in users start and stop it (only that, not change
/// or remove it), so this needs no admin rights. Services installed by congee 1.3.0 start with
/// Windows and don't give users that right: then these calls just fail and the service keeps
/// running on its own, as it did before.
/// </summary>
static class ServiceControl
{
    public const string Name = "congee-sensors";

    const uint SC_MANAGER_CONNECT = 0x1;
    const uint SERVICE_QUERY_STATUS = 0x4, SERVICE_START = 0x10, SERVICE_STOP = 0x20;
    const uint SERVICE_CONTROL_STOP = 1;
    const uint STOPPED = 1, START_PENDING = 2, STOP_PENDING = 3;
    const int ERROR_SERVICE_ALREADY_RUNNING = 1056;

    [StructLayout(LayoutKind.Sequential)]
    struct SERVICE_STATUS { public uint Type, CurrentState, ControlsAccepted, Win32ExitCode, SpecificExitCode, CheckPoint, WaitHint; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenSCManager(string? machine, string? db, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenService(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool QueryServiceStatus(IntPtr svc, out SERVICE_STATUS status);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool StartService(IntPtr svc, int argc, IntPtr argv);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool ControlService(IntPtr svc, uint control, out SERVICE_STATUS status);
    [DllImport("advapi32.dll")]
    static extern bool CloseServiceHandle(IntPtr h);

    /// <summary>Runs f with a handle to the service, or returns why it couldn't be opened.</summary>
    static string? With(uint access, Func<IntPtr, string?> f)
    {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return "can't reach the service manager (" + Marshal.GetLastWin32Error() + ")";
        try
        {
            IntPtr svc = OpenService(scm, Name, access);
            if (svc == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                return err switch
                {
                    1060 => "the service isn't installed",
                    5 => "not allowed to start or stop it: run congee's setup again and approve the admin prompt, so it can update the service",
                    _ => "can't open the service (" + err + ")",
                };
            }
            try { return f(svc); }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>Starts the service if it's stopped (waiting out a stop still in progress first).
    /// Returns null if this call started it, "running" if it already was, else why not.</summary>
    public static string? Start() => With(SERVICE_QUERY_STATUS | SERVICE_START, svc =>
    {
        var deadline = Environment.TickCount64 + 20_000;
        while (QueryServiceStatus(svc, out var st) && st.CurrentState == STOP_PENDING && Environment.TickCount64 < deadline)
            Thread.Sleep(250);
        if (QueryServiceStatus(svc, out var now) && now.CurrentState != STOPPED) return "running";
        if (StartService(svc, 0, IntPtr.Zero)) return null;
        int err = Marshal.GetLastWin32Error();
        return err == ERROR_SERVICE_ALREADY_RUNNING ? "running" : "couldn't start it (" + err + ")";
    });

    /// <summary>Asks the service to stop (it finishes on its own). Returns null if asked, else why not.</summary>
    public static string? Stop() => With(SERVICE_QUERY_STATUS | SERVICE_STOP, svc =>
    {
        if (QueryServiceStatus(svc, out var st) && st.CurrentState is STOPPED or STOP_PENDING) return "not running";
        if (st.CurrentState == START_PENDING)  // can't stop it mid-start: give it a moment
        {
            var deadline = Environment.TickCount64 + 10_000;
            while (QueryServiceStatus(svc, out st) && st.CurrentState == START_PENDING && Environment.TickCount64 < deadline)
                Thread.Sleep(250);
        }
        return ControlService(svc, SERVICE_CONTROL_STOP, out _) ? null : "couldn't stop it (" + Marshal.GetLastWin32Error() + ")";
    });
}
