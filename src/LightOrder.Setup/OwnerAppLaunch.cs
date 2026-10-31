using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LightOrder.Setup;

internal static class OwnerAppLaunch
{
    // Use only an existing, unelevated Explorer token for the verified initiating account.
    // Never launch the interactive UI with the elevated installer's token.
    internal static bool TryLaunch(string sid, string app)
    {
        foreach (var shell in Process.GetProcessesByName("explorer"))
        using (shell)
        {
            try
            {
                if (shell.SessionId != Process.GetCurrentProcess().SessionId || !OpenProcessToken(shell.Handle, 0xB, out var token)) continue;
                using (token)
                using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                {
                    if (identity.User?.Value != sid || !GetTokenInformation(token, 20, out var elevated, sizeof(int), out _) || elevated != 0) continue;
                    if (!DuplicateTokenEx(token, 0xF01FF, IntPtr.Zero, 2, 1, out var primary)) continue;
                    using (primary)
                    {
                        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
                        if (!CreateProcessWithTokenW(primary, 0, app, new StringBuilder('"' + app + '"'), 0, IntPtr.Zero, Path.GetDirectoryName(app), ref startup, out var process)) continue;
                        CloseHandle(process.Process); CloseHandle(process.Thread); return true;
                    }
                }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title; public int X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public short Show, ReservedSize; public IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint Pid, Tid; }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int type, out int info, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes, int level, int type, out SafeAccessTokenHandle primary);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, uint logon, string application, StringBuilder command, uint flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
