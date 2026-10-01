namespace OmronUpsd.Tests;

public class PowerMonitorTests
{
    private const long Poll = 2000;
    private static readonly Q1Status Online = Status();
    private static readonly Q1Status Fail = Status(utilityFail: true);

    private static Q1Status Status(bool utilityFail = false, bool batteryLow = false, bool testing = false) =>
        new(100, 100, 50, 60, 13.5, 36, utilityFail, batteryLow, false, testing, false);

    private readonly PowerMonitor _m = new(shutdownAfterSeconds: 300, minRuntimeMinutes: 6);
    private long _now = 1_000_000;

    private PowerTransition Step(Q1Status? q1, int? runtime = 30)
    {
        _now += Poll;
        return _m.Update(q1, runtime, _now);
    }

    private void EnterOnBattery()
    {
        for (var i = 0; i < PowerMonitor.FailDebounce; i++)
            Step(Fail);
        Assert.Equal(PowerState.OnBattery, _m.State);
    }

    [Fact]
    public void PowerFail_ConfirmedAfterDebounce()
    {
        for (var i = 1; i < PowerMonitor.FailDebounce; i++)
            Assert.Equal(PowerTransition.None, Step(Fail));
        Assert.Equal(PowerTransition.PowerFailed, Step(Fail));
    }

    [Fact]
    public void PowerFail_CountResetsOnUtilityReturn()
    {
        Step(Fail);
        Step(Fail);
        Step(Online);
        for (var i = 1; i < PowerMonitor.FailDebounce; i++)
            Step(Fail);
        Assert.Equal(PowerState.Online, _m.State);
    }

    [Fact]
    public void PowerFail_CommFailureKeepsCount()
    {
        Step(Fail);
        Step(null);
        for (var i = 1; i < PowerMonitor.FailDebounce; i++)
            Step(Fail);
        Assert.Equal(PowerState.OnBattery, _m.State);
    }

    [Fact]
    public void PowerFail_IgnoredDuringSelfTest()
    {
        for (var i = 0; i < 20; i++)
            Step(Status(utilityFail: true, testing: true));
        Assert.Equal(PowerState.Online, _m.State);
    }

    [Fact]
    public void OnBatterySince_IsFirstDetection()
    {
        Step(Fail);
        var first = _now;
        EnterOnBattery();
        Assert.Equal(first, _m.OnBatterySinceMs);
    }

    [Fact]
    public void PowerRestored_AfterRecoverDebounce()
    {
        EnterOnBattery();
        for (var i = 1; i < PowerMonitor.RecoverDebounce; i++)
            Assert.Equal(PowerTransition.None, Step(Online));
        Assert.Equal(PowerTransition.PowerRestored, Step(Online));
        Assert.Equal(PowerState.Online, _m.State);
        Assert.Null(_m.OnBatterySinceMs);
    }

    [Fact]
    public void Shutdown_AfterTimer()
    {
        EnterOnBattery();
        var deadline = _m.OnBatterySinceMs!.Value + _m.ShutdownAfterMs;
        while (_now + Poll < deadline)
            Assert.Equal(PowerTransition.None, Step(Fail));
        Assert.Equal(PowerTransition.ShutdownTriggered, Step(Fail));
        Assert.Equal(ShutdownReason.Timer, _m.Reason);
    }

    [Fact]
    public void Shutdown_TimerContinuesDuringCommFailure()
    {
        EnterOnBattery();
        PowerTransition t;
        do t = Step(null); while (t == PowerTransition.None);
        Assert.Equal(PowerTransition.ShutdownTriggered, t);
        Assert.Equal(ShutdownReason.Timer, _m.Reason);
    }

    [Fact]
    public void Shutdown_OnBatteryLow()
    {
        EnterOnBattery();
        Assert.Equal(PowerTransition.ShutdownTriggered, Step(Status(utilityFail: true, batteryLow: true)));
        Assert.Equal(ShutdownReason.BatteryLow, _m.Reason);
    }

    [Fact]
    public void Shutdown_OnLowRuntime()
    {
        EnterOnBattery();
        Assert.Equal(PowerTransition.ShutdownTriggered, Step(Fail, runtime: 5));
        Assert.Equal(ShutdownReason.LowRuntime, _m.Reason);
    }

    [Fact]
    public void Shutdown_UnknownRuntimeIgnored()
    {
        EnterOnBattery();
        Assert.Equal(PowerTransition.None, Step(Fail, runtime: null));
    }

    [Fact]
    public void Online_BatteryLowAndRuntimeIgnored()
    {
        for (var i = 0; i < 10; i++)
            Assert.Equal(PowerTransition.None, Step(Status(batteryLow: true), runtime: 1));
    }

    [Fact]
    public void Shutdown_NoReturnAfterTrigger()
    {
        EnterOnBattery();
        Step(Status(utilityFail: true, batteryLow: true));
        for (var i = 0; i < 10; i++)
            Assert.Equal(PowerTransition.None, Step(Online));
        Assert.Equal(PowerState.ShuttingDown, _m.State);
    }
}
