using EntregasApi.Controllers;
using EntregasApi.Data;
using EntregasApi.Hubs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EntregasApi.Tests;

public class SubscriptionMpWebhookTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PreapprovalAuthorized_MarksBusinessActive()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedAsync(ctx, status: SubscriptionStatus.PastDue);

        var mp = new StubMpPlatform();
        mp.GetResponse = new MercadoPagoPreapproval(
            "PRE-1", "authorized", null, null, null,
            250m, "MXN",
            1, "months",
            Now.UtcDateTime.AddMonths(1),
            null, null,
            "owner@test.com", null,
            null);

        var controller = Build(ctx, mp, includeSignatureHeader: false);
        var result = await controller.HandleWebhook(new PaymentsWebhookController.MpWebhookNotification
        {
            Type = "preapproval",
            Data = new PaymentsWebhookController.MpWebhookData { Id = "PRE-1" }
        });

        Assert.IsType<OkResult>(result);
        var stored = await ctx.Businesses.FindAsync(business.Id);
        Assert.Equal(SubscriptionStatus.Active, stored!.SubscriptionStatus);
        Assert.Equal("authorized", stored.PreapprovalStatus);
        Assert.Equal(Now.UtcDateTime.AddMonths(1), stored.CurrentPeriodEndsAt);
        Assert.Null(stored.CancellationEffectiveAt);
    }

    [Fact]
    public async Task PreapprovalCancelled_KeepsActiveUntilPeriodEnd()
    {
        using var ctx = TestDbContextFactory.Create();
        var periodEnd = Now.UtcDateTime.AddDays(20);
        var business = await SeedAsync(ctx,
            status: SubscriptionStatus.Active,
            currentPeriodEndsAt: periodEnd);

        var mp = new StubMpPlatform();
        mp.GetResponse = new MercadoPagoPreapproval(
            "PRE-1", "cancelled", null, null, null,
            250m, "MXN",
            1, "months",
            periodEnd, null, periodEnd,
            "owner@test.com", null,
            null);

        var controller = Build(ctx, mp, includeSignatureHeader: false);
        var result = await controller.HandleWebhook(new PaymentsWebhookController.MpWebhookNotification
        {
            Type = "preapproval",
            Data = new PaymentsWebhookController.MpWebhookData { Id = "PRE-1" }
        });

        Assert.IsType<OkResult>(result);
        var stored = await ctx.Businesses.FindAsync(business.Id);
        Assert.Equal(SubscriptionStatus.Canceled, stored!.SubscriptionStatus);
        Assert.Equal(periodEnd, stored.CancellationEffectiveAt);
    }

    [Fact]
    public async Task AuthorizedPaymentApproved_ExtendsCurrentPeriodEndsAt()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedAsync(ctx,
            status: SubscriptionStatus.Active,
            currentPeriodEndsAt: Now.UtcDateTime.AddDays(5));

        var mp = new StubMpPlatform();
        mp.AuthorizedPaymentResponse = new MercadoPagoAuthorizedPayment(
            "INV-1", PreapprovalId: "PRE-1", Status: "approved",
            StatusDetail: "accredited", TransactionAmount: 250m,
            CurrencyId: "MXN", DateCreated: Now.UtcDateTime);

        var controller = Build(ctx, mp, includeSignatureHeader: false);
        var result = await controller.HandleWebhook(new PaymentsWebhookController.MpWebhookNotification
        {
            Type = "authorized_payment",
            Data = new PaymentsWebhookController.MpWebhookData { Id = "INV-1" }
        });

        Assert.IsType<OkResult>(result);
        var stored = await ctx.Businesses.FindAsync(business.Id);
        Assert.Equal(SubscriptionStatus.Active, stored!.SubscriptionStatus);
        Assert.Equal(Now.UtcDateTime.AddMonths(1), stored.CurrentPeriodEndsAt);
    }

    [Fact]
    public async Task AuthorizedPaymentRejected_TriggersPastDue()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedAsync(ctx,
            status: SubscriptionStatus.Active,
            currentPeriodEndsAt: Now.UtcDateTime.AddDays(20));

        var mp = new StubMpPlatform();
        mp.AuthorizedPaymentResponse = new MercadoPagoAuthorizedPayment(
            "INV-1", PreapprovalId: "PRE-1", Status: "rejected",
            StatusDetail: "cc_rejected_other_reason", TransactionAmount: 250m,
            CurrencyId: "MXN", DateCreated: Now.UtcDateTime);

        var controller = Build(ctx, mp, includeSignatureHeader: false);
        var result = await controller.HandleWebhook(new PaymentsWebhookController.MpWebhookNotification
        {
            Type = "authorized_payment",
            Data = new PaymentsWebhookController.MpWebhookData { Id = "INV-1" }
        });

        Assert.IsType<OkResult>(result);
        var stored = await ctx.Businesses.FindAsync(business.Id);
        Assert.Equal(SubscriptionStatus.PastDue, stored!.SubscriptionStatus);
    }

    [Fact]
    public async Task WebhookForUnknownPreapproval_IgnoresAndReturns200()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedAsync(ctx, status: SubscriptionStatus.Active);

        var mp = new StubMpPlatform();
        mp.GetResponse = new MercadoPagoPreapproval(
            "PRE-UNKNOWN", "authorized", null, null, null,
            250m, "MXN",
            1, "months",
            null, null, null,
            null, null,
            null);

        var controller = Build(ctx, mp, includeSignatureHeader: false);
        var result = await controller.HandleWebhook(new PaymentsWebhookController.MpWebhookNotification
        {
            Type = "preapproval",
            Data = new PaymentsWebhookController.MpWebhookData { Id = "PRE-UNKNOWN" }
        });

        Assert.IsType<OkResult>(result);
        var stored = await ctx.Businesses.FindAsync(business.Id);
        Assert.Equal("authorized", stored!.PreapprovalStatus); // sin cambios
    }

    [Fact]
    public async Task MissingSignature_OnPlatformWebhook_RejectsWithUnauthorized()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedAsync(ctx, status: SubscriptionStatus.Active);

        var mp = new StubMpPlatform { ValidateResult = false };
        var controller = Build(ctx, mp, includeSignatureHeader: false,
            webhookSecret: "real-secret");

        var result = await controller.HandleWebhook(new PaymentsWebhookController.MpWebhookNotification
        {
            Type = "preapproval",
            Data = new PaymentsWebhookController.MpWebhookData { Id = "PRE-1" }
        });

        Assert.IsType<UnauthorizedResult>(result);

        var stored = await ctx.Businesses.FindAsync(business.Id);
        Assert.Equal("authorized", stored!.PreapprovalStatus); // no fue actualizado
    }

    [Fact]
    public async Task ValidSignature_OnPlatformWebhook_PassesValidation()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedAsync(ctx, status: SubscriptionStatus.PastDue);

        var mp = new StubMpPlatform
        {
            ValidateResult = true,
            GetResponse = new MercadoPagoPreapproval(
                "PRE-1", "authorized", null, null, null,
                250m, "MXN",
                1, "months",
                Now.UtcDateTime.AddMonths(1),
                null, null,
                "owner@test.com", null,
                null)
        };

        var controller = Build(ctx, mp, includeSignatureHeader: true,
            webhookSecret: "real-secret");

        var result = await controller.HandleWebhook(new PaymentsWebhookController.MpWebhookNotification
        {
            Type = "preapproval",
            Data = new PaymentsWebhookController.MpWebhookData { Id = "PRE-1" }
        });

        Assert.IsType<OkResult>(result);
        var stored = await ctx.Businesses.FindAsync(business.Id);
        Assert.Equal(SubscriptionStatus.Active, stored!.SubscriptionStatus);
    }

    [Fact]
    public async Task OneTimePaymentWebhook_WithoutBusinessId_IsIgnoredAndReturns200()
    {
        using var ctx = TestDbContextFactory.Create();
        var handler = new RecordingHandler();
        var controller = Build(
            ctx,
            new StubMpPlatform(),
            includeSignatureHeader: false,
            httpHandler: handler);

        var result = await controller.HandleWebhook(new PaymentsWebhookController.MpWebhookNotification
        {
            Type = "payment",
            Data = new PaymentsWebhookController.MpWebhookData { Id = "PAY-1" }
        });

        Assert.IsType<OkResult>(result);
        Assert.Empty(handler.Sent);
    }

    private static async Task<Business> SeedAsync(
        AppDbContext ctx,
        SubscriptionStatus status,
        DateTime? currentPeriodEndsAt = null)
    {
        var business = new Business
        {
            Name = "Tienda Test",
            Slug = $"tienda-{Guid.NewGuid():N}",
            City = "Test",
            FrontendUrl = "https://test.com",
            DepotLat = 27.4861,
            DepotLng = -99.5069,
            GeocodingRegion = "Test, MX",
            GeminiBusinessName = "Test",
            PlanTier = PlanTiers.Pro,
            SubscriptionStatus = status,
            PreapprovalId = "PRE-1",
            PayerEmail = "owner@test.com",
            PreapprovalStatus = "authorized",
            SubscriptionPeriodMonths = 1,
            CurrentPeriodEndsAt = currentPeriodEndsAt,
            IsActive = true,
            CreatedAt = Now.UtcDateTime
        };
        ctx.Businesses.Add(business);
        await ctx.SaveChangesAsync();
        return business;
    }

    private static PaymentsWebhookController Build(
        AppDbContext ctx,
        IMercadoPagoSubscriptionService mp,
        bool includeSignatureHeader,
        string webhookSecret = "dummy",
        RecordingHandler? httpHandler = null)
    {
        httpHandler ??= new RecordingHandler();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Platform:MercadoPago:WebhookSecret"] = webhookSecret
            })
            .Build();

        var controller = new PaymentsWebhookController(
            ctx,
            new NullHubContext<DeliveryHub>(),
            new SimpleHttpClientFactory(httpHandler),
            config,
            mp,
            new FixedTimeProvider(Now),
            NullLogger<PaymentsWebhookController>.Instance);

        var http = new DefaultHttpContext();
        if (includeSignatureHeader)
        {
            http.Request.Headers["x-signature"] = "ts=1,v1=valid";
            http.Request.Headers["x-request-id"] = "req-1";
        }
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        return controller;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private class StubMpPlatform : IMercadoPagoSubscriptionService
    {
        public MercadoPagoPreapproval? GetResponse { get; set; }
        public MercadoPagoAuthorizedPayment? AuthorizedPaymentResponse { get; set; }
        public bool ValidateResult { get; set; } = true;

        public virtual Task<MercadoPagoPreapproval> CreatePreapprovalAsync(
            Business business, string planTier, SubscriptionPeriodicity periodicity,
            string payerEmail, string? cardTokenId, string externalReference,
            DateTime? firstChargeDate, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public virtual Task<MercadoPagoPreapproval> UpdatePreapprovalAsync(
            string preapprovalId, string planTier, SubscriptionPeriodicity periodicity,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public virtual Task<MercadoPagoPreapproval> CancelPreapprovalAsync(
            string preapprovalId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public virtual Task<MercadoPagoPreapproval?> GetPreapprovalAsync(
            string preapprovalId, CancellationToken cancellationToken = default)
            => Task.FromResult(GetResponse);

        public virtual Task<MercadoPagoAuthorizedPayment?> GetAuthorizedPaymentAsync(
            string authorizedPaymentId, CancellationToken cancellationToken = default)
            => Task.FromResult(AuthorizedPaymentResponse);

        public virtual bool ValidateWebhookSignature(
            string? requestId, string? signatureHeader, string? dataId, DateTimeOffset now)
            => ValidateResult;
    }
}

internal sealed class NullHubContext<THub> : IHubContext<THub> where THub : Hub
{
    public IHubClients Clients => new NullHubClients();
    public IGroupManager Groups => new NullGroupManager();
}

internal sealed class NullHubClients : IHubClients
{
    public IClientProxy All => new NullClientProxy();
    public IClientProxy AllExcept(System.Collections.Generic.IReadOnlyList<string> ids) => new NullClientProxy();
    public IClientProxy Client(string id) => new NullClientProxy();
    public IClientProxy Clients(System.Collections.Generic.IReadOnlyList<string> ids) => new NullClientProxy();
    public IClientProxy Clients(System.Collections.Generic.IEnumerable<string> ids) => new NullClientProxy();
    public IClientProxy Group(string name) => new NullClientProxy();
    public IClientProxy GroupExcept(string name, System.Collections.Generic.IReadOnlyList<string> ids) => new NullClientProxy();
    public IClientProxy Groups(System.Collections.Generic.IReadOnlyList<string> names) => new NullClientProxy();
    public IClientProxy Groups(System.Collections.Generic.IEnumerable<string> names) => new NullClientProxy();
    public IClientProxy User(string id) => new NullClientProxy();
    public IClientProxy Users(System.Collections.Generic.IReadOnlyList<string> ids) => new NullClientProxy();
    public IClientProxy Users(System.Collections.Generic.IEnumerable<string> ids) => new NullClientProxy();
}

internal sealed class NullGroupManager : IGroupManager
{
    public Task AddToGroupAsync(string id, string name, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveFromGroupAsync(string id, string name, CancellationToken ct = default) => Task.CompletedTask;
    public Task AddToGroupsAsync(System.Collections.Generic.IEnumerable<string> ids, System.Collections.Generic.IEnumerable<string> names, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveFromGroupsAsync(System.Collections.Generic.IEnumerable<string> ids, System.Collections.Generic.IEnumerable<string> names, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class NullClientProxy : IClientProxy
{
    public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default) => Task.CompletedTask;
    public Task SendCoreAsync(string method, object?[] args, CancellationToken ct, System.Collections.Generic.IReadOnlyList<string>? excluded = null) => Task.CompletedTask;
}
