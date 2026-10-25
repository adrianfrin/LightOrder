using ControlCenter.Contracts;
using ControlCenter.WindowsPolicy;

if (args is ["--self-test"])
{
    await PolicySelfTests.RunAsync();
    return;
}
if (args.Length != 0 && args is not ["--service"]) throw new ArgumentException("Only --self-test or --service is supported.");
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WindowsPolicy requires Windows.");
await PluginServer.RunAsync(args is ["--service"]
    ? new WindowsPolicyPlugin(new NativePolicy(), new RecoveryJournal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LightOrder", "WindowsPolicy")))
    : new WindowsPolicyPlugin());
