using ControlCenter.Contracts;

namespace ControlCenter.GigabyteIt5702;

// Used under the plugin's request lock. Failed attempts are retained as well, because a
// failed HID call may have applied a prefix of the command sequence.
internal sealed class OperationCache(int capacity = 256)
{
    private sealed record Entry(LightingRequest Request, ApplyResult? Result, PluginException? Error);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();

    public bool TryGet(LightingRequest request, out ApplyResult? result)
    {
        result = null;
        if (!entries.TryGetValue(request.OperationId, out var entry)) return false;
        if (entry.Request != request)
            throw new PluginException("OperationConflict", "operationId was already used with a different payload.");
        if (entry.Error is not null) throw new PluginException(entry.Error.Code, entry.Error.Message);
        result = entry.Result;
        return true;
    }

    public void Add(LightingRequest request, ApplyResult? result, PluginException? error)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (entries.ContainsKey(request.OperationId)) throw new InvalidOperationException("Operation already cached.");
        while (entries.Count >= capacity) entries.Remove(order.Dequeue());
        entries.Add(request.OperationId, new(request, result, error));
        order.Enqueue(request.OperationId);
    }
}
