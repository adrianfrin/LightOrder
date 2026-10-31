using System.Text;
using System.Text.Json;
using ControlCenter.Contracts;

namespace LightOrder.Service;

/// <summary>Newline-delimited UTF-8 frames bounded in bytes, not UTF-16 characters.</summary>
public sealed class RpcFraming(Stream stream)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly byte[] buffer = new byte[8192];
    private int cursor;
    private int count;

    public async Task<string?> ReadAsync(CancellationToken token)
    {
        using var frame = new MemoryStream();
        while (true)
        {
            if (cursor == count)
            {
                count = await stream.ReadAsync(buffer, token);
                cursor = 0;
                if (count == 0)
                    return frame.Length == 0 ? null : throw new EndOfStreamException("Incomplete RPC frame.");
            }
            var newline = Array.IndexOf(buffer, (byte)'\n', cursor, count - cursor);
            var end = newline >= 0 ? newline : count;
            var length = end - cursor;
            if (frame.Length + length > Wire.MaxLine) throw new InvalidDataException("RPC frame exceeds 1 MiB.");
            frame.Write(buffer, cursor, length);
            cursor = newline >= 0 ? end + 1 : end;
            if (newline >= 0)
            {
                var bytes = frame.GetBuffer();
                var size = (int)frame.Length;
                if (size > 0 && bytes[size - 1] == '\r') size--;
                return Utf8.GetString(bytes, 0, size);
            }
        }
    }

    public async Task WriteAsync(RpcResponse response, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, Wire.Json);
        if (bytes.Length > Wire.MaxLine)
            bytes = JsonSerializer.SerializeToUtf8Bytes(new RpcResponse("2.0", response.Id, null,
                new RpcError(-32000, "Response exceeds 1 MiB. Reduce installed device/telemetry count.", "ResponseTooLarge")), Wire.Json);
        await stream.WriteAsync(bytes, token);
        await stream.WriteAsync(new byte[] { (byte)'\n' }, token);
        await stream.FlushAsync(token);
    }
}

public static class ServiceProtocol
{
    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        StandardMethods.Snapshot, StandardMethods.Refresh, StandardMethods.Apply, StandardMethods.Plugins,
        StandardMethods.ProfilesRead, StandardMethods.ProfilesWrite,
        StandardMethods.AutomationRead, StandardMethods.AutomationWrite
    };

    public static RpcRequest Parse(string line)
    {
        using var json = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new PluginException("InvalidRequest", "RPC request must be an object.");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!fields.Add(property.Name) || property.Name is not ("jsonrpc" or "id" or "method" or "params"))
                throw new PluginException("InvalidRequest", "Unknown or duplicate RPC field.");
        if (!root.TryGetProperty("id", out var id) || !id.TryGetInt64(out var requestId))
            throw new PluginException("InvalidRequest", "RPC id must be a 64-bit integer; notifications are not supported.");
        if (!root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
            throw new PluginException("ProtocolMismatch", "JSON-RPC 2.0 is required.");
        if (!root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String || !Methods.Contains(method.GetString()!))
            throw new PluginException("MethodNotAllowed", "Unknown or forbidden service method.");
        var parameters = root.TryGetProperty("params", out var args) ? args.Clone() : Wire.Element(new { });
        if (parameters.ValueKind != JsonValueKind.Object) throw new PluginException("InvalidArgument", "RPC params must be an object.");
        return new RpcRequest("2.0", requestId, method.GetString()!, parameters);
    }

    public static long RecoverId(string line)
    {
        try
        {
            using var json = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var value) ? value : 0;
        }
        catch (JsonException) { return 0; }
    }

    public static RpcResponse Error(long id, Exception error)
    {
        var category = error is PluginException p ? p.Code : error is JsonException or InvalidDataException or ArgumentException or InvalidOperationException
            ? "InvalidRequest" : "ServiceError";
        var code = category switch { "MethodNotAllowed" => -32601, "InvalidArgument" => -32602, "InvalidRequest" or "ProtocolMismatch" => -32600, _ => -32000 };
        // Do not disclose file paths, process arguments or OS exception details over the pipe.
        var message = error is PluginException known ? known.Message : "Request failed: " + category + ". Check installation configuration and service status.";
        return new RpcResponse("2.0", id, null, new RpcError(code, message, category));
    }
}
