using ControlCenter.Contracts;

namespace ControlCenter.GigabyteIt5702;

// A named Mutex is thread-affine. Its owner thread stays alive across asynchronous RPC calls
// and releases it on that same thread. The Local namespace confines ownership to this session.
internal sealed class ControllerLease : IDisposable
{
    private readonly ManualResetEventSlim release = new(false);
    private readonly Thread owner;
    private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;

    public ControllerLease(string deviceId)
    {
        owner = new Thread(() => Own(deviceId)) { IsBackground = true, Name = "IT5702 ownership" };
        owner.Start();
        try { ready.Task.GetAwaiter().GetResult(); }
        catch { Dispose(); throw; }
    }

    private void Own(string deviceId)
    {
        try
        {
            using var mutex = new Mutex(false, @"Local\IndependentControlCenter.IT5702." + deviceId);
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
                throw new PluginException("DeviceBusy", "Another Control Center instance owns this controller in the current Windows session.");
            try
            {
                ready.TrySetResult(true);
                release.Wait();
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception error) { ready.TrySetException(error); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        release.Set();
        owner.Join();
        release.Dispose();
    }
}
