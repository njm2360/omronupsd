using System.Globalization;
using System.Text.RegularExpressions;

namespace OmronUpsd;

/// <summary>BW55T のコマンド文字列と応答解析。</summary>
public static partial class UpsCommands
{
    public const string Status = "Q1";
    public const string BatteryLevel = "Bl ?";
    public const string Runtime = "RTS";
    public const string LoadConsumption = "LC";
    public const string Model = "Si ?";
    public const string Serial = "PSNR";
    public const string Firmware = "FWV";
    public const string FlagsV = "V";
    public const string FlagsV2 = "V2";
    public const string FlagsV3 = "V3";
    public const int FlagsV3Length = 17;
    public const string MaxBackupTime = "But ?";
    public const string OutputStartDelay = "Rbd ?";
    public const string RestartBatteryLevel = "Rbv ?";
    public const string BatteryLowLevel = "Bld ?";

    // S は 0～30 分を受け付けるが、0 は "S.2" (1 分未満) となりマスターの OS 停止が間に合わない
    public const int MinOffDelayMinutes = 1;
    public const int MaxOffDelayMinutes = 30;

    public static string ShutdownWithAutoRestart(int minutes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minutes, MinOffDelayMinutes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minutes, MaxOffDelayMinutes);
        return "S" + minutes.ToString("D2", CultureInfo.InvariantCulture);
    }

    public static int? ParseInt(string? response) =>
        int.TryParse(response, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <returns>V / V2 / V3 応答の先頭 length 文字 (各桁 0/1、V3 は区切りの空白を含む)。形式不正時は null</returns>
    public static string? ParseFlags(string? response, int length = 8) =>
        response?.Length >= length && response.AsSpan(0, length).IndexOfAnyExcept('0', '1', ' ') < 0 ? response[..length] : null;

    public static (int Watts, int VoltAmperes)? ParseTotalLoad(string? response)
    {
        if (response is null) return null;
        var m = TotalLoadRegex().Match(response);
        return m.Success ? (int.Parse(m.Groups[1].ValueSpan), int.Parse(m.Groups[2].ValueSpan)) : null;
    }

    [GeneratedRegex(@"T:(\d+)W/(\d+)VA")]
    private static partial Regex TotalLoadRegex();
}
