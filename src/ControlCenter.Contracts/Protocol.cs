using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlCenter.Contracts;

public static class Wire
{
    public const int Version = 1;
    public const int MaxLine = 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    public static JsonElement Element(object? value) => JsonSerializer.SerializeToElement(value, Json);
    public static T Read<T>(JsonElement value) => value.Deserialize<T>(Json) ?? throw new PluginException("InvalidArgument", "缺少请求参数");
}
public sealed record RpcRequest(string Jsonrpc, long Id, string Method, JsonElement Params);
public sealed record RpcError(int Code, string Message, string Data);
public sealed record RpcResponse(string Jsonrpc, long Id, object? Result, RpcError? Error);
public sealed record PluginHello(string Id, string Version, int ProtocolMajor = 1);
public sealed record DeviceInfo(string Id, string Name, string Kind, string[] Capabilities, string Status, string Detail);
public sealed record ApplyResult(string Status, string Detail, object? Readback = null);
public sealed class PluginException(string code, string message) : Exception(message) { public string Code { get; } = code; }
public interface IDevicePlugin : IAsyncDisposable
{
    PluginHello Hello { get; }
    Task<object> HandleAsync(string method, JsonElement args, CancellationToken cancellationToken);
}
public sealed record PluginManifest(string Id, string Name, string Version, int ProtocolMajor, string EntryPoint, string[] Arguments, string[] Permissions, bool Enabled = true);

public static class PluginServer
{
    public static async Task RunAsync(IDevicePlugin plugin)
    {
        await using (plugin)
        {
            while (await ReadLineLimitedAsync(Console.In, CancellationToken.None) is { } line)
            {
                RpcRequest? request = null;
                RpcResponse response;
                try
                {
                    request = JsonSerializer.Deserialize<RpcRequest>(line, Wire.Json) ?? throw new PluginException("InvalidArgument", "无效请求");
                    if (request.Jsonrpc != "2.0") throw new PluginException("ProtocolMismatch", "需要 JSON-RPC 2.0");
                    object result = request.Method switch
                    {
                        "plugin.hello" => plugin.Hello,
                        "plugin.shutdown" => new ApplyResult("accepted", "插件准备退出"),
                        _ => await plugin.HandleAsync(request.Method, request.Params, CancellationToken.None)
                    };
                    response = new("2.0", request.Id, result, null);
                }
                catch (Exception e)
                {
                    response = new("2.0", request?.Id ?? 0, null, new(-32000, e.Message, e is PluginException pe ? pe.Code : "DeviceError"));
                }
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, Wire.Json));
                await Console.Out.FlushAsync();
                if (request?.Method == "plugin.shutdown") break;
            }
        }
    }
    public static async Task<string?> ReadLineLimitedAsync(TextReader reader, CancellationToken token)
    {
        var text = new System.Text.StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), token) != 0)
        {
            if (buffer[0] == '\n') return text.ToString().TrimEnd('\r');
            if (text.Length >= Wire.MaxLine) throw new PluginException("InvalidArgument", "协议消息超过 1 MiB 字符限制");
            text.Append(buffer[0]);
        }
        return text.Length == 0 ? null : throw new EndOfStreamException("插件消息未完整结束");
    }
}
