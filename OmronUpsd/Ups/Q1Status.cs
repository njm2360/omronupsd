using System.Globalization;

namespace OmronUpsd;

/// <summary>
/// Q1 応答 "(098.9 000.0 098.9 048 60.0 13.5 36.1 00101000"。
/// b5/b3/b1 は用途不明 (実機では b5, b3 が常時 1)。
/// </summary>
public sealed record Q1Status(
    double InputVoltage,
    double OutputVoltage,
    int LoadPercent,
    double Frequency,
    double BatteryVoltage,
    double Temperature,
    bool UtilityFail,
    bool BatteryLow,
    bool TestAbnormal,
    bool Testing,
    bool BatteryFault)
{
    public static Q1Status? TryParse(string? response)
    {
        if (response is null || response.Length < 46 || response[0] != '(')
            return null;

        var parts = response[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 8 || parts[7].Length != 8 || parts[7].Any(c => c is not ('0' or '1')))
            return null;

        if (!TryD(parts[0], out var input) || !TryD(parts[2], out var output) ||
            !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var load) ||
            !TryD(parts[4], out var freq) || !TryD(parts[5], out var batt) || !TryD(parts[6], out var temp))
            return null;

        var bits = parts[7];
        return new Q1Status(input, output, load, freq, batt, temp,
            UtilityFail: bits[0] == '1',
            BatteryLow: bits[1] == '1',
            TestAbnormal: bits[3] == '1',
            Testing: bits[5] == '1',
            BatteryFault: bits[7] == '1');
    }

    private static bool TryD(string s, out double v) =>
        double.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out v);
}
