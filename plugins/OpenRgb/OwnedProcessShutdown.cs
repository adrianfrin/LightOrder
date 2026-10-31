using System.Diagnostics;

namespace ControlCenter.OpenRgb;

internal interface IOwnedProcess
{
    bool HasExited { get; }
    bool CloseMainWindow();
    void Kill();
    Task WaitForExitAsync(CancellationToken token);
}
internal sealed class OwnedProcess(Process process) : IOwnedProcess
{
    public bool HasExited => process.HasExited;
    public bool CloseMainWindow() => process.CloseMainWindow();
    public void Kill() => process.Kill(entireProcessTree: false);
    public Task WaitForExitAsync(CancellationToken token) => process.WaitForExitAsync(token);
}
internal static class OwnedProcessShutdown
{
    // Called only after the plugin gate has drained and all SDK connections have closed.
    // This bounded opportunity to drain cannot prove hardware acknowledgement.
    internal static async Task CloseAsync(IOwnedProcess process, bool writesMayBePending, Func<TimeSpan, Task>? drain = null)
    {
        if (process.HasExited) return;
        if (writesMayBePending) await (drain ?? (delay => Task.Delay(delay)))(TimeSpan.FromMilliseconds(250));
        if (process.HasExited) return;
        if (process.CloseMainWindow())
        {
            using var graceful = new CancellationTokenSource(1500);
            try { await process.WaitForExitAsync(graceful.Token); }
            catch (OperationCanceledException) when (graceful.IsCancellationRequested) { }
        }
        if (process.HasExited) return;
        process.Kill();
        using var termination = new CancellationTokenSource(1500);
        await process.WaitForExitAsync(termination.Token);
    }
}
