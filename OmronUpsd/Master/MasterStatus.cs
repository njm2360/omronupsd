namespace OmronUpsd;

// Zabbix 連携仕様: 真偽値は 0/1、時刻は相対秒、不明値はキーごと省略
public sealed record StatusSnapshot(
    string Version,
    int DryRun,
    string State,
    int StateCode,
    string? ShutdownReason,
    long? OnBatterySeconds,
    long? SecondsUntilShutdown,
    int UpsOffCommandFailures,
    UpsSnapshot Ups,
    ClientsSnapshot Clients);

public sealed record UpsSnapshot(
    int Connected,
    long? SecondsSinceComm,
    long CommErrors,
    string? Model,
    string? Serial,
    string? Firmware,
    double? InputVoltage,
    double? OutputVoltage,
    int? LoadPercent,
    double? Frequency,
    double? BatteryVoltage,
    double? Temperature,
    int? UtilityFail,
    int? BatteryLow,
    int? BatteryFault,
    int? TestAbnormal,
    int? Testing,
    int? BatteryPercent,
    int? RuntimeMinutes,
    int? PowerW,
    int? PowerVa);

public sealed record ClientsSnapshot(int Expected, int Connected, string Missing, ClientInfo[] List);

public sealed record ClientInfo(string Name, int Connected, long? SecondsSinceHeartbeat);

/// <summary>MasterWorker からの更新と StatusServer からの参照を仲介。</summary>
public sealed class MasterStatus
{
    private readonly object _lock = new();
    private UpsSnapshot _ups = new(0, null, 0, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
    private (PowerState State, ShutdownReason Reason, long? OnBatterySeconds, long? UntilShutdown) _power = (PowerState.Online, ShutdownReason.None, null, null);
    private int _upsOffFailures;

    public void SetUps(UpsSnapshot ups)
    {
        lock (_lock) _ups = ups;
    }

    public void SetPower(PowerState state, ShutdownReason reason, long? onBatterySeconds, long? untilShutdown)
    {
        lock (_lock) _power = (state, reason, onBatterySeconds, untilShutdown);
    }

    public void IncrementUpsOffFailures()
    {
        lock (_lock) _upsOffFailures++;
    }

    public StatusSnapshot Build(ServiceOptions opt, ClientsSnapshot clients)
    {
        lock (_lock)
        {
            return new StatusSnapshot(
                AppInfo.Version,
                opt.DryRun ? 1 : 0,
                StateName(_power.State),
                (int)_power.State,
                _power.Reason == ShutdownReason.None ? null : _power.Reason.ToString(),
                _power.OnBatterySeconds,
                _power.UntilShutdown,
                _upsOffFailures,
                _ups,
                clients);
        }
    }

    public static string StateName(PowerState s) => s switch
    {
        PowerState.Online => "online",
        PowerState.OnBattery => "on_battery",
        _ => "shutting_down",
    };
}
