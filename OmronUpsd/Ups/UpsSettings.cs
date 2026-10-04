namespace OmronUpsd;

/// <summary>
/// 停電時の動作と復電時の自動再起動に影響するUPS本体設定。未取得の項目はnull。
/// MaxBackupTimeとOutputStartDelayは単位未確認のため応答値のまま保持。
/// </summary>
public sealed record UpsSettings(
    bool? AutoRestart,
    int? MaxBackupTime,
    int? OutputStartDelay,
    int? RestartBatteryLevel,
    int? BatteryLowLevel)
{
    // V2の先頭桁は自動再起動設定で、'0' が有効
    public static bool? ParseAutoRestart(string? flagsV2) => flagsV2 is null ? null : flagsV2[0] == '0';

    public IEnumerable<string> Warnings()
    {
        if (AutoRestart == false)
            yield return "UPS auto restart is disabled; output will not resume on power restore after a battery-depleted shutdown";
        if (MaxBackupTime > 0)
            yield return $"UPS max backup time is set ({MaxBackupTime}); output may stop before OS shutdown completes";
        if (OutputStartDelay == 0)
            yield return "UPS output start delay is 0 (prohibited value)";
    }
}
