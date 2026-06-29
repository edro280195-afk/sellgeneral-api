using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntregasApi.Tests;

public class BuyerOrdersServiceTests
{
    [Fact]
    public async Task GetOrders_ReturnsAllByDefault_OrderedByCreatedAtDesc()
    {
        using var ctx = TestDbContextFactory.Create();

        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Sofía", Phone = "8681452290" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var client = new Client
        {
            BusinessId = business.Id,
            AccountId = account.Id,
            Name = "Sofía",
            NormalizedName = "sofia",
            CurrentPoints = 50,
        };
        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();

        ctx.Orders.AddRange(
            NewOrder(business.Id, client.Id, OrderStatus.Delivered, 249m, "tok-old",
                new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc)),
            NewOrder(business.Id, client.Id, OrderStatus.InRoute, 590m, "tok-mid",
                new DateTime(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc)),
            NewOrder(business.Id, client.Id, OrderStatus.Pending, 120m, "tok-new",
                new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc)));
        await ctx.SaveChangesAsync();

        var response = await new BuyerOrdersService(ctx).GetOrdersAsync(account.Id);

        Assert.Equal(3, response.Total);
        Assert.Equal(1, response.Page);
        Assert.Equal(20, response.PageSize);
        Assert.Equal(BuyerOrderFilter.All, response.Filter);
        Assert.Null(response.BusinessId);
        Assert.Equal(3, response.Orders.Count);
        Assert.Equal("tok-new", response.Orders[0].AccessToken);
        Assert.Equal("tok-old", response.Orders[2].AccessToken);
        Assert.Equal("Regi Bazar", response.Orders[0].BusinessName);
        Assert.Equal("#FF0072", response.Orders[0].BrandPrimaryColor);
    }

    [Fact]
    public async Task GetOrders_FilterOpen_ReturnsOnlyOpenStatuses()
    {
        using var ctx = TestDbContextFactory.Create();

        var business = NewBusiness("Luna Bella", "luna", "#FF7A59");
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var client = new Client
        {
            BusinessId = business.Id,
            AccountId = account.Id,
            Name = "Ana",
            NormalizedName = "ana",
        };
        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();

        ctx.Orders.AddRange(
            NewOrder(business.Id, client.Id, OrderStatus.Pending, 50m, "t1", DateTime.UtcNow),
            NewOrder(business.Id, client.Id, OrderStatus.Confirmed, 60m, "t2", DateTime.UtcNow),
            NewOrder(business.Id, client.Id, OrderStatus.Shipped, 70m, "t3", DateTime.UtcNow),
            NewOrder(business.Id, client.Id, OrderStatus.InRoute, 80m, "t4", DateTime.UtcNow),
            NewOrder(business.Id, client.Id, OrderStatus.Delivered, 90m, "t5", DateTime.UtcNow),
            NewOrder(business.Id, client.Id, OrderStatus.Canceled, 100m, "t6", DateTime.UtcNow),
            NewOrder(business.Id, client.Id, OrderStatus.Postponed, 110m, "t7", DateTime.UtcNow));
        await ctx.SaveChangesAsync();

        var response = await new BuyerOrdersService(ctx)
            .GetOrdersAsync(account.Id, BuyerOrderFilter.Open);

        Assert.Equal(4, response.Total);
        Assert.All(response.Orders, o =>
            Assert.Contains(o.Status, new[] { "Pending", "Confirmed", "Shipped", "InRoute" }));
    }

    [Fact]
    public async Task GetOrders_FilterClosed_ReturnsOnlyClosedStatuses()
    {
        using var ctx = TestDbContextFactory.Create();

        var business = NewBusiness("Mia Joya", "mia", "#7B61FF");
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Mia", Phone = "8680000002" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var client = new Client
        {
            BusinessId = business.Id,
            AccountId = account.Id,
            Name = "Mia",
            NormalizedName = "mia",
        };
        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();

        ctx.Orders.AddRange(
            NewOrder(business.Id, client.Id, OrderStatus.Pending, 10m, "a", DateTime.UtcNow),
            NewOrder(business.Id, client.Id, OrderStatus.Delivered, 20m, "b", DateTime.UtcNow),
            NewOrder(business.Id, client.Id, OrderStatus.Canceled, 30m, "c", DateTime.UtcNow),
            NewOrder(business.Id, client.Id, OrderStatus.NotDelivered, 40m, "d", DateTime.UtcNow),
            NewOrder(business.Id, client.Id, OrderStatus.Postponed, 50m, "e", DateTime.UtcNow));
        await ctx.SaveChangesAsync();

        var response = await new BuyerOrdersService(ctx)
            .GetOrdersAsync(account.Id, BuyerOrderFilter.Closed);

        Assert.Equal(4, response.Total);
        Assert.All(response.Orders, o =>
            Assert.Contains(o.Status, new[] { "Delivered", "Canceled", "NotDelivered", "Postponed" }));
    }

    [Fact]
    public async Task GetOrders_PaginationReturnsRequestedPage()
    {
        using var ctx = TestDbContextFactory.Create();

        var business = NewBusiness("Aurora", "aurora", "#9C27B0");
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Lu", Phone = "8680000003" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var client = new Client
        {
            BusinessId = business.Id,
            AccountId = account.Id,
            Name = "Lu",
            NormalizedName = "lu",
        };
        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();

        for (var i = 0; i < 25; i++)
        {
            ctx.Orders.Add(NewOrder(
                business.Id, client.Id, OrderStatus.Delivered, 100m + i,
                $"tok-{i:00}", new DateTime(2026, 1, 1).AddDays(i)));
        }
        await ctx.SaveChangesAsync();

        var page1 = await new BuyerOrdersService(ctx)
            .GetOrdersAsync(account.Id, BuyerOrderFilter.All, page: 1, pageSize: 10);
        var page2 = await new BuyerOrdersService(ctx)
            .GetOrdersAsync(account.Id, BuyerOrderFilter.All, page: 2, pageSize: 10);
        var page3 = await new BuyerOrdersService(ctx)
            .GetOrdersAsync(account.Id, BuyerOrderFilter.All, page: 3, pageSize: 10);

        Assert.Equal(25, page1.Total);
        Assert.Equal(10, page1.Orders.Count);
        Assert.Equal(10, page2.Orders.Count);
        Assert.Equal(5, page3.Orders.Count);
        Assert.Equal("tok-24", page1.Orders[0].AccessToken);
        Assert.Equal("tok-14", page2.Orders[0].AccessToken);
        Assert.Equal("tok-04", page3.Orders[0].AccessToken);
    }

    [Fact]
    public async Task GetOrders_BusinessIdFilter_OnlyReturnsThatBusinessOrders()
    {
        using var ctx = TestDbContextFactory.Create();

        var bizA = NewBusiness("A", "a", "#111111");
        var bizB = NewBusiness("B", "b", "#222222");
        ctx.Businesses.AddRange(bizA, bizB);
        var account = new Account { DisplayName = "Multi", Phone = "8680000004" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var clientA = new Client
        {
            BusinessId = bizA.Id, AccountId = account.Id, Name = "Multi A",
            NormalizedName = "multi a",
        };
        var clientB = new Client
        {
            BusinessId = bizB.Id, AccountId = account.Id, Name = "Multi B",
            NormalizedName = "multi b",
        };
        ctx.Clients.AddRange(clientA, clientB);
        await ctx.SaveChangesAsync();

        ctx.Orders.AddRange(
            NewOrder(bizA.Id, clientA.Id, OrderStatus.Delivered, 100m, "a1", DateTime.UtcNow),
            NewOrder(bizA.Id, clientA.Id, OrderStatus.Pending, 110m, "a2", DateTime.UtcNow),
            NewOrder(bizB.Id, clientB.Id, OrderStatus.InRoute, 200m, "b1", DateTime.UtcNow));
        await ctx.SaveChangesAsync();

        var onlyA = await new BuyerOrdersService(ctx)
            .GetOrdersAsync(account.Id, businessId: bizA.Id);
        var onlyB = await new BuyerOrdersService(ctx)
            .GetOrdersAsync(account.Id, businessId: bizB.Id);

        Assert.Equal(2, onlyA.Total);
        Assert.All(onlyA.Orders, o => Assert.Equal("A", o.BusinessName));
        Assert.Single(onlyB.Orders);
        Assert.Equal("B", onlyB.Orders[0].BusinessName);
    }

    [Fact]
    public async Task GetOrders_DoesNotLeakOtherAccountsData()
    {
        using var ctx = TestDbContextFactory.Create();

        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var mine = new Account { DisplayName = "Mía", Phone = "8681452290" };
        var other = new Account { DisplayName = "Otra", Phone = "8682223344" };
        ctx.Accounts.AddRange(mine, other);
        await ctx.SaveChangesAsync();

        var otherClient = new Client
        {
            BusinessId = business.Id, AccountId = other.Id, Name = "Otra",
            NormalizedName = "otra", CurrentPoints = 999,
        };
        ctx.Clients.Add(otherClient);
        await ctx.SaveChangesAsync();
        ctx.Orders.Add(NewOrder(business.Id, otherClient.Id, OrderStatus.InRoute, 999m, "leak", DateTime.UtcNow));
        await ctx.SaveChangesAsync();

        var response = await new BuyerOrdersService(ctx).GetOrdersAsync(mine.Id);

        Assert.Equal(0, response.Total);
        Assert.Empty(response.Orders);
    }

    [Fact]
    public async Task GetOrders_WithNoClaimedClients_ReturnsEmptyResponse()
    {
        using var ctx = TestDbContextFactory.Create();
        var account = new Account { DisplayName = "Nueva", Phone = "8000000000" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var response = await new BuyerOrdersService(ctx).GetOrdersAsync(account.Id);

        Assert.Equal(0, response.Total);
        Assert.Empty(response.Orders);
        Assert.Equal(1, response.Page);
        Assert.Equal(20, response.PageSize);
    }

    [Fact]
    public async Task GetOrders_ClampsPageSize_AndDefaultsInvalidValues()
    {
        using var ctx = TestDbContextFactory.Create();
        var response = await new BuyerOrdersService(ctx)
            .GetOrdersAsync(accountId: 1, page: 0, pageSize: 9999);

        Assert.Equal(1, response.Page);
        Assert.Equal(50, response.PageSize);
    }

    [Fact]
    public async Task GetOrders_UnknownFilterFallsBackToAll()
    {
        using var ctx = TestDbContextFactory.Create();

        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Sofía", Phone = "8681452290" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var client = new Client
        {
            BusinessId = business.Id, AccountId = account.Id, Name = "Sofía",
            NormalizedName = "sofia",
        };
        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();
        ctx.Orders.AddRange(
            NewOrder(business.Id, client.Id, OrderStatus.Pending, 10m, "open", DateTime.UtcNow),
            NewOrder(business.Id, client.Id, OrderStatus.Delivered, 20m, "done", DateTime.UtcNow));
        await ctx.SaveChangesAsync();

        var response = await new BuyerOrdersService(ctx)
            .GetOrdersAsync(account.Id, filter: "garbage");

        Assert.Equal(BuyerOrderFilter.All, response.Filter);
        Assert.Equal(2, response.Total);
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
