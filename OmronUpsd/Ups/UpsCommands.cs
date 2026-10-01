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

    // S は 0～30 分を受け付けるが、0 は "S.2" (12 秒) となりマスターの OS 停止が間に合わない
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

    public static (int Watts, int VoltAmperes)? ParseTotalLoad(string? response)
    {
        if (response is null) return null;
        var m = TotalLoadRegex().Match(response);
        return m.Success ? (int.Parse(m.Groups[1].ValueSpan), int.Parse(m.Groups[2].ValueSpan)) : null;
    }

    [GeneratedRegex(@"T:(\d+)W/(\d+)VA")]
    private static partial Regex TotalLoadRegex();
}
