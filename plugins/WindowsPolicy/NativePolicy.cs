using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ControlCenter.Contracts;
using Microsoft.Win32.SafeHandles;

namespace ControlCenter.WindowsPolicy;

internal sealed record Scheme(Guid Id, string Name);
internal sealed record ProcessState(int Pid, string StartTimeUtc, string Name, string Priority,
    string AffinityMask, string SystemAffinityMask, int ProcessorGroupCount);

internal interface IProcessTarget : IDisposable
{
    string StartTimeUtc { get; }
    string ReadPriority();
    string ReadAffinity();
    string SystemAffinity { get; }
    void SetPriority(string value);
    void SetAffinity(string value);
}

internal interface IPolicySystem
{
    IReadOnlyList<Scheme> ListSchemes();
    Guid ReadActiveScheme();
    void SetActiveScheme(Guid id);
    int[] ListProcessIds();
    string ProcessName(int pid);
    int ProcessorGroupCount { get; }
    IProcessTarget OpenProcess(int pid, bool writable);
}

internal static class PolicyValues
{
    internal static readonly string[] Priorities = ["Idle", "BelowNormal", "Normal", "AboveNormal", "High"];
    internal static string ValidatePriority(string value)
    {
        if (!Priorities.Contains(value, StringComparer.Ordinal))
            throw new PluginException("InvalidArgument", "priority must be Idle, BelowNormal, Normal, AboveNormal, or High; RealTime is forbidden.");
        return value;
    }

    internal static ulong ParseMask(string text)
    {
        ReadOnlySpan<char> digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text.AsSpan(2) : text.AsSpan();
        if (digits.IsEmpty || digits.Length > 16 ||
            !ulong.TryParse(digits, System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out var value) || value == 0)
            throw new PluginException("InvalidArgument", "affinityMask must be a nonzero hexadecimal string, for example F or 0xF.");
        return value;
    }

    internal static string FormatMask(ulong value) => $"0x{value:X}";
    internal static string ValidateMask(string value, string available, int groups)
    {
        if (groups != 1)
            throw new PluginException("Unsupported", "Affinity writes are disabled on systems with multiple processor groups; a single mask cannot represent them safely.");
        ulong mask = ParseMask(value), system = ParseMask(available);
        if ((mask & ~system) != 0 || (IntPtr.Size == 4 && mask > uint.MaxValue))
            throw new PluginException("InvalidArgument", "affinityMask must be a subset of the system affinity mask and fit the plugin architecture.");
        return FormatMask(mask);
    }

    internal static string NormalizeStartTime(string text)
    {
        // Require a UTC designator; never interpret an unspecified local wall clock.
        if (!(text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal)) ||
            !DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date))
            throw new PluginException("InvalidArgument", "startTimeUtc must be the exact UTC timestamp returned by process.list.");
        return date.UtcDateTime.ToString("O");
    }
}

internal sealed class NativePolicy : IPolicySystem
{
    public int ProcessorGroupCount => Native.GetActiveProcessorGroupCount();

    public IReadOnlyList<Scheme> ListSchemes()
    {
        var result = new List<Scheme>();
        for (uint index = 0; ; index++)
        {
            uint size = 16;
            byte[] buffer = new byte[size];
            uint error = Native.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16, index, buffer, ref size);
            if (error == 259) break; // ERROR_NO_MORE_ITEMS
            Check(error, "PowerEnumerate");
            if (size != 16) throw new PluginException("DeviceError", "PowerEnumerate returned an invalid GUID length.");
            var id = new Guid(buffer);
            uint nameSize = 0;
            error = Native.PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, null, ref nameSize);
            if (error != 0 && error != 234) Check(error, "PowerReadFriendlyName(size)");
            if (nameSize == 0 || nameSize > 1024 * 1024)
                throw new PluginException("DeviceError", "Power scheme friendly name has an invalid length.");
            byte[] name = new byte[nameSize];
            Check(Native.PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, name, ref nameSize), "PowerReadFriendlyName");
            result.Add(new(id, Encoding.Unicode.GetString(name, 0, checked((int)nameSize)).TrimEnd('\0')));
        }
        return result;
    }

    public Guid ReadActiveScheme()
    {
        Check(Native.PowerGetActiveScheme(IntPtr.Zero, out var pointer), "PowerGetActiveScheme");
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { Native.LocalFree(pointer); }
    }

    public void SetActiveScheme(Guid id) => Check(Native.PowerSetActiveScheme(IntPtr.Zero, ref id), "PowerSetActiveScheme");

    public int[] ListProcessIds()
    {
        var processes = Process.GetProcesses();
        try { return processes.Select(p => p.Id).Order().ToArray(); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public string ProcessName(int pid)
    {
        using var process = Process.GetProcessById(pid);
        return process.ProcessName;
    }

    public IProcessTarget OpenProcess(int pid, bool writable) => new NativeTarget(pid, writable);

    internal static void Check(uint code, string operation)
    {
        if (code == 0) return;
        string category = code switch
        {
            5 or 1314 => "AccessDenied",
            1168 => "ProcessUnavailable",
            _ => "NativeError"
        };
        throw new PluginException(category, $"{operation} failed (Win32 {code}): {new Win32Exception(unchecked((int)code)).Message}");
    }

    private sealed class NativeTarget : IProcessTarget
    {
        private readonly SafeProcessHandle handle;
        public string StartTimeUtc { get; }
        public NativeTarget(int pid, bool writable)
        {
            // QueryInformation is required by GetProcessAffinityMask. The handle pins identity,
            // so a reused PID cannot redirect a write after creation-time validation.
            handle = Native.OpenProcess(0x0400u | (writable ? 0x0200u : 0), false, pid);
            if (handle.IsInvalid)
            {
                uint error = unchecked((uint)Marshal.GetLastWin32Error());
                handle.Dispose();
                if (error == 87) throw new PluginException("ProcessUnavailable", "OpenProcess failed: process no longer exists or PID is not openable (Win32 87).");
                Check(error, "OpenProcess");
                throw new PluginException("ProcessUnavailable", "OpenProcess returned an invalid handle.");
            }
            try
            {
                if (!Native.GetProcessTimes(handle, out long creation, out _, out _, out _)) LastError("GetProcessTimes");
                StartTimeUtc = DateTime.FromFileTimeUtc(creation).ToString("O");
            }
            catch { handle.Dispose(); throw; }
        }

        public string ReadPriority()
        {
            uint priority = Native.GetPriorityClass(handle);
            if (priority == 0) LastError("GetPriorityClass");
            return priority switch
            {
                0x40 => "Idle", 0x4000 => "BelowNormal", 0x20 => "Normal",
                0x8000 => "AboveNormal", 0x80 => "High", 0x100 => "RealTime",
                _ => throw new PluginException("Unsupported", $"Unrecognized priority class: {priority}.")
            };
        }
        public string ReadAffinity() => PolicyValues.FormatMask(ReadMasks().Process);
        public string SystemAffinity => PolicyValues.FormatMask(ReadMasks().System);
        private (ulong Process, ulong System) ReadMasks()
        {
            if (!Native.GetProcessAffinityMask(handle, out var process, out var system)) LastError("GetProcessAffinityMask");
            return (process.ToUInt64(), system.ToUInt64());
        }
        public void SetPriority(string value)
        {
            PolicyValues.ValidatePriority(value);
            uint native = value switch
            {
                "Idle" => 0x40, "BelowNormal" => 0x4000, "Normal" => 0x20,
                "AboveNormal" => 0x8000, "High" => 0x80, "RealTime" => 0x100,
                _ => throw new PluginException("InvalidArgument", "Invalid priority class in journal.")
            };
            if (!Native.SetPriorityClass(handle, native)) LastError("SetPriorityClass");
        }
        public void SetAffinity(string value)
        {
            var mask = new UIntPtr(PolicyValues.ParseMask(value));
            if (!Native.SetProcessAffinityMask(handle, mask)) LastError("SetProcessAffinityMask");
        }
        public void Dispose() => handle.Dispose();
        private static void LastError(string operation) => Check(unchecked((uint)Marshal.GetLastWin32Error()), operation);
    }

    private static class Native
    {
        [DllImport("powrprof.dll")] internal static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr subgroup, uint access, uint index, [Out] byte[] buffer, ref uint size);
        [DllImport("powrprof.dll")] internal static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, [Out] byte[]? buffer, ref uint size);
        [DllImport("powrprof.dll")] internal static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] internal static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr pointer);
        [DllImport("kernel32.dll")] internal static extern ushort GetActiveProcessorGroupCount();
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetPriorityClass(SafeProcessHandle process);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetPriorityClass(SafeProcessHandle process, uint priority);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessAffinityMask(SafeProcessHandle process, out UIntPtr processMask, out UIntPtr systemMask);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetProcessAffinityMask(SafeProcessHandle process, UIntPtr mask);
    }
}
