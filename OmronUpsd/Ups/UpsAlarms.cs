namespace OmronUpsd;

/// <summary>Q1に含まれないUPSの警報。未取得の項目はnull。</summary>
public sealed record UpsAlarms(
    bool? BatteryDisconnected,
    bool? UpsFault,
    bool? BatteryReplace,
    bool? Overload,
    bool? OutputOverVoltage,
    bool? DcBusAbnormal,
    bool? OverloadTimeout,
    bool? OutputShort,
    bool? BatteryOverCharge,
    bool? BatteryUnderCharge,
    bool? OverTemperature,
    bool? OutputUnderVoltage)
{
    public static readonly UpsAlarms Unknown = Parse(null, null, null);

    public static UpsAlarms Parse(string? flagsV, string? flagsV2, string? flagsV3) => new(
        Bit(flagsV, 0), Bit(flagsV, 5), Bit(flagsV, 7), Bit(flagsV2, 4), Bit(flagsV2, 7),
        Bit(flagsV3, 0), Bit(flagsV3, 1), Bit(flagsV3, 2), Bit(flagsV3, 3), Bit(flagsV3, 4), Bit(flagsV3, 5), Bit(flagsV3, 11));

    public IReadOnlyList<(string Name, bool? Active)> Items() =>
    [
        ("Battery disconnected", BatteryDisconnected),
        ("UPS fault", UpsFault),
        ("Battery replacement due", BatteryReplace),
        ("Overload", Overload),
        ("Output overvoltage", OutputOverVoltage),
        ("DC bus voltage abnormal", DcBusAbnormal),
        ("Overload timeout", OverloadTimeout),
        ("Output short circuit", OutputShort),
        ("Battery overcharge", BatteryOverCharge),
        ("Battery undercharge", BatteryUnderCharge),
        ("Over temperature", OverTemperature),
        ("Output undervoltage", OutputUnderVoltage),
    ];

    private static bool? Bit(string? flags, int index) => flags is null ? null : flags[index] == '1';
}
