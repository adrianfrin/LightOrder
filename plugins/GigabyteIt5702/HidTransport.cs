using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ControlCenter.Contracts;
using Microsoft.Win32.SafeHandles;

namespace ControlCenter.GigabyteIt5702;

internal sealed record HidEndpoint(string Path)
{
    // Windows interface paths survive ordinary process restarts and are case-insensitive.
    // A USB topology / device-instance change can produce a different ID.
    public string Id => "it5702-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.ToUpperInvariant()))).ToLowerInvariant();
}

internal static class HidTransport
{
    private const int ErrorNoMoreItems = 259;
    private const int ErrorInsufficientBuffer = 122;

    public static HidEndpoint[] Enumerate()
    {
        Native.HidD_GetHidGuid(out Guid guid);
        using var devices = Native.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (devices.IsInvalid) throw Failure("Enumerate HID interfaces");
        var endpoints = new List<HidEndpoint>();
        for (uint index = 0; ; index++)
        {
            var item = new Native.InterfaceData { Size = Marshal.SizeOf<Native.InterfaceData>() };
            if (!Native.SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref guid, index, ref item))
            {
                if (Marshal.GetLastWin32Error() == ErrorNoMoreItems) break;
                throw Failure("Enumerate HID interface");
            }
            bool sized = Native.SetupDiGetDeviceInterfaceDetail(devices, ref item, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
            int sizingError = Marshal.GetLastWin32Error();
            if (sized || sizingError != ErrorInsufficientBuffer || required < 8 || required > 65536)
                throw new PluginException("DeviceError", "Invalid HID interface detail size.");
            IntPtr detail = Marshal.AllocHGlobal(checked((int)required));
            try
            {
                // cbSize includes architecture-specific padding; DevicePath always starts at byte 4.
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!Native.SetupDiGetDeviceInterfaceDetail(devices, ref item, detail, required, out _, IntPtr.Zero))
                    throw Failure("Read HID interface detail");
                string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4)) ?? "";
                if (!path.Contains("vid_048d&pid_5702", StringComparison.OrdinalIgnoreCase)) continue;
                using var handle = Open(path, exclusive: false);
                if (IsSupported(handle)) endpoints.Add(new(path));
            }
            finally { Marshal.FreeHGlobal(detail); }
        }
        return endpoints.ToArray();
    }

    public static SafeFileHandle OpenForApply(HidEndpoint endpoint)
    {
        var handle = Open(endpoint.Path, exclusive: true);
        try
        {
            if (!IsSupported(handle))
                throw new PluginException("DeviceChanged", "Endpoint no longer matches VID 048D / PID 5702 / usage FF89:CC / feature length 64.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static SafeFileHandle Open(string path, bool exclusive)
    {
        // Metadata enumeration uses zero desired access. Applying requests read/write access
        // and denies sharing for the duration of the transaction to reject competing handles.
        var handle = Native.CreateFile(path, exclusive ? 0xc0000000u : 0u, exclusive ? 0u : 3u,
            IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw Failure("Open IT5702 HID endpoint", error);
    }

    private static bool IsSupported(SafeFileHandle handle)
    {
        var attributes = new Native.Attributes { Size = Marshal.SizeOf<Native.Attributes>() };
        if (!Native.HidD_GetAttributes(handle, ref attributes)) throw Failure("Read HID attributes");
        if (attributes.Vendor != 0x048d || attributes.Product != 0x5702) return false;
        if (!Native.HidD_GetPreparsedData(handle, out var data))
        {
            data?.Dispose();
            throw Failure("Read HID capabilities");
        }
        using (data)
        {
            if (Native.HidP_GetCaps(data, out var caps) != 0x110000)
                throw new PluginException("DeviceError", "HID capability parsing failed.");
            return caps.UsagePage == 0xff89 && caps.Usage == 0xcc && caps.FeatureLength == 64;
        }
    }

    public static void Send(SafeFileHandle handle, byte[] report)
    {
        if (report.Length != It5702Protocol.ReportLength) throw new ArgumentException("Feature report must contain 64 bytes.");
        if (!Native.HidD_SetFeature(handle, report, report.Length)) throw Failure("Send HID feature report");
    }

    public static byte[] ReadIdentification(SafeFileHandle handle)
    {
        var report = new byte[It5702Protocol.ReportLength];
        report[0] = 0xcc;
        if (!Native.HidD_GetFeature(handle, report, report.Length)) throw Failure("Read controller identification");
        return report;
    }

    // Explicit diagnostic CLI only. One identification write and three reads; never
    // call any lighting command or relax the production identification guard.
    public static void DiagnoseIdentification(bool readWrite)
    {
        if (!OperatingSystem.IsWindows())
            throw new PluginException("PlatformUnsupported", "识别诊断需要 Windows HID API。");
        var endpoints = Enumerate();
        if (endpoints.Length != 1)
            throw new PluginException("AmbiguousDevice", $"识别诊断要求恰好一个匹配端点，实际为 {endpoints.Length}；未发送命令。");
        var endpoint = endpoints[0];
        using var lease = new ControllerLease(endpoint.Id);
        using var handle = Open(endpoint.Path, exclusive: readWrite);
        if (!IsSupported(handle))
            throw new PluginException("DeviceChanged", "端点属性发生变化；未发送命令。");
        Console.WriteLine($"deviceId={endpoint.Id} access={(readWrite ? "GENERIC_READ|GENERIC_WRITE" : "0")} share={(readWrite ? "0 (exclusive)" : "READ|WRITE (historical)")}");
        var request = It5702Protocol.Command(0x60);
        Console.WriteLine($"identificationRequest={Convert.ToHexString(request)}");
        Send(handle, request);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool readFailed = false;
        foreach (int targetMs in new[] { 0, 50, 200 })
        {
            // Targets are elapsed times after SetFeature returns, not cumulative delays.
            int waitMs = targetMs - (int)clock.ElapsedMilliseconds;
            if (waitMs > 0) Thread.Sleep(waitMs);
            long startedMs = clock.ElapsedMilliseconds;
            try
            {
                byte[] report = ReadIdentification(handle);
                Console.WriteLine($"targetMs={targetMs} startedMs={startedMs} completedMs={clock.ElapsedMilliseconds} report={Convert.ToHexString(report)}");
            }
            catch (Exception error)
            {
                readFailed = true;
                Console.Error.WriteLine($"targetMs={targetMs} startedMs={startedMs} completedMs={clock.ElapsedMilliseconds} error={error.Message}");
            }
        }
        Console.WriteLine("识别诊断结束：仅发送一次 CC60 识别请求，最多读取三次；未发送灯光命令。报告未经照明状态验证。");
        if (readFailed) throw new PluginException("DeviceError", "部分识别读取失败，详见诊断输出。");
    }

    private static PluginException Failure(string operation, int? error = null)
    {
        int code = error ?? Marshal.GetLastWin32Error();
        string reason = code is 5 or 32 or 33 ? "DeviceBusy" : "DeviceError";
        return new(reason, $"{operation} failed: {new Win32Exception(code).Message} (Win32 {code}). " +
            "Close RGB Fusion/GCC, liquidctl, or OpenRGB motherboard scans before retrying. No state verification is available.");
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct InterfaceData { public int Size; public Guid Class; public uint Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Attributes { public int Size; public ushort Vendor, Product, Version; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Caps
        {
            public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort LinkNodes, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues,
                OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
        }

        internal sealed class DeviceInfoSet : SafeHandleZeroOrMinusOneIsInvalid
        {
            public DeviceInfoSet() : base(true) { }
            protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle);
        }
        internal sealed class PreparsedData : SafeHandleZeroOrMinusOneIsInvalid
        {
            public PreparsedData() : base(true) { }
            protected override bool ReleaseHandle() => HidD_FreePreparsedData(handle);
        }

        [DllImport("hid.dll", ExactSpelling = true)]
        internal static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", ExactSpelling = true, SetLastError = true)]
        internal static extern DeviceInfoSet SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr window, uint flags);
        [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiEnumDeviceInterfaces(DeviceInfoSet devices, IntPtr device, ref Guid guid, uint index, ref InterfaceData item);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInterfaceDetail(DeviceInfoSet devices, ref InterfaceData item, IntPtr detail, uint size, out uint required, IntPtr device);
        [DllImport("setupapi.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
        [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out PreparsedData data);
        [DllImport("hid.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll", ExactSpelling = true)]
        internal static extern int HidP_GetCaps(PreparsedData data, out Caps caps);
        [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_SetFeature(SafeFileHandle handle, [In] byte[] report, int length);
        [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetFeature(SafeFileHandle handle, [In, Out] byte[] report, int length);
    }
}
