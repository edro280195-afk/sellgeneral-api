using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntregasApi.Tests;

public class BuyerReserveServiceTests
{
    [Fact]
    public async Task Reserve_WithNoClaimedClient_ThrowsNotFound()
    {
        using var ctx = NewContext();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "X", Phone = "8000000000" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var product = NewProduct(business.Id, "Aire", 1000m, stock: 5);
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ReserveNotFoundException>(() =>
            ServiceWith(ctx).ReserveAsync(
                account.Id,
                new ReserveProductRequest(business.Id, product.Id),
                CancellationToken.None));

        Assert.Contains("no está en tu cuenta", ex.Message);
    }

    [Fact]
    public async Task Reserve_WithProductFromOtherBusiness_ThrowsNotFound()
    {
        using var ctx = NewContext();
        var bizA = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        var bizB = NewBusiness("Luna Bella", "luna", "#FF7A59");
        ctx.Businesses.AddRange(bizA, bizB);
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        // Ana solo tiene Client en bizA, pero pide un producto de bizB.
        ctx.Clients.Add(new Client
        {
            BusinessId = bizA.Id, AccountId = account.Id, Name = "Ana",
            NormalizedName = "ana",
        });
        var productB = NewProduct(bizB.Id, "Bolsa", 200m, stock: 5);
        ctx.Products.Add(productB);
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<ReserveNotFoundException>(() =>
            ServiceWith(ctx).ReserveAsync(
                account.Id,
                new ReserveProductRequest(bizA.Id, productB.Id),
                CancellationToken.None));
    }

    [Fact]
    public async Task Reserve_WithInactiveProduct_ThrowsBadRequest()
    {
        using var ctx = NewContext();
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
        var product = NewProduct(business.Id, "Aire", 1000m, stock: 5);
        product.IsActive = false;
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ReserveBadRequestException>(() =>
            ServiceWith(ctx).ReserveAsync(
                account.Id,
                new ReserveProductRequest(business.Id, product.Id),
                CancellationToken.None));

        Assert.Contains("ya no está disponible", ex.Message);
    }

    [Fact]
    public async Task Reserve_WithInsufficientStock_ThrowsBadRequest()
    {
        using var ctx = NewContext();
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
        var product = NewProduct(business.Id, "Sartén", 500m, stock: 2);
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ReserveBadRequestException>(() =>
            ServiceWith(ctx).ReserveAsync(
                account.Id,
                new ReserveProductRequest(business.Id, product.Id, 5),
                CancellationToken.None));

        Assert.Contains("Solo hay 2 disponibles", ex.Message);
    }

    [Fact]
    public async Task Reserve_WithZeroQuantity_ThrowsBadRequest()
    {
        using var ctx = NewContext();
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
        var product = NewProduct(business.Id, "Sartén", 500m, stock: 5);
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<ReserveBadRequestException>(() =>
            ServiceWith(ctx).ReserveAsync(
                account.Id,
                new ReserveProductRequest(business.Id, product.Id, 0),
                CancellationToken.None));
    }

    [Fact]
    public async Task Reserve_HappyPath_CreatesPendingPickUpOrderWithAccessToken()
    {
        using var ctx = NewContext();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var client = new Client
        {
            BusinessId = business.Id, AccountId = account.Id, Name = "Ana",
            NormalizedName = "ana", Type = "Frecuente",
        };
        ctx.Clients.Add(client);
        var product = NewProduct(business.Id, "Aire acondicionado", 1000m, stock: 5);
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();

        var order = await ServiceWith(ctx).ReserveAsync(
            account.Id,
            new ReserveProductRequest(business.Id, product.Id, 2),
            CancellationToken.None);

        Assert.Equal(business.Id, order.BusinessId);
        Assert.Equal("Regi Bazar", order.BusinessName);
        Assert.Equal("Pending", order.Status);
        Assert.Equal(1, order.ItemsCount);
        Assert.Equal(2000m, order.Total);
        Assert.NotNull(order.AccessToken);
        Assert.Equal(32, order.AccessToken!.Length); // Guid "N" de 32 chars

        // Re-leer el order de la DB para verificar los detalles internos.
        var dbOrder = await ctx.Orders.AsNoTracking().IgnoreQueryFilters()
            .Include(o => o.Items)
            .FirstAsync(o => o.Id == order.OrderId);
        Assert.Equal(OrderStatus.Pending, dbOrder.Status);
        Assert.Equal(OrderType.PickUp, dbOrder.OrderType);
        Assert.Equal(0m, dbOrder.ShippingCost);
        Assert.Equal(2000m, dbOrder.Subtotal);
        Assert.Equal(2000m, dbOrder.Total);
        Assert.Equal("apartado", dbOrder.Tags);
        Assert.Equal(client.Id, dbOrder.ClientId);
        Assert.Equal(1, dbOrder.Items.Count);
        var item = dbOrder.Items.First();
        Assert.Equal(2, item.Quantity);
        Assert.Equal(1000m, item.UnitPrice);
        Assert.Equal(2000m, item.LineTotal);
        Assert.Equal(product.Id, item.ProductId);
    }

    [Fact]
    public async Task Reserve_AssignsRequestedBusinessId_WithoutActiveTenant()
    {
        using var ctx = NewContext();
        var defaultBusiness = NewBusiness("Default", "default", "#6C4AE0");
        var requestedBusiness = NewBusiness("Luna Bella", "luna", "#FF7A59");
        ctx.Businesses.AddRange(defaultBusiness, requestedBusiness);
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var client = new Client
        {
            BusinessId = requestedBusiness.Id, AccountId = account.Id, Name = "Ana",
            NormalizedName = "ana", Type = "Frecuente",
        };
        ctx.Clients.Add(client);
        var product = NewProduct(requestedBusiness.Id, "Bolsa", 350m, stock: 5);
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();

        var order = await ServiceWith(ctx).ReserveAsync(
            account.Id,
            new ReserveProductRequest(requestedBusiness.Id, product.Id, 1),
            CancellationToken.None);

        var dbOrder = await ctx.Orders.AsNoTracking().IgnoreQueryFilters()
            .Include(o => o.Items)
            .FirstAsync(o => o.Id == order.OrderId);

        Assert.Equal(requestedBusiness.Id, order.BusinessId);
        Assert.Equal(requestedBusiness.Id, dbOrder.BusinessId);
        Assert.All(dbOrder.Items, item => Assert.Equal(requestedBusiness.Id, item.BusinessId));
    }

    [Fact]
    public async Task Reserve_DoesNotDecrementStock_AtReserveTime()
    {
        using var ctx = NewContext();
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
        var product = NewProduct(business.Id, "Sartén", 500m, stock: 10);
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();

        await ServiceWith(ctx).ReserveAsync(
            account.Id,
            new ReserveProductRequest(business.Id, product.Id, 3),
            CancellationToken.None);

        var dbProduct = await ctx.Products.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(p => p.Id == product.Id);
        Assert.Equal(10, dbProduct.Stock); // sin decremento
    }

    [Fact]
    public async Task Reserve_DoesNotLeakOtherAccountsData()
    {
        using var ctx = NewContext();
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
        var product = NewProduct(business.Id, "Sartén", 500m, stock: 5);
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<ReserveNotFoundException>(() =>
            ServiceWith(ctx).ReserveAsync(
                mine.Id,
                new ReserveProductRequest(business.Id, product.Id),
                CancellationToken.None));
    }

    // ── Helpers ──

    private static AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid():N}")
            .Options;
        return new AppDbContext(options);
    }

    private static IBuyerReserveService ServiceWith(AppDbContext ctx)
    {
        var orderSvc = new OrderService();
        var pushSvc = new NoopPushService();
        return new BuyerReserveService(ctx, orderSvc, pushSvc);
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

    private static Product NewProduct(int businessId, string name, decimal price, int stock) =>
        new()
        {
            BusinessId = businessId,
            SKU = $"sku-{Guid.NewGuid():N}".Substring(0, 16),
            Name = name,
            Price = price,
            Stock = stock,
            IsActive = true,
        };
}

/// <summary>
/// Implementación de OrderService mínima: solo para tests, sin lógica
/// real. Calcula las fechas con la misma regla simple que producción.
/// </summary>
internal class OrderService : IOrderService
{
    public (DateTime ExpiresAt, DateTime ScheduledDeliveryDate) CalculateOrderDates(
        string clientType, DateTime createdAt, DateTime? manualDate = null)
    {
        if (manualDate.HasValue)
        {
            return (manualDate.Value.AddDays(2), manualDate.Value);
        }
        // Próximo domingo.
        var daysUntilSunday = ((7 - (int)createdAt.DayOfWeek) % 7);
        if (daysUntilSunday == 0) daysUntilSunday = 7;
        var sunday = createdAt.Date.AddDays(daysUntilSunday);
        return (sunday.AddDays(2), sunday);
    }

    public DateTime CalculateExpiration(string clientType, DateTime createdAt)
        => CalculateOrderDates(clientType, createdAt).ExpiresAt;

    public Task SyncOrderExpirationsAsync(int clientId) => Task.CompletedTask;
}

/// <summary>
/// Stub de IPushNotificationService para tests: no hace nada, no falla.
/// Implementa toda la interfaz con `Task.CompletedTask`.
/// </summary>
internal class NoopPushService : IPushNotificationService
{
    public Task SendNotificationToClientAsync(int clientId, string title, string message, string? url = null, string? tag = null)
        => Task.CompletedTask;
    public Task SendNotificationToDriverAsync(string routeToken, string title, string message, string? url = null, string? tag = null)
        => Task.CompletedTask;
    public Task SendNotificationToAdminsAsync(string title, string message, string? url = null, string? tag = null)
        => Task.CompletedTask;
    public Task SendNotificationToFollowersAsync(int businessId, string title, string message, string? url = null, string? tag = null, bool vipOnly = false, bool requireNotifyOnPost = false, bool requireNotifyOnLive = false)
        => Task.CompletedTask;
    public Task NotifyClientDriverEnRouteAsync(int clientId, string? driverName = null) => Task.CompletedTask;
    public Task NotifyClientDriverNearbyAsync(int clientId, int distanceMeters) => Task.CompletedTask;
    public Task NotifyClientDeliveredAsync(int clientId) => Task.CompletedTask;
    public Task NotifyChatMessageAsync(string targetRole, int? clientId, string? routeToken, string senderName, string messageText)
        => Task.CompletedTask;
    public Task NotifyDriversNewRouteAsync(string routeName, string driverToken, int deliveryCount)
        => Task.CompletedTask;
    public Task NotifyDriverFcmAsync(string driverRouteToken, string title, string body, Dictionary<string, string>? data = null)
        => Task.CompletedTask;
    public Task BroadcastToAllDriversAsync(string title, string body, Dictionary<string, string>? data = null)
        => Task.CompletedTask;
}
