using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntregasApi.Tests;

public class BuyerFeedServiceTests
{
    [Fact]
    public async Task GetHome_AggregatesClaimedStoresPointsAndActiveOrder()
    {
        using var ctx = TestDbContextFactory.Create();

        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Sofía", Phone = "8681452290" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        ctx.Clients.Add(new Client
        {
            BusinessId = business.Id,
            AccountId = account.Id,
            Name = "Sofía",
            CurrentPoints = 320,
            NormalizedName = "sofia",
        });
        await ctx.SaveChangesAsync();
        var clientId = (await ctx.Clients.SingleAsync()).Id;

        ctx.Orders.AddRange(
            NewOrder(business.Id, clientId, OrderStatus.InRoute, 590m, "tok-active",
                new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc)),
            NewOrder(business.Id, clientId, OrderStatus.Delivered, 249m, "tok-done",
                new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc)));
        await ctx.SaveChangesAsync();

        var home = await new BuyerFeedService(ctx).GetHomeAsync(account.Id);

        Assert.Equal("Sofía", home.DisplayName);
        Assert.Equal(320, home.TotalPoints);
        Assert.Single(home.Stores);
        Assert.Equal("Regi Bazar", home.Stores[0].Name);
        Assert.Equal("#FF0072", home.Stores[0].BrandPrimaryColor);
        Assert.Equal(320, home.Stores[0].Points);
        Assert.NotNull(home.ActiveOrder);
        Assert.Equal("InRoute", home.ActiveOrder!.Status);
        Assert.Equal("tok-active", home.ActiveOrder.AccessToken);
        Assert.Equal(2, home.RecentOrders.Count);
    }

    [Fact]
    public async Task GetHome_WithNoClaimedClients_ReturnsEmptyFeed()
    {
        using var ctx = TestDbContextFactory.Create();
        var account = new Account { DisplayName = "Nueva", Phone = "8000000000" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var home = await new BuyerFeedService(ctx).GetHomeAsync(account.Id);

        Assert.Equal(0, home.TotalPoints);
        Assert.Null(home.ActiveOrder);
        Assert.Empty(home.Stores);
        Assert.Empty(home.RecentOrders);
    }

    [Fact]
    public async Task GetHome_MarksClaimedStoreAsLive_WhenItHasActiveLiveAnnouncement()
    {
        using var ctx = TestDbContextFactory.Create();

        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Sofia", Phone = "8681452290" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        ctx.Clients.Add(new Client
        {
            BusinessId = business.Id,
            AccountId = account.Id,
            Name = "Sofia",
            NormalizedName = "sofia",
        });
        ctx.LiveAnnouncements.Add(new LiveAnnouncement
        {
            BusinessId = business.Id,
            Title = "Rebajas",
            StartedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var home = await new BuyerFeedService(ctx).GetHomeAsync(account.Id);

        var store = Assert.Single(home.Stores);
        Assert.True(store.IsLive);
        Assert.Equal(1, home.LiveCount);
    }

    [Fact]
    public async Task GetHome_DoesNotLeakOtherAccountsData()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Luna Bella", "luna", "#FF7A59");
        ctx.Businesses.Add(business);
        var mine = new Account { DisplayName = "Sofía", Phone = "8681452290" };
        var other = new Account { DisplayName = "Otra", Phone = "8682223344" };
        ctx.Accounts.AddRange(mine, other);
        await ctx.SaveChangesAsync();

        ctx.Clients.Add(new Client
        {
            BusinessId = business.Id,
            AccountId = other.Id,
            Name = "Otra",
            CurrentPoints = 999,
            NormalizedName = "otra",
        });
        await ctx.SaveChangesAsync();

        var home = await new BuyerFeedService(ctx).GetHomeAsync(mine.Id);

        Assert.Equal(0, home.TotalPoints);
        Assert.Empty(home.Stores);
        Assert.Empty(home.RecentOrders);
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
        GeocodingRegion = "Test, MX",
        GeminiBusinessName = name,
        PlanTier = "Elite",
        SubscriptionStatus = SubscriptionStatus.Active,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
    };

    private static Order NewOrder(
        int businessId, int clientId, OrderStatus status, decimal total, string token, DateTime createdAt) =>
        new()
        {
            BusinessId = businessId,
            ClientId = clientId,
            Status = status,
            Total = total,
            AccessToken = token,
            CreatedAt = createdAt,
            ExpiresAt = createdAt.AddDays(3),
        };
}
