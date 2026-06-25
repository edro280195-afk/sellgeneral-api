using EntregasApi.Data;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EntregasApi.Tests;

public class EntitlementServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TrialingBusiness_UsesProFeatures()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedBusinessAsync(
            ctx,
            planTier: PlanTiers.Entrada,
            status: SubscriptionStatus.Trialing,
            trialEndsAt: Now.UtcDateTime.AddDays(1));

        var service = CreateService(ctx, business.Id);

        Assert.Equal(PlanTiers.Pro, await service.EffectivePlanTierAsync());
        Assert.True(await service.HasFeatureAsync(Feature.Financials));
        Assert.False(await service.HasFeatureAsync(Feature.CamiAssistant));
    }

    [Fact]
    public async Task ExpiredTrial_ReturnsPaymentRequiredFromRequiresFeatureFilter()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedBusinessAsync(
            ctx,
            planTier: PlanTiers.Pro,
            status: SubscriptionStatus.Trialing,
            trialEndsAt: Now.UtcDateTime.AddMinutes(-1));

        var service = CreateService(ctx, business.Id);
        var attribute = new RequiresFeatureAttribute(Feature.ManualOrders);
        var context = CreateActionContext(service);
        var nextCalled = false;

        await attribute.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(
                context,
                new List<IFilterMetadata>(),
                controller: new object()));
        });

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status402PaymentRequired, result.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task ActiveEntrada_DoesNotIncludeFinancials()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedBusinessAsync(
            ctx,
            planTier: PlanTiers.Entrada,
            status: SubscriptionStatus.Active);

        var service = CreateService(ctx, business.Id);

        Assert.Equal(PlanTiers.Entrada, await service.EffectivePlanTierAsync());
        Assert.True(await service.HasFeatureAsync(Feature.ManualOrders));
        Assert.False(await service.HasFeatureAsync(Feature.Financials));
    }

    [Fact]
    public async Task MaxDrivers_RespectsEntradaLimit()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedBusinessAsync(
            ctx,
            planTier: PlanTiers.Entrada,
            status: SubscriptionStatus.Active);

        var service = CreateService(ctx, business.Id);

        Assert.Equal(1, await service.GetLimitAsync(LimitKey.MaxDrivers));
        await service.EnsureWithinLimitAsync(LimitKey.MaxDrivers, currentCount: 0);

        var ex = await Assert.ThrowsAsync<EntitlementLimitExceededException>(() =>
            service.EnsureWithinLimitAsync(LimitKey.MaxDrivers, currentCount: 1));

        Assert.Equal(LimitKey.MaxDrivers, ex.LimitKey);
        Assert.Equal(PlanTiers.Pro, ex.RequiredPlan);
        Assert.Equal(1, ex.Limit);
    }

    [Fact]
    public async Task PastDueWithinGrace_UsesStoredPlan()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedBusinessAsync(
            ctx,
            planTier: PlanTiers.Pro,
            status: SubscriptionStatus.PastDue,
            currentPeriodEndsAt: Now.UtcDateTime.AddDays(-2));

        var service = CreateService(ctx, business.Id);
        var snapshot = await service.GetSubscriptionSnapshotAsync();

        Assert.Equal(PlanTiers.Pro, snapshot.EffectivePlanTier);
        Assert.False(snapshot.IsLocked);
        Assert.Equal(1, snapshot.DaysLeft);
        Assert.True(await service.HasFeatureAsync(Feature.Financials));
    }

    [Fact]
    public async Task PastDueAfterGrace_ExpiresLazilyAndLocksBusiness()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedBusinessAsync(
            ctx,
            planTier: PlanTiers.Elite,
            status: SubscriptionStatus.PastDue,
            currentPeriodEndsAt: Now.UtcDateTime.AddDays(-4));

        var service = CreateService(ctx, business.Id);
        var snapshot = await service.GetSubscriptionSnapshotAsync();
        var storedBusiness = await ctx.Businesses.FindAsync(business.Id);

        Assert.Equal(PlanTiers.Locked, snapshot.EffectivePlanTier);
        Assert.True(snapshot.IsLocked);
        Assert.Equal(SubscriptionStatus.Expired, snapshot.SubscriptionStatus);
        Assert.Equal(SubscriptionStatus.Expired, storedBusiness?.SubscriptionStatus);
        Assert.False(await service.HasFeatureAsync(Feature.ManualOrders));
    }

    private static IEntitlementService CreateService(AppDbContext ctx, int businessId)
    {
        var tenant = new TestCurrentTenant(businessId);
        var currentBusiness = new CurrentBusiness(ctx, tenant);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Subscriptions:PastDueGraceDays"] = "3"
            })
            .Build();

        return new EntitlementService(
            ctx,
            currentBusiness,
            configuration,
            new FixedTimeProvider(Now));
    }

    private static ActionExecutingContext CreateActionContext(IEntitlementService service)
    {
        var services = new ServiceCollection()
            .AddSingleton(service)
            .BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services
        };

        return new ActionExecutingContext(
            new ActionContext(
                httpContext,
                new RouteData(),
                new ActionDescriptor()),
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object());
    }

    private static async Task<Business> SeedBusinessAsync(
        AppDbContext ctx,
        string planTier,
        SubscriptionStatus status,
        DateTime? trialEndsAt = null,
        DateTime? currentPeriodEndsAt = null)
    {
        var business = new Business
        {
            Name = $"Tenant {Guid.NewGuid():N}",
            Slug = $"tenant-{Guid.NewGuid():N}",
            City = "Nuevo Laredo",
            FrontendUrl = "https://example.com",
            DepotLat = 27.4861,
            DepotLng = -99.5069,
            GeocodingRegion = "Nuevo Laredo, Tamaulipas, MX",
            GeminiBusinessName = "Tenant",
            PlanTier = planTier,
            SubscriptionStatus = status,
            TrialEndsAt = trialEndsAt,
            CurrentPeriodEndsAt = currentPeriodEndsAt,
            IsActive = true,
            CreatedAt = Now.UtcDateTime
        };

        ctx.Businesses.Add(business);
        await ctx.SaveChangesAsync();
        return business;
    }

    private sealed class TestCurrentTenant : ICurrentTenant
    {
        public TestCurrentTenant(int businessId)
        {
            ActiveBusinessId = businessId;
        }

        public int ActiveBusinessId { get; private set; }
        public bool IsResolved => true;
        public void SetBusiness(int businessId) => ActiveBusinessId = businessId;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
