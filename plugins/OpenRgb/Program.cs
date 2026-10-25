using ControlCenter.Contracts;
using ControlCenter.OpenRgb;

if (args is ["--start-backend-elevated"]) return await ElevatedBackend.RunAsync();
if (args is ["--self-test"])
{
    try { await SelfTest.RunAsync(); Console.WriteLine("OpenRGB self-test: PASS"); return 0; }
    catch (Exception e) { Console.Error.WriteLine($"OpenRGB self-test: FAIL\n{e}"); return 1; }
}
if (args.Length != 0) { Console.Error.WriteLine("Usage: ControlCenter.OpenRgb [--self-test]"); return 2; }
try { await PluginServer.RunAsync(new OpenRgbPlugin()); return 0; }
catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
