using System.Text.Json;

static class FanSessionSelfTest
{
    // Program.cs may wire: if (args is ["--fan-self-test"]) return FanSessionSelfTest.Run();
    // No Computer instance, administrator requirement, hardware access, or filesystem writes.
    public static int Run()
    {
        var passed = 0;
        void Check(bool result, string name)
        {
            if (!result) throw new InvalidOperationException(name);
            passed++;
        }
        void Reject(Action action, string name)
        {
            try { action(); } catch (Exception e) when (e is InvalidOperationException or ArgumentException) { passed++; return; }
            throw new InvalidOperationException(name);
        }
        string Report(string chip = "8689", string bank = "0") =>
            $"LPC IT87XX\n\nChip ID: 0x{chip}\nChip Version: 0x2\nBase Address: 0x0A40\nGPIO Address: 0x0A00\n\nEnvironment Controller Registers Bank {bank}\n\n      00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F\n\n" +
            " 10   FF FF FF 77 C6 D0 C0 C0 02 FF FF FF FF FF FF FF\n" +
            " 60   00 14 46 42 06 01 00 C0 00 14 3C 3F 05 01 00 FF\n" +
            " 70   00 14 3C 3F 05 01 00 3E 00 14 3C 3F 05 01 00 C0\n";
        try
        {
            Check(FanSessionPolicy.MaximumDuration == TimeSpan.FromMinutes(30) && FanSessionPolicy.MaximumSamples == 1800, "bounded duration");
            foreach (var profile in new[] { "quiet", "balanced" })
            {
                var floor = profile == "quiet" ? 50 : 65;
                Check(FanSessionPolicy.Next(0, 0, profile).Duty == floor, profile + " low floor");
                Check(FanSessionPolicy.Next(50, 50, profile).Duty == floor, profile + " curve start");
                Check(FanSessionPolicy.Next(65, 65, profile).Duty == (floor + 100) / 2f, profile + " midpoint");
                Check(FanSessionPolicy.Next(80, 80, profile).Duty == 100, profile + " full duty");
                Check(FanSessionPolicy.Next(110, 50, profile).Duty == 100, profile + " heat response");
                Check(FanSessionPolicy.Next(40, 70, profile).Temperature == 68, profile + " cooling release");
                Check(FanSessionPolicy.Next(45, 100, profile).Duty == 100, profile + " conservative startup");
                for (var temperature = 0; temperature <= 110; temperature++)
                {
                    var duty = FanSessionPolicy.Next(temperature, temperature, profile).Duty;
                    Check(float.IsFinite(duty) && duty >= floor && duty <= 100, profile + " bounded curve");
                }
            }
            foreach (float? value in new float?[] { null, float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1 })
            {
                Check(!FanSessionPolicy.ValidTemperature(value), "invalid temperature");
                Check(!FanSessionPolicy.ValidRpm(value), "invalid RPM");
                Check(!FanSessionPolicy.RunningRpm(value), "invalid running RPM");
            }
            Check(!FanSessionPolicy.ValidTemperature(111), "temperature upper bound");
            Check(FanSessionPolicy.ValidTemperature(0) && FanSessionPolicy.ValidTemperature(110), "temperature edges");
            Check(FanSessionPolicy.ValidRpm(0) && !FanSessionPolicy.RunningRpm(0), "finite stalled RPM");
            Check(!FanSessionPolicy.RunningRpm(199) && FanSessionPolicy.RunningRpm(200), "RPM threshold");
            Reject(() => FanSessionPolicy.Next(float.NaN, 100, "quiet"), "reject NaN curve");
            Reject(() => FanSessionPolicy.Next(40, float.PositiveInfinity, "quiet"), "reject previous NaN curve");
            Reject(() => FanSessionPolicy.Next(40, 100, "unknown"), "reject profile");
            var baseline = FanSessionPolicy.ReadRegisters(Report());
            Check(baseline.Count == 5 && baseline["0x13"] == 0x77 && baseline["0x15"] == 0xD0 && baseline["0x63"] == 0x42 && baseline["0x7B"] == 0x3F && baseline["0x7F"] == 0xC0, "exact relevant addresses");
            Check(FanSessionPolicy.ValidBaseline(baseline), "output bits enabled");
            Check(FanSessionPolicy.Restored(baseline, FanSessionPolicy.ReadRegisters(Report().Replace("\n", "\r\n"))), "CRLF parsing");
            Check(FanSessionPolicy.Restored(baseline, FanSessionPolicy.ReadRegisters(" 10   unrelated\n" + Report() + "\nEnvironment Controller Registers Bank 1\n 10   malformed\n")), "ignore unrelated tables and other banks");
            foreach (var address in baseline.Keys)
            {
                var modified = new Dictionary<string, byte>(baseline) { [address] = (byte)(baseline[address] ^ 1) };
                Check(!FanSessionPolicy.Restored(baseline, modified), "detect changed " + address);
                modified.Remove(address);
                Check(!FanSessionPolicy.Restored(baseline, modified), "detect missing " + address);
            }
            Check(!FanSessionPolicy.ValidBaseline(new Dictionary<string, byte>(baseline) { ["0x13"] = 0x76 }), "reject disabled output bit");
            Reject(() => FanSessionPolicy.ReadRegisters(Report("8792")), "reject wrong chip");
            Reject(() => FanSessionPolicy.ReadRegisters(Report(bank: "1")), "reject bank 1 only");
            Reject(() => FanSessionPolicy.ReadRegisters(Report() + "\n---\n" + Report()), "reject duplicate chip reports");
            Reject(() => FanSessionPolicy.ReadRegisters(Report().Replace(" 60   ", " 50   ")), "reject missing register row");
            Reject(() => FanSessionPolicy.ReadRegisters(Report().Replace("42", "ZZ")), "reject invalid hex");
            Reject(() => FanSessionPolicy.ReadRegisters(Report().Replace(" 60   ", " 10   ")), "reject duplicate row");
            Reject(() => FanSessionPolicy.ReadRegisters(Report().Replace(" 60   00", " 60  ")), "reject short row");
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", assertions = passed, hardwareAccessed = false }));
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", assertions = passed, error = e.Message, hardwareAccessed = false }));
            return 1;
        }
    }
}
