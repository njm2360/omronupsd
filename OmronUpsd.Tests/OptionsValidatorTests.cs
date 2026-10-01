namespace OmronUpsd.Tests;

public class OptionsValidatorTests
{
    private static ServiceOptions Valid() => new() { SharedKey = "0123456789abcdef" };

    private static bool IsValid(ServiceOptions o) => new ServiceOptionsValidator().Validate(null, o).Succeeded;

    [Fact]
    public void Defaults_AreValid() => Assert.True(IsValid(Valid()));

    [Fact]
    public void ShortKey_Rejected()
    {
        var o = Valid();
        o.SharedKey = "short";
        Assert.False(IsValid(o));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void UpsOffDelay_OutOfRange_Rejected(int minutes)
    {
        var o = Valid();
        o.Master.UpsOffDelayMinutes = minutes;
        o.Master.MinRuntimeMinutes = 60;
        Assert.False(IsValid(o));
    }

    [Fact]
    public void MinRuntime_MustExceedUpsOffDelay()
    {
        var o = Valid();
        o.Master.UpsOffDelayMinutes = 5;
        o.Master.MinRuntimeMinutes = 5;
        Assert.False(IsValid(o));
    }

    [Fact]
    public void Client_RequiresMasterHost()
    {
        var o = Valid();
        o.Role = Role.Client;
        Assert.False(IsValid(o));
        o.Client.MasterHost = "192.168.1.10";
        Assert.True(IsValid(o));
    }
}
