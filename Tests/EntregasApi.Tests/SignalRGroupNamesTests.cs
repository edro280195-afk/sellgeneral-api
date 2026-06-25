using EntregasApi.Services;
using Xunit;

namespace EntregasApi.Tests;

public class SignalRGroupNamesTests
{
    [Fact]
    public void Admins_PrefixesWithTenant()
    {
        Assert.Equal("t1_Admins", SignalRGroupNames.Admins(1));
        Assert.Equal("t42_Admins", SignalRGroupNames.Admins(42));
    }

    [Fact]
    public void PosNodriza_PrefixesWithTenant()
    {
        Assert.Equal("t7_PosNodriza", SignalRGroupNames.PosNodriza(7));
    }

    [Fact]
    public void Route_PrefixesWithTenantAndAppendsToken()
    {
        Assert.Equal(
            "t1_Route_abc123",
            SignalRGroupNames.Route(1, "abc123"));
    }

    [Fact]
    public void Tracking_PrefixesWithTenantAndAppendsToken()
    {
        Assert.Equal(
            "t1_Tracking_xyz789",
            SignalRGroupNames.Tracking(1, "xyz789"));
    }

    [Fact]
    public void Order_PrefixesWithTenantAndAppendsToken()
    {
        Assert.Equal(
            "t1_Order_TOKEN-LUPITA-1",
            SignalRGroupNames.Order(1, "TOKEN-LUPITA-1"));
    }

    [Fact]
    public void PosOrder_PrefixesWithTenantAndAppendsInt()
    {
        Assert.Equal("t1_PosOrder_118", SignalRGroupNames.PosOrder(1, 118));
        Assert.Equal("t2_PosOrder_970", SignalRGroupNames.PosOrder(2, 970));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("with/slash")]
    [InlineData("dot.dot")]
    [InlineData("semicolon;injection")]
    public void EnsureSafeToken_RejectsInvalidTokens(string? badToken)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            SignalRGroupNames.Order(1, badToken!));
    }

    [Fact]
    public void EnsureSafeToken_AcceptsValidTokens()
    {
        // No debe lanzar
        SignalRGroupNames.EnsureSafeToken("abc-123_XYZ", "test");
        SignalRGroupNames.Route(1, "A1b2C3d4E5");
    }
}
