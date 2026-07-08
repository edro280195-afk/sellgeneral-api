using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntregasApi.Tests;

public class LiveAnnouncementServiceTests
{
    [Fact]
    public async Task Start_CreatesActiveAnnouncement_AndNotifiesFollowers()
    {
        using var ctx = NewContext();
        var (tenant, _) = SeedTenant(ctx, businessId: 1);
        var follower = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(follower);
        await ctx.SaveChangesAsync();
        ctx.StoreFollowers.Add(new StoreFollower { BusinessId = 1, AccountId = follower.Id, NotifyOnLive = true });
        await ctx.SaveChangesAsync();

        var push = new RecordingPushService();
        var dto = await ServiceWith(ctx, tenant, push).StartAsync(new StartLiveAnnouncementRequest("Rebaja de fin de semana"));

        Assert.True(dto.IsActive);
        Assert.Equal("Rebaja de fin de semana", dto.Title);
        Assert.Single(push.FollowerCalls);
        Assert.Equal(1, push.FollowerCalls[0].BusinessId);
        Assert.True(push.FollowerCalls[0].RequireNotifyOnLive);
    }

    [Fact]
    public async Task Start_WhenAlreadyActive_ThrowsConflict()
    {
        using var ctx = NewContext();
        var (tenant, _) = SeedTenant(ctx, businessId: 1);
        var service = ServiceWith(ctx, tenant, new RecordingPushService());

        await service.StartAsync(new StartLiveAnnouncementRequest("Primero"));

        var ex = await Assert.ThrowsAsync<LiveAnnouncementAlreadyActiveException>(
            () => service.StartAsync(new StartLiveAnnouncementRequest("Segundo")));
        Assert.True(ex.Active.IsActive);
    }

    [Fact]
    public async Task Start_AfterEnding_AllowsNewAnnouncement()
    {
        using var ctx = NewContext();
        var (tenant, _) = SeedTenant(ctx, businessId: 1);
        var service = ServiceWith(ctx, tenant, new RecordingPushService());

        var first = await service.StartAsync(new StartLiveAnnouncementRequest(null));
        await service.EndAsync(first.Id);
        var second = await service.StartAsync(new StartLiveAnnouncementRequest("Otro vivo"));

        Assert.True(second.IsActive);
        Assert.Equal("Otro vivo", second.Title);
    }

    [Fact]
    public async Task GetActive_ExpiredByTtl_ReturnsNull()
    {
        using var ctx = NewContext();
        var (tenant, businessId) = SeedTenant(ctx, businessId: 1);
        ctx.LiveAnnouncements.Add(new LiveAnnouncement
        {
            BusinessId = businessId,
            StartedAt = DateTime.UtcNow.AddHours(-4), // fuera del TTL de 3h
            EndedAt = null,
        });
        await ctx.SaveChangesAsync();

        var active = await ServiceWith(ctx, tenant, new RecordingPushService()).GetActiveAsync();

        Assert.Null(active);
    }

    [Fact]
    public async Task GetActive_IsolatedBetweenBusinesses()
    {
        using var ctx = NewContext();
        var (tenantA, businessAId) = SeedTenant(ctx, businessId: 1);
        var bizB = NewBusiness("Luna Bella", "luna", "#FF7A59");
        bizB.Id = 2;
        ctx.Businesses.Add(bizB);
        await ctx.SaveChangesAsync();
        ctx.LiveAnnouncements.Add(new LiveAnnouncement { BusinessId = 2, StartedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();

        var active = await ServiceWith(ctx, tenantA, new RecordingPushService()).GetActiveAsync();

        Assert.Null(active);
    }

    private static AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid():N}")
            .Options;
        return new AppDbContext(options);
    }

    private static (FakeCurrentTenant, int) SeedTenant(AppDbContext ctx, int businessId)
    {
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        business.Id = businessId;
        ctx.Businesses.Add(business);
        ctx.SaveChanges();
        return (new FakeCurrentTenant(businessId), businessId);
    }

    private static Business NewBusiness(string name, string slug, string color) => new()
    {
        Name = name,
        Slug = slug,
        City = "Nuevo Laredo",
        FrontendUrl = "https://example.com",
        BrandPrimaryColor = color,
        DepotLat = 27.4861,
        DepotLng = -99.5069,
    };

    private static ILiveAnnouncementService ServiceWith(
        AppDbContext ctx, ICurrentTenant tenant, IPushNotificationService push) =>
        new LiveAnnouncementService(ctx, tenant, push);

    private sealed class FakeCurrentTenant : ICurrentTenant
    {
        public FakeCurrentTenant(int businessId) => ActiveBusinessId = businessId;
        public int ActiveBusinessId { get; }
        public bool IsResolved => true;
        public void SetBusiness(int businessId) { }
    }

    private sealed class RecordingPushService : IPushNotificationService
    {
        public List<(int BusinessId, bool RequireNotifyOnLive, bool VipOnly)> FollowerCalls { get; } = new();

        public Task SendNotificationToClientAsync(int clientId, string title, string message, string? url = null, string? tag = null) => Task.CompletedTask;
        public Task SendNotificationToDriverAsync(string routeToken, string title, string message, string? url = null, string? tag = null) => Task.CompletedTask;
        public Task SendNotificationToAdminsAsync(string title, string message, string? url = null, string? tag = null) => Task.CompletedTask;
        public Task SendNotificationToFollowersAsync(int businessId, string title, string message, string? url = null, string? tag = null, bool vipOnly = false, bool requireNotifyOnPost = false, bool requireNotifyOnLive = false)
        {
            FollowerCalls.Add((businessId, requireNotifyOnLive, vipOnly));
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
