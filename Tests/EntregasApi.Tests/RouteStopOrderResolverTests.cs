using EntregasApi.Services;
using Xunit;

namespace EntregasApi.Tests;

public class RouteStopOrderResolverTests
{
    [Fact]
    public void Resolve_PreservesInterleavedPreviewOrder()
    {
        var tandaId = Guid.Parse("5f764386-9173-43dd-8cf4-8e20c68efbef");
        var validStops = new List<RouteStop>
        {
            new("order:10", 27.49, -99.50),
            new("order:20", 27.50, -99.51),
            new($"tanda:{tandaId}", 27.51, -99.52)
        };

        var result = RouteStopOrderResolver.Resolve(
            validStops,
            new[]
            {
                "order:20",
                $"tanda:{tandaId}",
                "order:10"
            });

        Assert.Equal(
            new[] { "order:20", $"tanda:{tandaId}", "order:10" },
            result);
    }

    [Fact]
    public void Resolve_IgnoresUnknownAndDuplicateStops()
    {
        var validStops = new List<RouteStop>
        {
            new("order:10", null, null),
            new("order:20", null, null)
        };

        var result = RouteStopOrderResolver.Resolve(
            validStops,
            new[] { "order:20", "order:999", "ORDER:20" });

        Assert.Equal(new[] { "order:20", "order:10" }, result);
    }

    [Fact]
    public void Resolve_AppendsValidStopsMissingFromPreview()
    {
        var validStops = new List<RouteStop>
        {
            new("order:10", null, null),
            new("order:20", null, null),
            new("order:30", null, null)
        };

        var result = RouteStopOrderResolver.Resolve(
            validStops,
            new[] { "order:30" });

        Assert.Equal(new[] { "order:30", "order:10", "order:20" }, result);
    }
}
