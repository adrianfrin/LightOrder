using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ControlCenter.Contracts;

namespace LightOrder.App;

/// <summary>One request per connection, bounded UTF-8 frames, no automatic write retries.</summary>
internal sealed class PipeClient
{
    private long nextId;
    public async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        long id = Interlocked.Increment(ref nextId);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new RpcRequest("2.0", id, method, Wire.Element(parameters ?? new { })), Wire.Json);
        if (payload.Length > Wire.MaxLine) throw new InvalidDataException("请求超过 1 MiB / Request too large");
        await using var pipe = new NamedPipeClientStream(".", "LightOrder.Control.v1", PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Identification, HandleInheritability.None);
        bool sent = false;
        try
        {
            await pipe.ConnectAsync(2500, timeout.Token);
            sent = true;
            await pipe.WriteAsync(payload, timeout.Token);
            await pipe.WriteAsync(new byte[] { 10 }, timeout.Token);
            await pipe.FlushAsync(timeout.Token);
            using var frame = new MemoryStream();
            byte[] buffer = new byte[4096];
            bool complete = false;
            while (!complete)
            {
                int length = await pipe.ReadAsync(buffer, timeout.Token);
                if (length == 0) throw new EndOfStreamException("服务连接中断 / Service disconnected");
                int newline = Array.IndexOf(buffer, (byte)10, 0, length);
                int count = newline < 0 ? length : newline;
                if (frame.Length + count > Wire.MaxLine) throw new InvalidDataException("服务响应超过 1 MiB / Response too large");
                frame.Write(buffer, 0, count);
                complete = newline >= 0;
            }
            using var document = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(frame.ToArray()));
            var root = document.RootElement;
            if (root.GetProperty("jsonrpc").GetString() != "2.0" || root.GetProperty("id").GetInt64() != id)
                throw new InvalidDataException("服务响应标识不匹配 / Response ID mismatch");
            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                throw new InvalidOperationException($"{error.GetProperty("message").GetString()} ({error.GetProperty("data")})");
            return root.GetProperty("result").Clone();
        }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException)
        {
            if (sent && method is StandardMethods.Apply or StandardMethods.ProfilesWrite or StandardMethods.AutomationWrite)
                throw new IOException("操作结果未知，请重新读取状态后再决定；不会自动重试。 / Outcome unknown; refresh before retrying.", e);
            throw new IOException("无法连接本地服务。请完成首次配置，或检查服务后重试。 / Service unavailable. Complete setup or retry.", e);
        }
    }
}
