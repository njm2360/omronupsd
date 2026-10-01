namespace OmronUpsd.Tests;

public class UpsParsingTests
{
    [Fact]
    public void Q1_ParsesDeviceResponse()
    {
        var q = Q1Status.TryParse("(098.9 000.0 098.9 048 60.0 13.5 36.1 00101000");

        Assert.NotNull(q);
        Assert.Equal(98.9, q.InputVoltage);
        Assert.Equal(98.9, q.OutputVoltage);
        Assert.Equal(48, q.LoadPercent);
        Assert.Equal(60.0, q.Frequency);
        Assert.Equal(13.5, q.BatteryVoltage);
        Assert.Equal(36.1, q.Temperature);
        Assert.False(q.UtilityFail);
        Assert.False(q.BatteryLow);
        Assert.False(q.Testing);
    }

    [Theory]
    [InlineData("10000000", true, false, false, false, false)]
    [InlineData("01000000", false, true, false, false, false)]
    [InlineData("00010000", false, false, true, false, false)]
    [InlineData("00000100", false, false, false, true, false)]
    [InlineData("00000001", false, false, false, false, true)]
    public void Q1_StatusBits(string bits, bool fail, bool low, bool testAbnormal, bool testing, bool fault)
    {
        var q = Q1Status.TryParse($"(000.0 000.0 098.9 048 60.0 12.1 36.1 {bits}")!;

        Assert.Equal(fail, q.UtilityFail);
        Assert.Equal(low, q.BatteryLow);
        Assert.Equal(testAbnormal, q.TestAbnormal);
        Assert.Equal(testing, q.Testing);
        Assert.Equal(fault, q.BatteryFault);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("NAK")]
    [InlineData("")]
    [InlineData("(098.9 000.0 098.9 048 60.0 13.5 36.1 0010100")]
    [InlineData("(098.9 000.0 098.9 048 60.0 13.5 36.1 0010100x")]
    [InlineData("(098.9 000.0 098.9 04x 60.0 13.5 36.1 00101000")]
    public void Q1_InvalidResponse_ReturnsNull(string? response) => Assert.Null(Q1Status.TryParse(response));

    [Theory]
    [InlineData(1, "S01")]
    [InlineData(3, "S03")]
    [InlineData(30, "S30")]
    public void ShutdownCommand_TwoDigitMinutes(int minutes, string expected) =>
        Assert.Equal(expected, UpsCommands.ShutdownWithAutoRestart(minutes));

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void ShutdownCommand_OutOfRange_Throws(int minutes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => UpsCommands.ShutdownWithAutoRestart(minutes));

    [Fact]
    public void LoadConsumption_ExtractsTotal() =>
        Assert.Equal((166, 186), UpsCommands.ParseTotalLoad("A:----W/----VA,B:----W/----VA,C:----W/----VA,T:0166W/0186VA"));

    [Theory]
    [InlineData("0012", 12)]
    [InlineData("100", 100)]
    [InlineData("NAK", null)]
    [InlineData(null, null)]
    [InlineData("-1", null)]
    public void ParseInt(string? response, int? expected) => Assert.Equal(expected, UpsCommands.ParseInt(response));
}
