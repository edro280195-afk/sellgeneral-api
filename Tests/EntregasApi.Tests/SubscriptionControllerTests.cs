using System.Security.Claims;
using EntregasApi.Controllers;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EntregasApi.Tests;

public class SubscriptionControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreatePreapproval_OnFreshTenant_StoresMpStateAndActiveSubscription()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedBusinessAsync(ctx, planTier: PlanTiers.Pro,
            status: SubscriptionStatus.Trialing,
            trialEndsAt: Now.UtcDateTime.AddDays(5));

        var mp = new StubMpPlatform();
        mp.CreateResponse = MakePreapproval(
            "PRE-1", "authorized", 250m, 1,
            nextPaymentDate: Now.UtcDateTime.AddDays(5),
            startDate: Now.UtcDateTime.AddDays(5),
            payerEmail: "owner@test.com", payerId: 100,
            initPoint: "https://mp.test/checkout?preapproval_id=PRE-1");

        var controller = BuildController(ctx, business.Id, mp, out _);

        var result = await controller.CreatePreapproval(new CreatePreapprovalRequest(
            PlanTier: PlanTiers.Pro,
            Periodicity: "monthly",
            PayerEmail: "owner@test.com",
            CardTokenId: "tok-x"), default);

        var ok = Assert.IsType<ActionResult<PreapprovalSummaryDto>>(result);
        var summary = Assert.IsType<PreapprovalSummaryDto>(ok.Value);
        Assert.Equal("PRE-1", summary.PreapprovalId);
        Assert.Equal(PlanTiers.Pro, summary.PlanTier);
        Assert.Equal(SubscriptionPeriodicities.Monthly, summary.Periodicity);
        Assert.Equal(250m, summary.Amount);
        Assert.Equal("authorized", summary.Status);
        Assert.Equal(Now.UtcDateTime.AddDays(5), summary.CurrentPeriodEndsAt);

        var stored = await ctx.Businesses.FindAsync(business.Id);
        Assert.NotNull(stored);
        Assert.Equal("PRE-1", stored!.PreapprovalId);
        Assert.Equal("owner@test.com", stored.PayerEmail);
        Assert.Equal(SubscriptionStatus.Active, stored.SubscriptionStatus);
        Assert.Null(stored.TrialEndsAt);
        Assert.Equal(1, stored.SubscriptionPeriodMonths);

        Assert.Single(mp.CreateCalls);
        Assert.Equal("tok-x", mp.CreateCalls[0].CardTokenId);
        Assert.Equal("owner@test.com", mp.CreateCalls[0].PayerEmail);
        Assert.Equal(PlanTiers.Pro, mp.CreateCalls[0].PlanTier);
        Assert.Equal(SubscriptionPeriodicity.Monthly, mp.CreateCalls[0].Periodicity);
    }

    [Fact]
    public async Task CreatePreapproval_OnExistingPreapproval_RoutesToUpdate()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedBusinessAsync(ctx, planTier: PlanTiers.Entrada,
            status: SubscriptionStatus.Active);
        business.PreapprovalId = "PRE-EXISTING";
        business.PayerEmail = "owner@test.com";
        business.PreapprovalStatus = "authorized";
        business.SubscriptionPeriodMonths = 1;
        business.CurrentPeriodEndsAt = Now.UtcDateTime.AddDays(20);
        await ctx.SaveChangesAsync();

        var mp = new StubMpPlatform();
        mp.UpdateResponse = MakePreapproval("PRE-EXISTING", "authorized", 250m, 1,
            nextPaymentDate: Now.UtcDateTime.AddDays(20));

        var controller = BuildController(ctx, business.Id, mp, out _);

        var result = await controller.CreatePreapproval(new CreatePreapprovalRequest(
            PlanTier: PlanTiers.Pro,
            Periodicity: "monthly",
            PayerEmail: "owner@test.com"), default);

        var ok = Assert.IsType<ActionResult<PreapprovalSummaryDto>>(result);
        var summary = Assert.IsType<PreapprovalSummaryDto>(ok.Value);
        Assert.Equal(250m, summary.Amount);
        Assert.Equal(PlanTiers.Pro, summary.PlanTier);

        Assert.Empty(mp.CreateCalls);
        Assert.Single(mp.UpdateCalls);
        Assert.Equal("PRE-EXISTING", mp.UpdateCalls[0].PreapprovalId);
        Assert.Equal(PlanTiers.Pro, mp.UpdateCalls[0].PlanTier);

        var stored = await ctx.Businesses.FindAsync(business.Id);
        Assert.Equal(PlanTiers.Pro, stored!.PlanTier);
    }

    [Fact]
    public async Task CreatePreapproval_UpgradeFromLocked_ReactivatesImmediately()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedBusinessAsync(ctx, planTier: PlanTiers.Locked,
            status: SubscriptionStatus.Expired);

        var mp = new StubMpPlatform();
        mp.CreateResponse = MakePreapproval("PRE-NEW", "authorized", 250m, 1,
            nextPaymentDate: Now.UtcDateTime.AddMonths(1));

        var controller = BuildController(ctx, business.Id, mp, out _);

        var result = await controller.CreatePreapproval(new CreatePreapprovalRequest(
            PlanTier: PlanTiers.Pro,
            Periodicity: "monthly",
            PayerEmail: "owner@test.com"), default);

        var ok = Assert.IsType<ActionResult<PreapprovalSummaryDto>>(result);
        var summary = Assert.IsType<PreapprovalSummaryDto>(ok.Value);
        Assert.Equal(PlanTiers.Pro, summary.PlanTier);

        var stored = await ctx.Businesses.FindAsync(business.Id);
        Assert.Equal(SubscriptionStatus.Active, stored!.SubscriptionStatus);
        Assert.Equal(PlanTiers.Pro, stored.PlanTier);
        Assert.Equal("PRE-NEW", stored.PreapprovalId);
    }

    [Fact]
    public async Task UpdatePreapproval_UpgradeChangesAmountImmediately()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedBusinessAsync(ctx, planTier: PlanTiers.Entrada,
            status: SubscriptionStatus.Active);
        business.PreapprovalId = "PRE-EXISTING";
        business.PayerEmail = "owner@test.com";
        business.PreapprovalStatus = "authorized";
        business.CurrentPeriodEndsAt = Now.UtcDateTime.AddDays(20);
        await ctx.SaveChangesAsync();

        var mp = new StubMpPlatform();
        mp.UpdateResponse = MakePreapproval("PRE-EXISTING", "authorized", 250m, 1,
            nextPaymentDate: Now.UtcDateTime.AddDays(20));

        var controller = BuildController(ctx, business.Id, mp, out _);

        var result = await controller.UpdatePreapproval(new UpdatePreapprovalRequest(
            PlanTier: PlanTiers.Pro, Periodicity: "monthly"), default);

        var ok = Assert.IsType<ActionResult<PreapprovalSummaryDto>>(result);
        var summary = Assert.IsType<PreapprovalSummaryDto>(ok.Value);
        Assert.Equal(PlanTiers.Pro, summary.PlanTier);
        Assert.Equal(250m, summary.Amount);

        var stored = await ctx.Businesses.FindAsync(business.Id);
        Assert.Equal(PlanTiers.Pro, stored!.PlanTier);
        Assert.Null(stored.PendingPlanTier);
    }

    [Fact]
    public async Task UpdatePreapproval_DowngradeSchedulesAtEndOfPeriod()
    {
        using var ctx = TestDbContextFactory.Create();
        var periodEnd = Now.UtcDateTime.AddDays(20);
        var business = await SeedBusinessAsync(ctx, planTier: PlanTiers.Elite,
            status: SubscriptionStatus.Active);
        business.PreapprovalId = "PRE-EXISTING";
        business.PayerEmail = "owner@test.com";
        business.PreapprovalStatus = "authorized";
        business.CurrentPeriodEndsAt = periodEnd;
        await ctx.SaveChangesAsync();

        var mp = new StubMpPlatform();
        mp.UpdateResponse = MakePreapproval("PRE-EXISTING", "authorized", 348.30m, 3,
            nextPaymentDate: periodEnd);

        var controller = BuildController(ctx, business.Id, mp, out _);

        var result = await controller.UpdatePreapproval(new UpdatePreapprovalRequest(
            PlanTier: PlanTiers.Entrada, Periodicity: "quarterly"), default);

        var ok = Assert.IsType<ActionResult<PreapprovalSummaryDto>>(result);
        var summary = Assert.IsType<PreapprovalSummaryDto>(ok.Value);
        Assert.Equal(PlanTiers.Elite, summary.PlanTier); // plan vigente no cambia hasta fin de periodo
        Assert.Equal(SubscriptionPeriodicities.Quarterly, summary.Periodicity);

        var stored = await ctx.Businesses.FindAsync(business.Id);
        Assert.Equal(PlanTiers.Elite, stored!.PlanTier);
        Assert.Equal(PlanTiers.Entrada, stored.PendingPlanTier);
        Assert.Equal(periodEnd, stored.PendingPlanEffectiveAt);
        Assert.Equal(3, stored.SubscriptionPeriodMonths);
    }

    [Fact]
    public async Task CancelPreapproval_SetsCanceledAndEffectiveAt()
    {
        using var ctx = TestDbContextFactory.Create();
        var periodEnd = Now.UtcDateTime.AddDays(20);
        var business = await SeedBusinessAsync(ctx, planTier: PlanTiers.Pro,
            status: SubscriptionStatus.Active);
        business.PreapprovalId = "PRE-EXISTING";
        business.PayerEmail = "owner@test.com";
        business.PreapprovalStatus = "authorized";
        business.CurrentPeriodEndsAt = periodEnd;
        await ctx.SaveChangesAsync();

        var mp = new StubMpPlatform();
        mp.CancelResponse = MakePreapproval("PRE-EXISTING", "cancelled", 250m, 1,
            nextPaymentDate: periodEnd, endDate: periodEnd);

        var controller = BuildController(ctx, business.Id, mp, out _);

        var result = await controller.CancelPreapproval(default);

        var ok = Assert.IsType<ActionResult<PreapprovalSummaryDto>>(result);
        var summary = Assert.IsType<PreapprovalSummaryDto>(ok.Value);
        Assert.Equal("cancelled", summary.Status);

        var stored = await ctx.Businesses.FindAsync(business.Id);
        Assert.Equal(SubscriptionStatus.Canceled, stored!.SubscriptionStatus);
        Assert.Equal(periodEnd, stored.CancellationEffectiveAt);
        Assert.Equal("cancelled", stored.PreapprovalStatus);
        Assert.Single(mp.CancelCalls);
        Assert.Equal("PRE-EXISTING", mp.CancelCalls[0]);
    }

    [Fact]
    public async Task GetPlatformPublicKey_ReturnsConfiguredKey()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedBusinessAsync(ctx, planTier: PlanTiers.Pro,
            status: SubscriptionStatus.Active);

        var controller = BuildController(ctx, business.Id, new StubMpPlatform(), out _);

        var result = controller.GetPlatformPublicKey();
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<PlatformMpPublicKeyDto>(okResult.Value);
        Assert.Equal("TEST-PUB", dto.PublicKey);
    }

    [Fact]
    public async Task GetPricing_ReturnsPlansWithDiscounts()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedBusinessAsync(ctx, planTier: PlanTiers.Pro,
            status: SubscriptionStatus.Active);

        var controller = BuildController(ctx, business.Id, new StubMpPlatform(), out _);

        var result = controller.GetPricing();
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var pricing = Assert.IsType<SubscriptionPricingDto>(okResult.Value);
        Assert.Equal("MXN", pricing.Currency);
        Assert.Equal(3, pricing.Plans.Count);

        var pro = pricing.Plans.Single(p => p.PlanTier == PlanTiers.Pro);
        Assert.Equal(250m, pro.MonthlyPrice);
        Assert.Equal(675m, pro.QuarterlyPrice);
        Assert.Equal(2400m, pro.AnnualPrice);
        Assert.Equal(10, pro.QuarterlyDiscountPct);
        Assert.Equal(20, pro.AnnualDiscountPct);
    }

    [Fact]
    public async Task CreatePreapproval_MpFailure_Returns502()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedBusinessAsync(ctx, planTier: PlanTiers.Pro,
            status: SubscriptionStatus.Trialing,
            trialEndsAt: Now.UtcDateTime.AddDays(5));

        var mp = new StubMpPlatform
        {
            CreateException = new MercadoPagoSubscriptionException("MP respondio 503")
        };

        var controller = BuildController(ctx, business.Id, mp, out _);

        var result = await controller.CreatePreapproval(new CreatePreapprovalRequest(
            PlanTier: PlanTiers.Pro,
            Periodicity: "monthly",
            PayerEmail: "owner@test.com"), default);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, objectResult.StatusCode);

        var stored = await ctx.Businesses.FindAsync(business.Id);
        Assert.Null(stored!.PreapprovalId);
        Assert.Equal(SubscriptionStatus.Trialing, stored.SubscriptionStatus);
    }

    private static SubscriptionController BuildController(
        AppDbContext db,
        int businessId,
        IMercadoPagoSubscriptionService mp,
        out IConfiguration config)
    {
        var tenant = new TestCurrentTenant(businessId);
        var currentBusiness = new CurrentBusiness(db, tenant);
        config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Subscriptions:PeriodicityDiscounts:Quarterly"] = "10",
                ["Subscriptions:PeriodicityDiscounts:Annual"] = "20"
            })
            .Build();

        var mpOptions = Options.Create(new MercadoPagoSubscriptionOptions
        {
            PublicKey = "TEST-PUB",
            Currency = "MXN"
        });

        var controller = new SubscriptionController(
            db,
            tenant,
            currentBusiness,
            new FakeEntitlementService(isLocked: false),
            mp,
            new FixedTimeProvider(Now),
            mpOptions,
            NullLogger<SubscriptionController>.Instance);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection()
                    .AddSingleton<IConfiguration>(config)
                    .BuildServiceProvider()
            }
        };

        return controller;
    }

    private static MercadoPagoPreapproval MakePreapproval(
        string id,
        string status,
        decimal amount,
        int frequency,
        DateTime? nextPaymentDate = null,
        DateTime? startDate = null,
        DateTime? endDate = null,
        string? payerEmail = null,
        long? payerId = null,
        string? initPoint = null)
    {
        return new MercadoPagoPreapproval(
            Id: id,
            Status: status,
            PreapprovalPlanId: null,
            Reason: null,
            ExternalReference: null,
            TransactionAmount: amount,
            CurrencyId: "MXN",
            Frequency: frequency,
            FrequencyType: "months",
            NextPaymentDate: nextPaymentDate,
            StartDate: startDate,
            EndDate: endDate,
            PayerEmail: payerEmail,
            PayerId: payerId,
            InitPoint: initPoint);
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
            City = "Test",
            FrontendUrl = "https://test.example.com",
            DepotLat = 27.4861,
            DepotLng = -99.5069,
            GeocodingRegion = "Test, MX",
            GeminiBusinessName = "Test",
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
        public TestCurrentTenant(int businessId) { ActiveBusinessId = businessId; }
        public int ActiveBusinessId { get; private set; }
        public bool IsResolved => true;
        public void SetBusiness(int businessId) => ActiveBusinessId = businessId;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FakeEntitlementService : IEntitlementService
    {
        private readonly bool _isLocked;
        public FakeEntitlementService(bool isLocked) => _isLocked = isLocked;

        public Task<bool> HasFeatureAsync(Feature feature, CancellationToken cancellationToken = default)
            => Task.FromResult(!_isLocked);

        public Task<int> GetLimitAsync(LimitKey limitKey, CancellationToken cancellationToken = default)
            => Task.FromResult(_isLocked ? 0 : int.MaxValue);

        public Task<string> EffectivePlanTierAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_isLocked ? PlanTiers.Locked : PlanTiers.Pro);

        public Task<SubscriptionSnapshot> GetSubscriptionSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SubscriptionSnapshot(
                _isLocked ? PlanTiers.Locked : PlanTiers.Pro,
                PlanTiers.Pro,
                _isLocked ? SubscriptionStatus.Expired : SubscriptionStatus.Active,
                null, null, null, null,
                _isLocked, 0, 3));
        }

        public Task EnsureWithinLimitAsync(LimitKey limitKey, int currentCount, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<Feature>> GetEnabledFeaturesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Feature>>(_isLocked
                ? Array.Empty<Feature>()
                : new[] { Feature.ManualOrders, Feature.ClientDirectory, Feature.Financials });
    }
}

internal sealed class StubMpPlatform : IMercadoPagoSubscriptionService
{
    public MercadoPagoPreapproval? CreateResponse { get; set; }
    public MercadoPagoPreapproval? UpdateResponse { get; set; }
    public MercadoPagoPreapproval? CancelResponse { get; set; }
    public MercadoPagoSubscriptionException? CreateException { get; set; }
    public MercadoPagoSubscriptionException? UpdateException { get; set; }
    public MercadoPagoSubscriptionException? CancelException { get; set; }

    public List<CreateCall> CreateCalls { get; } = new();
    public List<UpdateCall> UpdateCalls { get; } = new();
    public List<string> CancelCalls { get; } = new();

    public Task<MercadoPagoPreapproval> CreatePreapprovalAsync(
        Business business,
        string planTier,
        SubscriptionPeriodicity periodicity,
        string payerEmail,
        string? cardTokenId,
        string externalReference,
        DateTime? firstChargeDate,
        CancellationToken cancellationToken = default)
    {
        CreateCalls.Add(new CreateCall(planTier, periodicity, payerEmail, cardTokenId));
        if (CreateException is not null) throw CreateException;
        return Task.FromResult(CreateResponse!);
    }

    public Task<MercadoPagoPreapproval> UpdatePreapprovalAsync(
        string preapprovalId, string planTier, SubscriptionPeriodicity periodicity,
        CancellationToken cancellationToken = default)
    {
        UpdateCalls.Add(new UpdateCall(preapprovalId, planTier, periodicity));
        if (UpdateException is not null) throw UpdateException;
        return Task.FromResult(UpdateResponse!);
    }

    public Task<MercadoPagoPreapproval> CancelPreapprovalAsync(
        string preapprovalId, CancellationToken cancellationToken = default)
    {
        CancelCalls.Add(preapprovalId);
        if (CancelException is not null) throw CancelException;
        return Task.FromResult(CancelResponse!);
    }

    public Task<MercadoPagoPreapproval?> GetPreapprovalAsync(
        string preapprovalId, CancellationToken cancellationToken = default)
        => Task.FromResult<MercadoPagoPreapproval?>(UpdateResponse);

    public Task<MercadoPagoAuthorizedPayment?> GetAuthorizedPaymentAsync(
        string authorizedPaymentId, CancellationToken cancellationToken = default)
        => Task.FromResult<MercadoPagoAuthorizedPayment?>(null);

    public bool ValidateWebhookSignature(string? requestId, string? signatureHeader, string? dataId, DateTimeOffset now)
        => true;

    public sealed record CreateCall(string PlanTier, SubscriptionPeriodicity Periodicity, string PayerEmail, string? CardTokenId);
    public sealed record UpdateCall(string PreapprovalId, string PlanTier, SubscriptionPeriodicity Periodicity);
}
