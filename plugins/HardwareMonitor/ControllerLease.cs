// Shared across service accounts, interactive CLI sessions, and calibration. Unlike Mutex,
// a named Semaphore may be released after an async continuation changes threads.
sealed class ControllerLease : IDisposable
{
    private readonly Semaphore semaphore;
    private bool disposed;
    public ControllerLease()
    {
        semaphore = new Semaphore(1, 1, @"Global\LightOrder.ValidatedFanController.v1");
        try
        {
            if (!semaphore.WaitOne(0)) throw new InvalidOperationException("Another fan controller owns the validated channels.");
        }
        catch { semaphore.Dispose(); throw; }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { semaphore.Release(); } finally { semaphore.Dispose(); }
    }
}
