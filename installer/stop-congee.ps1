# Stops every running congee (both editions) so setup can replace or remove its files.
# First it closes each one's hidden "congee tray" window, which both editions treat like
# "Exit congee" (icon removed from the tray, clean shutdown). taskkill without /f can't do
# this: it skips invisible and tool windows, which is all congee has. Anything still
# running after a few seconds is force-stopped.
$procs = @(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -eq 'congee.exe' -or ($_.Name -match '^pythonw?\.exe$' -and $_.CommandLine -like '*congee.py*')
})
if ($procs.Count -eq 0) { exit 0 }

Add-Type @'
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class CongeeStop {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    public static void CloseTrays(uint[] pids) {
        var set = new HashSet<uint>(pids);
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            var sb = new StringBuilder(64); GetWindowText(h, sb, 64);
            if (set.Contains(p) && sb.ToString() == "congee tray") PostMessage(h, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }
}
'@
[CongeeStop]::CloseTrays([uint32[]]($procs | ForEach-Object { $_.ProcessId }))
for ($i = 0; $i -lt 30; $i++) {
    if (-not ($procs | Where-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })) { exit 0 }
    Start-Sleep -Milliseconds 100
}
foreach ($p in $procs) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
