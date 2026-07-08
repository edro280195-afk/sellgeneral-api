using EntregasApi.Data;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EntregasApi.Tests;

/// <summary>
/// Cubre <see cref="PushNotificationService.SendNotificationToFollowersAsync"/>:
/// el fan-out a seguidoras (persistencia de Notification + envío por FCM,
/// nunca por Web Push/PushSubscriptions).
/// </summary>
public class PushNotificationServiceFollowersTests
{
    [Fact]
    public async Task SendToFollowers_WithNoFollowers_DoesNothing()
    {
        using var ctx = NewContext();
        var business = NewBusiness();
        ctx.Businesses.Add(business);
        await ctx.SaveChangesAsync();

        var fcm = new FakeFcmService();
        await ServiceWith(ctx, fcm).SendNotificationToFollowersAsync(
            business.Id, "Título", "Mensaje");

        Assert.Empty(fcm.MulticastCalls);
        Assert.Equal(0, await ctx.Notifications.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task SendToFollowers_PersistsNotificationPerFollower_AndSendsFcm()
    {
        using var ctx = NewContext();
        var business = NewBusiness();
        ctx.Businesses.Add(business);
        var accountA = new Account { DisplayName = "Ana", Phone = "8680000001" };
        var accountB = new Account { DisplayName = "Beto", Phone = "8680000002" };
        ctx.Accounts.AddRange(accountA, accountB);
        await ctx.SaveChangesAsync();

        ctx.StoreFollowers.AddRange(
            new StoreFollower { BusinessId = business.Id, AccountId = accountA.Id },
            new StoreFollower { BusinessId = business.Id, AccountId = accountB.Id });
        ctx.BuyerDeviceTokens.AddRange(
            new BuyerDeviceToken { AccountId = accountA.Id, Token = "tok-a", Platform = "android" },
            new BuyerDeviceToken { AccountId = accountB.Id, Token = "tok-b", Platform = "ios" });
        await ctx.SaveChangesAsync();

        var fcm = new FakeFcmService();
        await ServiceWith(ctx, fcm).SendNotificationToFollowersAsync(
            business.Id, "Nueva novedad", "Mira lo nuevo", url: "/store/1", tag: "store-post");

        Assert.Equal(2, await ctx.Notifications.IgnoreQueryFilters().CountAsync());
        var notifAccountIds = await ctx.Notifications.IgnoreQueryFilters()
            .Select(n => n.AccountId).ToListAsync();
        Assert.Contains(accountA.Id, notifAccountIds);
        Assert.Contains(accountB.Id, notifAccountIds);

        Assert.Single(fcm.MulticastCalls);
        var call = fcm.MulticastCalls[0];
        Assert.Contains("tok-a", call.Tokens);
        Assert.Contains("tok-b", call.Tokens);
        Assert.Equal("store-post", call.Data!["type"]);
        Assert.Equal(business.Id.ToString(), call.Data["businessId"]);
    }

    [Fact]
    public async Task SendToFollowers_ExcludesUnfollowedAndOtherBusinesses()
    {
        using var ctx = NewContext();
        var bizA = NewBusiness();
        var bizB = NewBusiness();
        ctx.Businesses.AddRange(bizA, bizB);
        var unfollowed = new Account { DisplayName = "Salió", Phone = "8680000003" };
        var otherBiz = new Account { DisplayName = "OtraTienda", Phone = "8680000004" };
        ctx.Accounts.AddRange(unfollowed, otherBiz);
        await ctx.SaveChangesAsync();

        ctx.StoreFollowers.AddRange(
            new StoreFollower { BusinessId = bizA.Id, AccountId = unfollowed.Id, UnfollowedAt = DateTime.UtcNow },
            new StoreFollower { BusinessId = bizB.Id, AccountId = otherBiz.Id });
        await ctx.SaveChangesAsync();

        var fcm = new FakeFcmService();
        await ServiceWith(ctx, fcm).SendNotificationToFollowersAsync(bizA.Id, "T", "M");

        Assert.Empty(fcm.MulticastCalls);
        Assert.Equal(0, await ctx.Notifications.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task SendToFollowers_VipOnly_ExcludesNonVip()
    {
        using var ctx = NewContext();
        var business = NewBusiness();
        ctx.Businesses.Add(business);
        var vip = new Account { DisplayName = "Vip", Phone = "8680000005" };
        var normal = new Account { DisplayName = "Normal", Phone = "8680000006" };
        ctx.Accounts.AddRange(vip, normal);
        await ctx.SaveChangesAsync();

        ctx.StoreFollowers.AddRange(
            new StoreFollower { BusinessId = business.Id, AccountId = vip.Id, IsVip = true },
            new StoreFollower { BusinessId = business.Id, AccountId = normal.Id, IsVip = false });
        ctx.BuyerDeviceTokens.AddRange(
            new BuyerDeviceToken { AccountId = vip.Id, Token = "tok-vip" },
            new BuyerDeviceToken { AccountId = normal.Id, Token = "tok-normal" });
        await ctx.SaveChangesAsync();

        var fcm = new FakeFcmService();
        await ServiceWith(ctx, fcm).SendNotificationToFollowersAsync(
            business.Id, "Drop VIP", "Solo para ti", vipOnly: true);

        var call = Assert.Single(fcm.MulticastCalls);
        Assert.Contains("tok-vip", call.Tokens);
        Assert.DoesNotContain("tok-normal", call.Tokens);
    }

    [Fact]
    public async Task SendToFollowers_RequireNotifyOnLive_ExcludesFollowersWithFlagOff()
    {
        using var ctx = NewContext();
        var business = NewBusiness();
        ctx.Businesses.Add(business);
        var wantsLive = new Account { DisplayName = "Quiere", Phone = "8680000007" };
        var mutedLive = new Account { DisplayName = "Silenció", Phone = "8680000008" };
        ctx.Accounts.AddRange(wantsLive, mutedLive);
        await ctx.SaveChangesAsync();

        ctx.StoreFollowers.AddRange(
            new StoreFollower { BusinessId = business.Id, AccountId = wantsLive.Id, NotifyOnLive = true },
            new StoreFollower { BusinessId = business.Id, AccountId = mutedLive.Id, NotifyOnLive = false });
        ctx.BuyerDeviceTokens.AddRange(
            new BuyerDeviceToken { AccountId = wantsLive.Id, Token = "tok-wants" },
            new BuyerDeviceToken { AccountId = mutedLive.Id, Token = "tok-muted" });
        await ctx.SaveChangesAsync();

        var fcm = new FakeFcmService();
        await ServiceWith(ctx, fcm).SendNotificationToFollowersAsync(
            business.Id, "¡Voy en vivo!", "Entra ya", tag: "live-started", requireNotifyOnLive: true);

        var call = Assert.Single(fcm.MulticastCalls);
        Assert.Contains("tok-wants", call.Tokens);
        Assert.DoesNotContain("tok-muted", call.Tokens);
    }

    [Fact]
    public async Task SendToFollowers_SetsClientId_WhenFollowerHasClientInThisBusiness()
    {
        using var ctx = NewContext();
        var business = NewBusiness();
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Ana", Phone = "8680000009" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        ctx.StoreFollowers.Add(new StoreFollower { BusinessId = business.Id, AccountId = account.Id });
        var client = new Client
        {
            BusinessId = business.Id, AccountId = account.Id, Name = "Ana", NormalizedName = "ana",
        };
        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();

        await ServiceWith(ctx, new FakeFcmService()).SendNotificationToFollowersAsync(business.Id, "T", "M");

        var notif = await ctx.Notifications.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(client.Id, notif.ClientId);
        Assert.Equal(account.Id, notif.AccountId);
    }

    private static AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid():N}")
            .Options;
        return new AppDbContext(options);
    }

    private static IPushNotificationService ServiceWith(AppDbContext ctx, IFcmService fcm) =>
        new PushNotificationService(ctx, new ConfigurationBuilder().Build(),
            NullLogger<PushNotificationService>.Instance, fcm);

    private static Business NewBusiness() => new()
    {
        Name = "Regi Bazar",
        Slug = $"regibazar-{Guid.NewGuid():N}",
        City = "Nuevo Laredo",
        FrontendUrl = "https://example.com",
        BrandPrimaryColor = "#FF0072",
        DepotLat = 27.4861,
        DepotLng = -99.5069,
    };

    private class FakeFcmService : IFcmService
    {
        public List<(List<string> Tokens, string Title, string Body, Dictionary<string, string>? Data)> MulticastCalls { get; } = new();

        public Task SendToTokensAsync(IEnumerable<string> fcmTokens, string title, string body, Dictionary<string, string>? data = null)
        {
            MulticastCalls.Add((fcmTokens.ToList(), title, body, data));
            return Task.CompletedTask;
        }

        public Task SendToTokenAsync(string fcmToken, string title, string body, Dictionary<string, string>? data = null)
        {
            MulticastCalls.Add((new List<string> { fcmToken }, title, body, data));
            return Task.CompletedTask;
        }
    }
}
