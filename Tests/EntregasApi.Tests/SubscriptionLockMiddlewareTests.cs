using System.Security.Claims;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace EntregasApi.Tests;

public class SubscriptionLockMiddlewareTests
{
    [Fact]
    public async Task LockedAuthenticatedBusinessEndpoint_ReturnsPaymentRequired()
    {
        var nextCalled = false;
        var middleware = new SubscriptionLockMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateContext(new AuthorizeAttribute());
        var tenant = new TestCurrentTenant(10);
        var entitlements = new FakeEntitlementService(isLocked: true);

        await middleware.InvokeAsync(context, tenant, entitlements);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status402PaymentRequired, context.Response.StatusCode);
    }

    [Fact]
    public async Task BypassSubscriptionLock_AllowsNextMiddleware()
    {
        var nextCalled = false;
        var middleware = new SubscriptionLockMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateContext(new AuthorizeAttribute(), new BypassSubscriptionLockAttribute());
        var tenant = new TestCurrentTenant(10);
        var entitlements = new FakeEntitlementService(isLocked: true);

        await middleware.InvokeAsync(context, tenant, entitlements);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task PublicEndpoint_AllowsNextMiddleware()
    {
        var nextCalled = false;
        var middleware = new SubscriptionLockMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateContext(new AllowAnonymousAttribute());
        var tenant = new TestCurrentTenant(10);
        var entitlements = new FakeEntitlementService(isLocked: true);

        await middleware.InvokeAsync(context, tenant, entitlements);

        Assert.True(nextCalled);
    }

    private static DefaultHttpContext CreateContext(params object[] metadata)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "1")],
            authenticationType: "test"));
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(metadata),
            displayName: "test"));

        return context;
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

    private sealed class FakeEntitlementService : IEntitlementService
    {
        private readonly bool _isLocked;

        public FakeEntitlementService(bool isLocked)
        {
            _isLocked = isLocked;
        }

        public Task<bool> HasFeatureAsync(Feature feature, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(!_isLocked);
        }

        public Task<int> GetLimitAsync(LimitKey limitKey, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_isLocked ? 0 : int.MaxValue);
        }

        public Task<string> EffectivePlanTierAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_isLocked ? PlanTiers.Locked : PlanTiers.Pro);
        }

        public Task<SubscriptionSnapshot> GetSubscriptionSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var effectivePlan = _isLocked ? PlanTiers.Locked : PlanTiers.Pro;
            return Task.FromResult(new SubscriptionSnapshot(
                effectivePlan,
                PlanTiers.Pro,
                _isLocked ? SubscriptionStatus.Expired : SubscriptionStatus.Active,
                null,
                null,
                null,
                null,
                _isLocked,
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
            => Task.FromResult<IReadOnlyList<Feature>>(Array.Empty<Feature>());
    }
}
