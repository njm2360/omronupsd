namespace OmronUpsd;

public enum PowerState { Online, OnBattery, ShuttingDown }

public enum ShutdownReason { None, Timer, BatteryLow, LowRuntime }

public enum PowerTransition { None, PowerFailed, PowerRestored, ShutdownTriggered }

/// <summary>
/// 停電判定の状態遷移。Online → OnBattery → ShuttingDown、OnBattery からは復電で Online に戻る。
/// 時刻は Environment.TickCount64 (単調増加 ms)。
/// </summary>
public sealed class PowerMonitor(int shutdownAfterSeconds, int minRuntimeMinutes)
{
    public const int FailDebounce = 4;
    public const int RecoverDebounce = 3;

    private int _failCount;
    private int _recoverCount;
    private long _firstFailAt;

    public PowerState State { get; private set; } = PowerState.Online;
    public ShutdownReason Reason { get; private set; }
    public long? OnBatterySinceMs { get; private set; }

    public long ShutdownAfterMs => shutdownAfterSeconds * 1000L;

    /// <param name="q1">取得失敗時は null。停電中の通信断時もタイマーは継続</param>
    /// <param name="runtimeMinutes">不明時は null (判定対象外)</param>
    /// <returns>この呼び出しで発生した遷移</returns>
    public PowerTransition Update(Q1Status? q1, int? runtimeMinutes, long nowMs)
    {
        switch (State)
        {
            case PowerState.Online:
                // セルフテスト中に b7 が立つ機種があるため停電扱いしない
                if (q1 is { UtilityFail: true, Testing: false })
                {
                    if (_failCount++ == 0)
                        _firstFailAt = nowMs;
                    if (_failCount >= FailDebounce)
                    {
                        State = PowerState.OnBattery;
                        OnBatterySinceMs = _firstFailAt;
                        _recoverCount = 0;
                        return CheckShutdown(q1, runtimeMinutes, nowMs) ?? PowerTransition.PowerFailed;
                    }
                }
                else if (q1 is not null)
                {
                    _failCount = 0;
                }
                return PowerTransition.None;

            case PowerState.OnBattery:
                if (q1 is { UtilityFail: false })
                {
                    if (++_recoverCount >= RecoverDebounce)
                    {
                        Reset();
                        return PowerTransition.PowerRestored;
                    }
                }
                else if (q1 is not null)
                {
                    _recoverCount = 0;
                }
                return CheckShutdown(q1, runtimeMinutes, nowMs) ?? PowerTransition.None;

            default:
                return PowerTransition.None;
        }
    }

    /// <summary>DryRun のシャットダウン模擬後、復電時の監視復帰用。</summary>
    public void Reset()
    {
        State = PowerState.Online;
        Reason = ShutdownReason.None;
        OnBatterySinceMs = null;
        _failCount = 0;
        _recoverCount = 0;
    }

    public void ForceShutdown(ShutdownReason reason)
    {
        State = PowerState.ShuttingDown;
        Reason = reason;
    }

    /// <summary>判定順はバッテリ低下 → 残り時間 → 経過時間。該当なしは null。</summary>
    private PowerTransition? CheckShutdown(Q1Status? q1, int? runtimeMinutes, long nowMs)
    {
        ShutdownReason reason;
        if (q1 is { BatteryLow: true })
            reason = ShutdownReason.BatteryLow;
        else if (runtimeMinutes < minRuntimeMinutes)
            reason = ShutdownReason.LowRuntime;
        else if (nowMs - OnBatterySinceMs >= ShutdownAfterMs)
            reason = ShutdownReason.Timer;
        else
            return null;

        ForceShutdown(reason);
        return PowerTransition.ShutdownTriggered;
    }
}
