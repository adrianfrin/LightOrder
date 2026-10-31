using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LightOrder.Service;

// No command lines, usernames, or executable paths are returned or logged.
public sealed class WindowsAutomationProcessSource : ITargetedAutomationProcessSource
{
    private readonly string ownerSid;
    public WindowsAutomationProcessSource(string ownerSid) => this.ownerSid = ServiceSecurity.ValidateOwnerSid(ownerSid).Value;

    public IReadOnlyList<AutomationProcessSnapshot> Snapshot() =>
        throw new InvalidOperationException("Windows automation requires explicit target executable names.");

    public IReadOnlyList<AutomationProcessSnapshot> Snapshot(string[] processNames)
    {
        if (processNames is null || processNames.Length > 64 || processNames.Any(n => !AutomationValidation.ValidProcessName(n)))
            throw new ArgumentException("Invalid target executable names.", nameof(processNames));
        var targets = processNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0) return [];
        var snapshots = new List<AutomationProcessSnapshot>();
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    // ProcessName comes from enumeration metadata. Filter before querying sessions, handles or token ownership.
                    if (!MatchesEnumeratedName(process.ProcessName, targets)) continue;
                    var pid = process.Id;
                    if (pid <= 0 || process.SessionId <= 0) continue;
                    using var handle = OpenProcess(0x1000, false, pid); // QUERY_LIMITED_INFORMATION
                    if (handle.IsInvalid)
                    {
                        var code = Marshal.GetLastWin32Error();
                        if (code == 87) continue; // Process already exited.
                        throw new Win32Exception(code, "Cannot inspect an interactive process safely.");
                    }
                    if (!OpenProcessToken(handle, 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    using (token)
                    using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                        if (identity.User?.Value != ownerSid) continue;
                    if (!GetProcessTimes(handle, out var created, out var exited, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (exited != 0) continue;
                    var buffer = new StringBuilder(32768);
                    var length = buffer.Capacity;
                    if (!QueryFullProcessImageName(handle, 0, buffer, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var name = Path.GetFileName(buffer.ToString());
                    // Recheck the actual opened process image: the PID may have been reused after enumeration.
                    if (!targets.Contains(name)) continue;
                    snapshots.Add(new(pid, new DateTimeOffset(DateTime.FromFileTimeUtc(created)), name, process.SessionId));
                }
                catch (InvalidOperationException) { /* Process exited during enumeration. */ }
                catch (Win32Exception e) when (e.NativeErrorCode == 87) { /* Process exited. */ }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        return snapshots;
    }

    internal static bool MatchesEnumeratedName(string processName, IReadOnlySet<string> targets) =>
        targets.Contains(processName + ".exe");

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int length);
}
