using ControlCenter.Contracts;
using ControlCenter.GigabyteIt5702;

if (args.Length == 1 && args[0] == "--self-test")
{
    try { SelfTest.Run(); }
    catch (Exception error)
    {
        Console.Error.WriteLine(error.Message);
        Environment.ExitCode = 1;
    }
    return;
}
if (args.Length == 2 && args[0] == "--identify" && args[1] is "--access0" or "--rw")
{
    try { HidTransport.DiagnoseIdentification(readWrite: args[1] == "--rw"); }
    catch (Exception error)
    {
        Console.Error.WriteLine(error.Message);
        Environment.ExitCode = 1;
    }
    return;
}
if (args.Length != 0)
{
    Console.Error.WriteLine("Usage: ControlCenter.GigabyteIt5702 [--self-test | --identify (--access0 | --rw)]");
    Environment.ExitCode = 2;
    return;
}
await PluginServer.RunAsync(new It5702Plugin());
