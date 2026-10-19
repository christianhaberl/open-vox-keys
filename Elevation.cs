using System.Runtime.InteropServices;

// Token elevation of a process; used to explain why the hotkey is invisible behind an administrator window.
static class Elevation
{
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint OpenProcess(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(nint token, int cls, out int value, int length, out int returned);
    internal static nint ForegroundWindow() => GetForegroundWindow();
    internal static int WindowProcess(nint window) { GetWindowThreadProcessId(window, out uint id); return (int)id; }
    internal static bool IsElevated(int processId)
    {
        nint process = OpenProcess(0x1000, false, (uint)processId); if (process == 0) return false;
        try
        {
            if (!OpenProcessToken(process, 0x8, out nint token)) return false;
            try { return GetTokenInformation(token, 20, out int elevated, 4, out _) && elevated != 0; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }
}
