using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntregasApi.Tests;

public class BuyerPaymentServiceTests
{
    [Fact]
    public async Task GetMyPayments_WithNoClaimedClients_ReturnsEmpty()
    {
        using var ctx = TestDbContextFactory.Create();
        var account = new Account { DisplayName = "X", Phone = "8000000000" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var result = await new BuyerPaymentService(ctx).GetMyPaymentsAsync(account.Id);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetMyPayments_WithClaimedClientNoPayments_ReturnsEmpty()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        ctx.Clients.Add(new Client
        {
            BusinessId = business.Id, AccountId = account.Id, Name = "Ana",
            NormalizedName = "ana",
        });
        await ctx.SaveChangesAsync();

        var result = await new BuyerPaymentService(ctx).GetMyPaymentsAsync(account.Id);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetMyPayments_HappyPath_ReturnsPaymentsAcrossBusinesses()
    {
        using var ctx = TestDbContextFactory.Create();
        var bizA = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        var bizB = NewBusiness("Luna Bella", "luna", "#FF7A59");
        ctx.Businesses.AddRange(bizA, bizB);
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var clientA = new Client
        {
            BusinessId = bizA.Id, AccountId = account.Id, Name = "Ana",
            NormalizedName = "ana",
        };
        var clientB = new Client
        {
            BusinessId = bizB.Id, AccountId = account.Id, Name = "Ana B",
            NormalizedName = "ana b",
        };
        ctx.Clients.AddRange(clientA, clientB);
        await ctx.SaveChangesAsync();

        var orderA1 = NewOrder(bizA.Id, clientA.Id, OrderStatus.Delivered, 500m);
        var orderA2 = NewOrder(bizA.Id, clientA.Id, OrderStatus.Delivered, 200m);
        var orderB1 = NewOrder(bizB.Id, clientB.Id, OrderStatus.Delivered, 300m);
        ctx.Orders.AddRange(orderA1, orderA2, orderB1);
        await ctx.SaveChangesAsync();

        ctx.OrderPayments.AddRange(
            NewPayment(bizA.Id, orderA1.Id, 500m, "Efectivo",
                new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc)),
            NewPayment(bizA.Id, orderA2.Id, 200m, "Tarjeta",
                new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc)),
            NewPayment(bizB.Id, orderB1.Id, 300m, "Efectivo",
                new DateTime(2026, 6, 20, 14, 0, 0, DateTimeKind.Utc)));
        await ctx.SaveChangesAsync();

        var result = await new BuyerPaymentService(ctx).GetMyPaymentsAsync(account.Id);

        Assert.Equal(3, result.Count);
        // Ordenado por fecha desc.
        Assert.Equal(orderB1.Id, result[0].OrderId);
        Assert.Equal("Luna Bella", result[0].BusinessName);
        Assert.Equal("#FF7A59", result[0].BrandPrimaryColor);
        Assert.Equal(300m, result[0].Amount);

        Assert.Equal(orderA2.Id, result[1].OrderId);
        Assert.Equal(200m, result[1].Amount);
        Assert.Equal("Tarjeta", result[1].Method);

        Assert.Equal(orderA1.Id, result[2].OrderId);
        Assert.Equal(500m, result[2].Amount);
        Assert.Equal("Delivered", result[2].OrderStatus);
    }

    [Fact]
    public async Task GetMyPayments_DoesNotLeakOtherAccountsData()
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
            NormalizedName = "otra",
        };
        ctx.Clients.Add(otherClient);
        var otherOrder = NewOrder(business.Id, otherClient.Id, OrderStatus.Delivered, 100m);
        ctx.Orders.Add(otherOrder);
        await ctx.SaveChangesAsync();
        ctx.OrderPayments.Add(NewPayment(business.Id, otherOrder.Id, 100m, "Efectivo"));
        await ctx.SaveChangesAsync();

        var result = await new BuyerPaymentService(ctx).GetMyPaymentsAsync(mine.Id);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetMyPayments_OnlyPaymentsForMyClientOrders_NotAllBusinessPayments()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var mine = new Account { DisplayName = "Mía", Phone = "8681452290" };
        var other = new Account { DisplayName = "Otra", Phone = "8682223344" };
        ctx.Accounts.AddRange(mine, other);
        await ctx.SaveChangesAsync();

        var myClient = new Client
        {
            BusinessId = business.Id, AccountId = mine.Id, Name = "Mía",
            NormalizedName = "mia",
        };
        var otherClient = new Client
        {
            BusinessId = business.Id, AccountId = other.Id, Name = "Otra",
            NormalizedName = "otra",
        };
        ctx.Clients.AddRange(myClient, otherClient);

        var myOrder = NewOrder(business.Id, myClient.Id, OrderStatus.Delivered, 100m);
        var otherOrder = NewOrder(business.Id, otherClient.Id, OrderStatus.Delivered, 999m);
        ctx.Orders.AddRange(myOrder, otherOrder);
        await ctx.SaveChangesAsync();

        ctx.OrderPayments.AddRange(
            NewPayment(business.Id, myOrder.Id, 100m, "Efectivo"),
            NewPayment(business.Id, otherOrder.Id, 999m, "Efectivo"));
        await ctx.SaveChangesAsync();

        var result = await new BuyerPaymentService(ctx).GetMyPaymentsAsync(mine.Id);

        Assert.Single(result);
        Assert.Equal(myOrder.Id, result[0].OrderId);
        Assert.Equal(100m, result[0].Amount);
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

    private static Order NewOrder(int businessId, int clientId, OrderStatus status, decimal total) =>
        new()
        {
            BusinessId = businessId,
            ClientId = clientId,
            Status = status,
            Total = total,
            Subtotal = total,
            AccessToken = $"tok-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(3),
        };

    private static OrderPayment NewPayment(
        int businessId, int orderId, decimal amount, string method = "Efectivo",
        DateTime? date = null) =>
        new()
        {
            BusinessId = businessId,
            OrderId = orderId,
            Amount = amount,
            Method = method,
            Date = date ?? DateTime.UtcNow,
            RegisteredBy = "Admin",
        };
}
