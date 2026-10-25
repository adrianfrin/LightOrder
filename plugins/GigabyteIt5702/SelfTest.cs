using ControlCenter.Contracts;

namespace ControlCenter.GigabyteIt5702;

internal static class SelfTest
{
    public static void Run()
    {
        // This path never constructs the plugin or invokes any native API.
        Equal(It5702Protocol.StaticReport(0, "#FF0000", 1, false), Expected(0x20, 1, 90, 0, 0, 255), "red BGR");
        Equal(It5702Protocol.StaticReport(0, "#00FF00", 1, false), Expected(0x20, 1, 90, 0, 255, 0), "green BGR");
        Equal(It5702Protocol.StaticReport(0, "#0000FF", 1, false), Expected(0x20, 1, 90, 255, 0, 0), "blue BGR");
        Equal(It5702Protocol.StaticReport(7, "#123456", 0.5, false), Expected(0x27, 0x80, 45, 0x56, 0x34, 0x12), "channel eight and brightness");
        Equal(It5702Protocol.StaticReport(0, "#FFFFFF", 1, true), Expected(0x20, 1, 0, 0, 0, 0), "off zeros");
        Equal(It5702Protocol.StaticReport(0, "#FFFFFF", 0, false), Expected(0x20, 1, 0, 255, 255, 255), "zero brightness");
        Check(It5702Protocol.StaticReport(0, "#FFFFFF", 0.25, false)[12] == 23, "brightness rounding");
        for (int channel = 0; channel < 8; channel++)
            Equal(It5702Protocol.StaticReport(channel, "#000000", 1, false),
                Expected((byte)(0x20 + channel), (byte)(1 << channel), 90, 0, 0, 0), "channel mask");
        foreach (double invalid in new[] { -0.001, 1.001, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Throws<PluginException>(() => It5702Protocol.StaticReport(0, "#FFFFFF", invalid, false), "brightness bounds");
        foreach (string invalid in new[] { "#12345", "#1234567", "123456", "#GG0000", "# 00000", "#１２３４５６" })
            Throws<PluginException>(() => It5702Protocol.StaticReport(0, invalid, 1, false), "color format");
        Throws<ArgumentOutOfRangeException>(() => It5702Protocol.StaticReport(-1, "#000000", 1, false), "low channel");
        Throws<ArgumentOutOfRangeException>(() => It5702Protocol.StaticReport(8, "#000000", 1, false), "high channel");
        var init = new byte[64]; init[0] = 0xcc; init[1] = 0x60;
        Equal(It5702Protocol.Command(0x60), init, "identification request");
        var execute = new byte[64]; execute[0] = 0xcc; execute[1] = 0x28; execute[2] = 0xff;
        Equal(It5702Protocol.Command(0x28, 0xff), execute, "execute request");
        Throws<PluginException>(() => It5702Protocol.Identify(new byte[64]), "invalid identification marker");
        Throws<PluginException>(() => It5702Protocol.Identify(new byte[63]), "short identification");
        var identification = new byte[64]; identification[0] = 0xcc; identification[1] = 1;
        identification[4] = 2; identification[11] = 2;
        System.Text.Encoding.ASCII.GetBytes("IT5701-GIGABYTE").CopyTo(identification, 12);
        var identity = It5702Protocol.Identify(identification);
        Check(identity.Name == "IT5701-GIGABYTE" && identity.Firmware == "2.0.0.0" && identity.HasLampArray, "identity fields");
        identification[0] = 0;
        Check(It5702Protocol.Identify(identification).Name == "IT5701-GIGABYTE", "compatible zero report ID");
        var emptyFirmware = (byte[])identification.Clone(); emptyFirmware[4] = 0;
        Throws<PluginException>(() => It5702Protocol.Identify(emptyFirmware), "zero firmware rejected");
        var emptyName = (byte[])identification.Clone(); Array.Clear(emptyName, 12, 28);
        Throws<PluginException>(() => It5702Protocol.Identify(emptyName), "empty product rejected");
        var observedFailure = Convert.FromHexString("CC000100000000000000000000000000000000000000000000020000000000000000010000000000000102000200010002000100000102000004000000000000");
        Throws<PluginException>(() => It5702Protocol.Identify(observedFailure), "actual failed controller response rejected");
        identification[0] = 1;
        Throws<PluginException>(() => It5702Protocol.Identify(identification), "wrong report ID");

        var request = LightingRequest.Parse(Wire.Element(new { deviceId = "controller", operationId = "op", color = "#aAbBcC", brightness = 0.5, off = false }));
        Check(request.Color == "#AABBCC", "canonical color");
        Throws<PluginException>(() => LightingRequest.Parse(Wire.Element(new { deviceId = "controller", operationId = "op", color = "#123456", off = false })), "missing brightness");
        Throws<PluginException>(() => LightingRequest.Parse(Wire.Element(new { deviceId = "controller", operationId = "op", color = "#123456", brightness = 1, off = "false" })), "boolean format");
        TestOperationCache(request);
        Check(new HidEndpoint("PATH").Id == new HidEndpoint("path").Id, "case-insensitive stable ID");
        Console.WriteLine("IT5702 self-test passed: packets, BGR, off, brightness, channels, identification, validation, IDs and operation cache. No hardware accessed.");
    }

    private static void TestOperationCache(LightingRequest request)
    {
        var cache = new OperationCache(2);
        var accepted = new ApplyResult("accepted", "unverified");
        cache.Add(request, accepted, null);
        Check(cache.TryGet(request, out var repeated) && repeated == accepted, "idempotent response");
        Throws<PluginException>(() => cache.TryGet(request with { Brightness = 1 }, out _), "payload conflict");
        var failed = request with { OperationId = "failed" };
        cache.Add(failed, null, new PluginException("DeviceError", "partial write"));
        Throws<PluginException>(() => cache.TryGet(failed, out _), "failure retained");
        var third = request with { OperationId = "third" };
        cache.Add(third, accepted, null);
        Check(!cache.TryGet(request, out _), "bounded eviction");
        Check(cache.TryGet(third, out _), "recent operation retained");
    }

    private static byte[] Expected(byte command, byte mask, byte brightness, byte blue, byte green, byte red)
    {
        var report = new byte[64];
        report[0] = 0xcc; report[1] = command; report[2] = mask; report[11] = 1; report[12] = brightness;
        report[14] = blue; report[15] = green; report[16] = red;
        return report;
    }
    private static void Equal(byte[] actual, byte[] expected, string name) => Check(actual.SequenceEqual(expected), name);
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Self-test failed: " + name);
    }
    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Self-test expected " + typeof(T).Name + ": " + name);
    }
}
