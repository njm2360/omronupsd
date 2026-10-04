namespace OmronUpsd;

/// <summary>
/// 停電時の動作と復電時の自動再起動に影響する UPS 本体設定。未取得の項目は null。
/// MaxBackupTime と OutputStartDelay は単位未確認のため応答値のまま保持。
/// </summary>
public sealed record UpsSettings(
    bool? AutoRestart,
    int? MaxBackupTime,
    int? OutputStartDelay,
    int? RestartBatteryLevel,
    int? BatteryLowLevel)
{
    // V2 の先頭桁は自動再起動設定で、'0' が有効
    public static bool? ParseAutoRestart(string? flagsV2) => flagsV2 is null ? null : flagsV2[0] == '0';

    public IEnumerable<string> Warnings()
    {
        if (AutoRestart == false)
            yield return "UPS 自動再起動が無効、電池切れによる停止後に復電しても出力を再開しない";
        if (MaxBackupTime > 0)
            yield return $"UPS 最大バックアップ時間が設定済み ({MaxBackupTime})、OS シャットダウン完了前に出力を停止する可能性あり";
        if (OutputStartDelay == 0)
            yield return "UPS 出力開始遅延が 0 (設定禁止値)";
    }
}
