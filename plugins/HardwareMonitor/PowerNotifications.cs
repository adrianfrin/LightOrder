using System.ComponentModel;
using System.Runtime.InteropServices;

// Callback registration works without a UI message loop (including service session 0).
// Best effort only: Windows may omit a notification or stop before callbacks complete.
sealed class PowerNotifications : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint Notification(IntPtr context, uint type, IntPtr setting);
    [StructLayout(LayoutKind.Sequential)]
    private struct Subscription { public IntPtr Callback; public IntPtr Context; }
    [DllImport("powrprof.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref Subscription recipient, out IntPtr handle);
    [DllImport("powrprof.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr handle);
    private readonly Notification callback;
    private IntPtr handle;

    public PowerNotifications(Action<bool> transition)
    {
        callback = (_, type, _) =>
        {
            try
            {
                if (type == 4) transition(true); // PBT_APMSUSPEND
                else if (type is 7 or 18) transition(false); // resume / automatic resume
            }
            catch (Exception e) { Console.Error.WriteLine("Power transition handling failed: " + e.Message); }
            return 0;
        };
        var subscription = new Subscription { Callback = Marshal.GetFunctionPointerForDelegate(callback) };
        var result = PowerRegisterSuspendResumeNotification(2, ref subscription, out handle); // DEVICE_NOTIFY_CALLBACK
        if (result != 0) throw new Win32Exception((int)result);
    }
    public void Dispose()
    {
        var registration = Interlocked.Exchange(ref handle, IntPtr.Zero);
        if (registration == IntPtr.Zero) return;
        var result = PowerUnregisterSuspendResumeNotification(registration);
        if (result != 0)
        {
            // Keep the delegate rooted if Windows could still call it.
            GCHandle.Alloc(callback);
            Console.Error.WriteLine("Power notification unregister failed: " + new Win32Exception((int)result).Message);
        }
        GC.KeepAlive(callback);
    }
}
