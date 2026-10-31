using System.Text.Json;
using ControlCenter.Contracts;
using LightOrder.App;

if (args is ["wait-probe"]) { await Task.Delay(TimeSpan.FromSeconds(60)); return; }
if (args is ["verify-live", var directory]) { await LiveVerification.RunAsync(directory); return; }
var client = new PipeClient();
var options = args.ToList();
var output = options.Count > 1 ? options[1] : Path.Combine(Environment.CurrentDirectory, "artifacts", "integration-snapshot.json");
if (options.Count == 0 || options[0] == "snapshot")
{
    var result = await client.CallAsync(StandardMethods.Snapshot, new { });
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    await File.WriteAllTextAsync(output, result.GetRawText());
    Console.WriteLine($"Snapshot saved: {result.GetProperty("devices").GetArrayLength()} entries.");
    return;
}
if (options[0] == "rpc" && options.Count == 4)
{
    var method = options[1];
    if (method is not (StandardMethods.Snapshot or StandardMethods.Refresh or StandardMethods.Apply or StandardMethods.ProfilesRead or StandardMethods.ProfilesWrite or StandardMethods.AutomationRead or StandardMethods.AutomationWrite))
        throw new ArgumentException("Only standard service methods can be exercised.");
    using var parameters = JsonDocument.Parse(await File.ReadAllTextAsync(options[2]));
    var result = await client.CallAsync(method, parameters.RootElement);
    await File.WriteAllTextAsync(options[3], result.GetRawText());
    Console.WriteLine("RPC result written: " + method);
    return;
}
throw new ArgumentException("Usage: snapshot <output> | rpc <standard-method> <parameters.json> <result.json>");
