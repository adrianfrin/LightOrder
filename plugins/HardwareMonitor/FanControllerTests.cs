using ControlCenter.Contracts;

static class FanControllerTests
{
    public static int Run()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); count++; }
        var second = TimeSpan.FromSeconds(1);
        var silent = FanProfile.Silent;
        var balanced = silent with { Mode = "curve", RadiatorPercent = 65, CasePercent = 55,
            RadiatorCurve = [new(40,65), new(55,65), new(65,80), new(80,100)],
            CaseCurve = [new(40,55), new(55,65), new(65,80), new(80,100)] };
        var controller = new FanController();
        foreach (var temperature in new[] { 25f, 56f, 65f, 74f, 79.9f })
        {
            controller.Reset();
            Check(controller.Next(silent, temperature, TimeSpan.Zero) == (40f,25f), "silent starts at fixed minimum without synthetic full duty");
            for (int i=0; i<20; i++) Check(controller.Next(silent, temperature, second) == (40f,25f), "silent does not follow normal CPU temperature");
        }
        Check(controller.Next(silent, 80, second) == (100f,100f), "raw 80C immediately bypasses smoothing");
        for (int i=0; i<30; i++) Check(controller.Next(silent, 76, second) == (100f,100f), "protection remains above recovery threshold");
        for (int i=0; i<9; i++) Check(controller.Next(silent, 70, second) == (100f,100f), "protection needs ten continuous cool seconds");
        Check(controller.Next(silent, 76, second) == (100f,100f), "warm interruption resets recovery hold");
        for (int i=0; i<9; i++) controller.Next(silent, 40, second);
        Check(controller.Next(silent, 40, second) == (98f,98f), "release protection descends gradually");
        for (int i=0; i<40; i++) controller.Next(silent, 40, second);
        Check(controller.Next(silent, 40, second) == (40f,25f), "recovery returns to fixed minimum");
        controller.Reset();
        Check(controller.Next(balanced, 40, TimeSpan.Zero) == (65f,55f), "balanced startup uses fresh reading");
        Check(controller.Next(balanced, 70, second) == (65f,55f), "one-second spike filtered below knee");
        var prior = controller.Next(balanced, 40, second);
        for (int i=0; i<30; i++)
        {
            var next = controller.Next(balanced, 74, second);
            Check(next.Radiator >= prior.Radiator && next.Radiator-prior.Radiator <= 5.001f && next.Case-prior.Case <= 5.001f, "sustained heat raises duty within slew bound");
            prior = next;
        }
        Check(prior.Radiator > 90 && prior.Case > 90, "balanced follows sustained heat");
        for (int i=0; i<60; i++)
        {
            var next = controller.Next(balanced, 40, second);
            Check(prior.Radiator-next.Radiator <= 2.001f && prior.Case-next.Case <= 2.001f, "cool-down slew bounded");
            prior = next;
        }
        Check(prior == (65f,55f), "balanced settles to low temperature duty");
        controller.Reset(); controller.Next(balanced, 65, TimeSpan.Zero);
        for (int i=0; i<20; i++) Check(controller.Next(balanced, i%2==0?66:64, second) == (80f,80f), "small temperature jitter held by deadband");
        var before = controller.Next(balanced, 65, TimeSpan.Zero);
        Check(controller.Next(balanced, 74, TimeSpan.Zero) == before, "repeated commands cannot advance time-based filter");
        Check(controller.Next(balanced, 81, TimeSpan.Zero) == (100f,100f), "emergency independent of elapsed time");
        controller.Reset();
        Check(controller.Next(silent, 81, TimeSpan.Zero) == (100f,100f), "hot startup protected");
        Console.WriteLine($"Fan controller: {count} assertions passed.");
        return count;
    }
}
