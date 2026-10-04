namespace OmronUpsd;

/// <summary>Q1 に含まれない UPS の警報。未取得の項目は null。</summary>
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
        ("バッテリ未接続", BatteryDisconnected),
        ("UPS 故障", UpsFault),
        ("バッテリ交換時期", BatteryReplace),
        ("過負荷", Overload),
        ("出力過電圧", OutputOverVoltage),
        ("DC バス電圧異常", DcBusAbnormal),
        ("過負荷継続", OverloadTimeout),
        ("出力短絡", OutputShort),
        ("バッテリ過充電", BatteryOverCharge),
        ("バッテリ充電不足", BatteryUnderCharge),
        ("過温度", OverTemperature),
        ("出力電圧低下", OutputUnderVoltage),
    ];

    private static bool? Bit(string? flags, int index) => flags is null ? null : flags[index] == '1';
}
