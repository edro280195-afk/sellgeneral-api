using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EntregasApi.Tests;

public class RouteOptimizerEntitlementTests
{
    [Fact]
    public async Task OptimizeAsync_WithoutTrafficFeature_UsesHeuristicFallback()
    {
        var optimizer = CreateOptimizer(hasTrafficFeature: false);

        var result = await optimizer.OptimizeAsync(CreateStops(), 27.4861, -99.5069);

        Assert.Equal("haversine+2opt", result.Source);
        Assert.Equal(2, result.OrderedStopIds.Count);
    }

    [Fact]
    public async Task OptimizeAsync_WithTrafficFeatureButNoGoogleKey_UsesEliteHeuristicFallback()
    {
        var optimizer = CreateOptimizer(hasTrafficFeature: true);

        var result = await optimizer.OptimizeAsync(CreateStops(), 27.4861, -99.5069);

        Assert.Equal("elite-haversine+2opt", result.Source);
        Assert.Equal(2, result.OrderedStopIds.Count);
    }

    private static RouteOptimizerService CreateOptimizer(bool hasTrafficFeature)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Google:RoutesApiKey"] = "dummy"
            })
            .Build();

        return new RouteOptimizerService(
            new FakeEntitlementService(hasTrafficFeature),
            new NoopHttpClientFactory(),
            config,
            NullLogger<RouteOptimizerService>.Instance);
    }

    private static List<RouteStop> CreateStops()
    {
        return new List<RouteStop>
        {
            new("order:1", 27.4900, -99.5000),
            new("order:2", 27.5100, -99.5200)
        };
    }

    private sealed class FakeEntitlementService : IEntitlementService
    {
        private readonly bool _hasTrafficFeature;

        public FakeEntitlementService(bool hasTrafficFeature)
        {
            _hasTrafficFeature = hasTrafficFeature;
        }

        public Task<bool> HasFeatureAsync(Feature feature, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(feature == Feature.TrafficRouteOptimization && _hasTrafficFeature);
        }

        public Task<int> GetLimitAsync(LimitKey limitKey, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(int.MaxValue);
        }

        public Task<string> EffectivePlanTierAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_hasTrafficFeature ? PlanTiers.Elite : PlanTiers.Entrada);
        }

        public Task<SubscriptionSnapshot> GetSubscriptionSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var effectivePlan = _hasTrafficFeature ? PlanTiers.Elite : PlanTiers.Entrada;
            return Task.FromResult(new SubscriptionSnapshot(
                effectivePlan,
                effectivePlan,
                SubscriptionStatus.Active,
                null,
                null,
                null,
                null,
                false,
                0,
                3));
        }

        public Task EnsureWithinLimitAsync(
            LimitKey limitKey,
            int currentCount,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Feature>> GetEnabledFeaturesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Feature>>(_hasTrafficFeature
                ? new[] { Feature.TrafficRouteOptimization }
                : Array.Empty<Feature>());
    }

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient();
        }
    }
}
