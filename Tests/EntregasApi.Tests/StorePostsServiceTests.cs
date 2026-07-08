using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntregasApi.Tests;

public class StorePostsServiceTests
{
    [Fact]
    public async Task Create_PersistsPost_AndNotifiesFollowers()
    {
        using var ctx = NewContext();
        SeedBusiness(ctx, 1);
        var follower = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(follower);
        await ctx.SaveChangesAsync();
        ctx.StoreFollowers.Add(new StoreFollower { BusinessId = 1, AccountId = follower.Id, NotifyOnPost = true });
        await ctx.SaveChangesAsync();

        var push = new RecordingPushService();
        var dto = await ServiceWith(ctx, push, vipAllowed: true)
            .CreateAsync(new CreateStorePostRequest("Llegaron vestidos nuevos", null, IsVipOnly: false));

        Assert.False(dto.IsVipOnly);
        Assert.Equal(1, await ctx.StorePosts.CountAsync());
        Assert.Single(push.FollowerCalls);
        Assert.True(push.FollowerCalls[0].RequireNotifyOnPost);
    }

    [Fact]
    public async Task Create_VipOnly_WithoutFeature_Throws()
    {
        using var ctx = NewContext();
        SeedBusiness(ctx, 1);

        await Assert.ThrowsAsync<StorePostVipNotAllowedException>(() =>
            ServiceWith(ctx, new RecordingPushService(), vipAllowed: false)
                .CreateAsync(new CreateStorePostRequest("Drop VIP", null, IsVipOnly: true)));

        Assert.Equal(0, await ctx.StorePosts.CountAsync());
    }

    [Fact]
    public async Task Create_VipOnly_WithFeature_Succeeds()
    {
        using var ctx = NewContext();
        SeedBusiness(ctx, 1);

        var dto = await ServiceWith(ctx, new RecordingPushService(), vipAllowed: true)
            .CreateAsync(new CreateStorePostRequest("Drop VIP", null, IsVipOnly: true));

        Assert.True(dto.IsVipOnly);
    }

    [Fact]
    public async Task GetMine_ExcludesDeleted_OrdersDescending()
    {
        using var ctx = NewContext();
        SeedBusiness(ctx, 1);
        var service = ServiceWith(ctx, new RecordingPushService(), vipAllowed: true);

        var first = await service.CreateAsync(new CreateStorePostRequest("Primero", null, false));
        var second = await service.CreateAsync(new CreateStorePostRequest("Segundo", null, false));
        await service.DeleteAsync(first.Id);

        var mine = await service.GetMineAsync(1, 20);

        var single = Assert.Single(mine);
        Assert.Equal(second.Id, single.Id);
    }

    [Fact]
    public async Task Delete_Nonexistent_ThrowsNotFound()
    {
        using var ctx = NewContext();
        SeedBusiness(ctx, 1);

        await Assert.ThrowsAsync<StorePostNotFoundException>(
            () => ServiceWith(ctx, new RecordingPushService(), vipAllowed: true).DeleteAsync(999));
    }

    private static AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid():N}")
            .Options;
        return new AppDbContext(options);
    }

    private static void SeedBusiness(AppDbContext ctx, int businessId)
    {
        ctx.Businesses.Add(new Business
        {
            Id = businessId,
            Name = "Regi Bazar",
            Slug = "regibazar",
            City = "Nuevo Laredo",
            FrontendUrl = "https://example.com",
            BrandPrimaryColor = "#FF0072",
            DepotLat = 27.4861,
            DepotLng = -99.5069,
        });
        ctx.SaveChanges();
    }

    private static IStorePostsService ServiceWith(AppDbContext ctx, IPushNotificationService push, bool vipAllowed) =>
        new StorePostsService(ctx, new FakeCurrentTenant(1), new FakeEntitlementService(vipAllowed), push);

    private sealed class FakeCurrentTenant : ICurrentTenant
    {
        public FakeCurrentTenant(int businessId) => ActiveBusinessId = businessId;
        public int ActiveBusinessId { get; }
        public bool IsResolved => true;
        public void SetBusiness(int businessId) { }
    }

    private sealed class FakeEntitlementService : IEntitlementService
    {
        private readonly bool _hasVipDrops;
        public FakeEntitlementService(bool hasVipDrops) => _hasVipDrops = hasVipDrops;

        public Task<bool> HasFeatureAsync(Feature feature, CancellationToken cancellationToken = default) =>
            Task.FromResult(feature != Feature.VipDrops || _hasVipDrops);

        public Task<int> GetLimitAsync(LimitKey limitKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(int.MaxValue);

        public Task<string> EffectivePlanTierAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(PlanTiers.Pro);

        public Task<SubscriptionSnapshot> GetSubscriptionSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SubscriptionSnapshot(PlanTiers.Pro, PlanTiers.Pro, SubscriptionStatus.Active, null, null, null, null, false, 0, 3));

        public Task EnsureWithinLimitAsync(LimitKey limitKey, int currentCount, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<Feature>> GetEnabledFeaturesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Feature>>(new List<Feature>());
    }

    private sealed class RecordingPushService : IPushNotificationService
    {
        public List<(bool RequireNotifyOnPost, bool VipOnly)> FollowerCalls { get; } = new();

        public Task SendNotificationToClientAsync(int clientId, string title, string message, string? url = null, string? tag = null) => Task.CompletedTask;
        public Task SendNotificationToDriverAsync(string routeToken, string title, string message, string? url = null, string? tag = null) => Task.CompletedTask;
        public Task SendNotificationToAdminsAsync(string title, string message, string? url = null, string? tag = null) => Task.CompletedTask;
        public Task SendNotificationToFollowersAsync(int businessId, string title, string message, string? url = null, string? tag = null, bool vipOnly = false, bool requireNotifyOnPost = false, bool requireNotifyOnLive = false)
        {
            FollowerCalls.Add((requireNotifyOnPost, vipOnly));
            return Task.CompletedTask;
        }
        public Task NotifyClientDriverEnRouteAsync(int clientId, string? driverName = null) => Task.CompletedTask;
        public Task NotifyClientDriverNearbyAsync(int clientId, int distanceMeters) => Task.CompletedTask;
        public Task NotifyClientDeliveredAsync(int clientId) => Task.CompletedTask;
        public Task NotifyChatMessageAsync(string targetRole, int? clientId, string? routeToken, string senderName, string messageText) => Task.CompletedTask;
        public Task NotifyDriversNewRouteAsync(string routeName, string driverToken, int deliveryCount) => Task.CompletedTask;
        public Task NotifyDriverFcmAsync(string driverRouteToken, string title, string body, Dictionary<string, string>? data = null) => Task.CompletedTask;
        public Task BroadcastToAllDriversAsync(string title, string body, Dictionary<string, string>? data = null) => Task.CompletedTask;
    }
}
