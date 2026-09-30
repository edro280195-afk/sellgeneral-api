using EntregasApi.Controllers;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EntregasApi.Tests;

/// <summary>
/// Cada evento del sistema avisa a SU destinatario: lo que pasa en la tienda le llega a
/// la dueña/administradoras de ESE negocio (nunca al "negocio activo por defecto" #1 ni a
/// la clienta), y lo que pasa con un pedido le llega a la clienta de ESE pedido.
///
/// Los escenarios usan un negocio de relleno (Id 1, el que el DbContext usaría "por
/// defecto") y el negocio real de la prueba (Id 2), para detectar cualquier aviso que
/// se mande al negocio equivocado.
/// </summary>
public class NotificationEmittersTests
{
    // ═══════════════════════════════════════════
    //  Apartado (clienta aparta → avisa a la vendedora)
    // ═══════════════════════════════════════════

    [Fact]
    public async Task Reserve_NotifiesTheOwnersOfThatBusiness_AndNobodyElse()
    {
        using var ctx = NewContext();
        var (_, business) = await SeedBusinessesAsync(ctx);
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();
        ctx.Clients.Add(new Client
        {
            BusinessId = business.Id, AccountId = account.Id, Name = "Ana", NormalizedName = "ana", Type = "Frecuente",
        });
        var product = new Product
        {
            BusinessId = business.Id, SKU = "sku-1", Name = "Bolsa de mano", Price = 350m, Stock = 5, IsActive = true,
        };
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();
        var push = new RecordingPushNotificationService();

        var order = await new BuyerReserveService(ctx, new OrderService(), push)
            .ReserveAsync(account.Id, new ReserveProductRequest(business.Id, product.Id), CancellationToken.None);

        var call = Assert.Single(push.Calls);
        Assert.Equal(RecordingPushNotificationService.Owners, call.Audience);
        Assert.Equal(business.Id, call.BusinessId);
        Assert.Equal("reserve", call.Tag);
        Assert.Equal($"/orders/detail/{order.OrderId}", call.Url);
        Assert.Contains("Ana", call.Message);
        Assert.Contains("Bolsa de mano", call.Message);
    }

    // ═══════════════════════════════════════════
    //  Pedido confirmado por la clienta (enlace público)
    // ═══════════════════════════════════════════

    [Fact]
    public async Task ConfirmOrder_NotifiesTheOwnersOfTheOrdersBusiness_WithClientNameAndOrderNumber()
    {
        using var ctx = NewContext(activeBusinessId: 2);
        var (_, business) = await SeedBusinessesAsync(ctx);
        var order = await SeedOrderAsync(ctx, business, "Juana López");
        var push = new RecordingPushNotificationService();

        var result = await ClientView(ctx, push, business.Id).ConfirmOrder(order.AccessToken);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(OrderStatus.Confirmed, (await ctx.Orders.AsNoTracking().SingleAsync()).Status);

        var call = Assert.Single(push.Calls);
        Assert.Equal(RecordingPushNotificationService.Owners, call.Audience);
        Assert.Equal(business.Id, call.BusinessId);
        Assert.Equal("order-confirmed", call.Tag);
        Assert.Equal($"/orders/detail/{order.Id}", call.Url);
        // Antes el nombre salía vacío (no se cargaba la clienta) y el número era el id interno.
        Assert.Contains("Juana López", call.Message);
        Assert.Contains($"#{order.OrderNumber}", call.Message);
    }

    [Fact]
    public async Task ConfirmOrder_StillConfirms_WhenTheSellerPushFails()
    {
        using var ctx = NewContext(activeBusinessId: 2);
        var (_, business) = await SeedBusinessesAsync(ctx);
        var order = await SeedOrderAsync(ctx, business, "Juana López");
        var push = new RecordingPushNotificationService { FailOnOwners = true };

        var result = await ClientView(ctx, push, business.Id).ConfirmOrder(order.AccessToken);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(OrderStatus.Confirmed, (await ctx.Orders.AsNoTracking().SingleAsync()).Status);
    }

    // ═══════════════════════════════════════════
    //  Chofer: entrega con cobro / entrega fallida
    // ═══════════════════════════════════════════

    [Fact]
    public async Task DriverDelivery_WithPayment_TellsTheSellerOnce_AndOnlyTheOrdersClienta()
    {
        using var ctx = NewContext(activeBusinessId: 2);
        var (_, business) = await SeedBusinessesAsync(ctx);
        var order = await SeedOrderAsync(ctx, business, "Juana López", total: 300m);
        var delivery = await SeedRouteWithDeliveryAsync(ctx, business, order);
        var push = new RecordingPushNotificationService();

        var result = await Driver(ctx, push, business.Id).MarkDelivered(
            "route-b", delivery.Id,
            new CompleteDeliveryRequest(null, "[{\"amount\":300,\"method\":\"Efectivo\"}]"),
            photos: null);

        Assert.IsType<OkObjectResult>(result);

        var seller = Assert.Single(push.To(RecordingPushNotificationService.Owners));
        Assert.Equal(business.Id, seller.BusinessId);
        Assert.Equal("payment-received", seller.Tag);
        Assert.Equal($"/orders/detail/{order.Id}", seller.Url);

        var clienta = Assert.Single(push.To(RecordingPushNotificationService.Client));
        Assert.Equal(order.ClientId, clienta.ClientId);
        Assert.Equal("delivered", clienta.Tag);

        Assert.Empty(push.To(RecordingPushNotificationService.Followers));
        Assert.Empty(push.To(RecordingPushNotificationService.Driver));
    }

    [Fact]
    public async Task DriverDelivery_WithoutPayment_DoesNotBotherTheSeller()
    {
        using var ctx = NewContext(activeBusinessId: 2);
        var (_, business) = await SeedBusinessesAsync(ctx);
        var order = await SeedOrderAsync(ctx, business, "Juana López");
        var delivery = await SeedRouteWithDeliveryAsync(ctx, business, order);
        var push = new RecordingPushNotificationService();

        await Driver(ctx, push, business.Id).MarkDelivered(
            "route-b", delivery.Id, new CompleteDeliveryRequest(null, null), photos: null);

        Assert.Empty(push.To(RecordingPushNotificationService.Owners));
        Assert.Single(push.To(RecordingPushNotificationService.Client));
    }

    [Fact]
    public async Task DriverFailedDelivery_TellsTheSellerOfTheRoutesBusiness_NotTheClienta()
    {
        using var ctx = NewContext(activeBusinessId: 2);
        var (_, business) = await SeedBusinessesAsync(ctx);
        var order = await SeedOrderAsync(ctx, business, "Juana López");
        var delivery = await SeedRouteWithDeliveryAsync(ctx, business, order);
        var push = new RecordingPushNotificationService();

        var result = await Driver(ctx, push, business.Id).MarkFailed(
            "route-b", delivery.Id, new FailDeliveryRequest("No estaba en casa", null), photos: null);

        Assert.IsType<OkObjectResult>(result);

        var call = Assert.Single(push.Calls);
        Assert.Equal(RecordingPushNotificationService.Owners, call.Audience);
        Assert.Equal(business.Id, call.BusinessId);
        Assert.Equal("delivery-failed", call.Tag);
        Assert.Equal($"/orders/detail/{order.Id}", call.Url);
        Assert.Contains("Juana López", call.Message);
        Assert.Contains("No estaba en casa", call.Message);
    }

    [Fact]
    public async Task DriverFailedDelivery_StillRegisters_WhenTheSellerPushFails()
    {
        using var ctx = NewContext(activeBusinessId: 2);
        var (_, business) = await SeedBusinessesAsync(ctx);
        var order = await SeedOrderAsync(ctx, business, "Juana López");
        var delivery = await SeedRouteWithDeliveryAsync(ctx, business, order);
        var push = new RecordingPushNotificationService { FailOnOwners = true };

        var result = await Driver(ctx, push, business.Id).MarkFailed(
            "route-b", delivery.Id, new FailDeliveryRequest("No estaba en casa", null), photos: null);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(DeliveryStatus.NotDelivered, (await ctx.Deliveries.AsNoTracking().SingleAsync()).Status);
    }

    // ═══════════════════════════════════════════
    //  "Va en camino": solo a la clienta a la que de verdad le toca
    // ═══════════════════════════════════════════

    [Fact]
    public async Task RouteStart_TellsOnlyTheFirstClienta_ThatSheIsNext()
    {
        using var ctx = NewContext(activeBusinessId: 2);
        var (_, business) = await SeedBusinessesAsync(ctx);
        var first = await SeedOrderAsync(ctx, business, "Primera");
        var second = await SeedOrderAsync(ctx, business, "Segunda");
        await SeedRouteWithDeliveriesAsync(ctx, business, RouteStatus.Pending, first, second);
        var push = new RecordingPushNotificationService();

        var result = await Driver(ctx, push, business.Id).StartRoute("route-b");

        Assert.IsType<OkObjectResult>(result);
        // Las dos están en la ruta, pero solo a la primera le toca: la segunda espera su turno.
        var call = Assert.Single(push.Calls);
        Assert.Equal(RecordingPushNotificationService.Client, call.Audience);
        Assert.Equal(first.ClientId, call.ClientId);
        Assert.Equal("driver-en-route", call.Tag);
    }

    [Fact]
    public async Task DeliveringOne_TellsTheNextClientaItsHerTurn_AndOnlyHer()
    {
        using var ctx = NewContext(activeBusinessId: 2);
        var (_, business) = await SeedBusinessesAsync(ctx);
        var first = await SeedOrderAsync(ctx, business, "Primera");
        var second = await SeedOrderAsync(ctx, business, "Segunda");
        var third = await SeedOrderAsync(ctx, business, "Tercera");
        var deliveries = await SeedRouteWithDeliveriesAsync(
            ctx, business, RouteStatus.Active, first, second, third);
        deliveries[0].Status = DeliveryStatus.InTransit;
        await ctx.SaveChangesAsync();
        var push = new RecordingPushNotificationService();

        await Driver(ctx, push, business.Id).MarkDelivered(
            "route-b", deliveries[0].Id, new CompleteDeliveryRequest(null, null), photos: null);

        var calls = push.To(RecordingPushNotificationService.Client).ToList();
        Assert.Equal(2, calls.Count);
        Assert.Contains(calls, c => c.ClientId == first.ClientId && c.Tag == "delivered");
        Assert.Contains(calls, c => c.ClientId == second.ClientId && c.Tag == "driver-en-route");
        // La tercera sigue esperando: no recibe nada todavía.
        Assert.DoesNotContain(calls, c => c.ClientId == third.ClientId);
    }

    [Fact]
    public async Task MarkInTransit_DoesNotRepeatTheAlert_WhenItWasAlreadyTheCurrentStop()
    {
        using var ctx = NewContext(activeBusinessId: 2);
        var (_, business) = await SeedBusinessesAsync(ctx);
        var first = await SeedOrderAsync(ctx, business, "Primera");
        var second = await SeedOrderAsync(ctx, business, "Segunda");
        var deliveries = await SeedRouteWithDeliveriesAsync(
            ctx, business, RouteStatus.Active, first, second);
        deliveries[0].Status = DeliveryStatus.InTransit;
        await ctx.SaveChangesAsync();
        var push = new RecordingPushNotificationService();
        var driver = Driver(ctx, push, business.Id);

        // Ya era la parada en curso: su clienta ya fue avisada, no se repite.
        await driver.MarkInTransit("route-b", deliveries[0].Id);
        Assert.Empty(push.Calls);

        // Pasa a la siguiente parada: ahora sí le toca a la segunda, y solo a ella.
        await driver.MarkInTransit("route-b", deliveries[1].Id);
        var call = Assert.Single(push.Calls);
        Assert.Equal(second.ClientId, call.ClientId);
    }

    // ═══════════════════════════════════════════
    //  Escenario
    // ═══════════════════════════════════════════

    private static AppDbContext NewContext(int? activeBusinessId = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid():N}")
            .Options;

        return activeBusinessId is null
            ? new AppDbContext(options)
            : new AppDbContext(options, new StubTenant(activeBusinessId.Value), new EphemeralDataProtectionProvider());
    }

    /// <summary>Negocio de relleno (Id 1, el "por defecto") y el negocio real de la prueba (Id 2).</summary>
    private static async Task<(Business Decoy, Business Real)> SeedBusinessesAsync(AppDbContext ctx)
    {
        var decoy = NewBusiness("Regi Bazar");
        var real = NewBusiness("Luna Bella");
        ctx.Businesses.AddRange(decoy, real);
        await ctx.SaveChangesAsync();
        Assert.Equal(1, decoy.Id);
        Assert.Equal(2, real.Id);
        return (decoy, real);
    }

    private static async Task<Order> SeedOrderAsync(
        AppDbContext ctx, Business business, string clientName, decimal total = 100m)
    {
        var client = new Client
        {
            BusinessId = business.Id, Name = clientName, NormalizedName = clientName.ToLowerInvariant(),
        };
        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();

        var order = new Order
        {
            BusinessId = business.Id,
            ClientId = client.Id,
            AccessToken = Guid.NewGuid().ToString("N"),
            Status = OrderStatus.Pending,
            Subtotal = total,
            Total = total,
            ExpiresAt = DateTime.UtcNow.AddDays(2),
        };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();
        return order;
    }

    private static async Task<Delivery> SeedRouteWithDeliveryAsync(AppDbContext ctx, Business business, Order order)
    {
        var route = new DeliveryRoute
        {
            BusinessId = business.Id, Name = "Ruta B", DriverToken = "route-b", Status = RouteStatus.Active,
        };
        ctx.DeliveryRoutes.Add(route);
        await ctx.SaveChangesAsync();

        var delivery = new Delivery
        {
            BusinessId = business.Id,
            OrderId = order.Id,
            Kind = DeliveryKind.Order,
            DeliveryRouteId = route.Id,
            SortOrder = 1,
            Status = DeliveryStatus.InTransit,
        };
        ctx.Deliveries.Add(delivery);
        order.DeliveryRouteId = route.Id;
        await ctx.SaveChangesAsync();
        return delivery;
    }

    private static async Task<List<Delivery>> SeedRouteWithDeliveriesAsync(
        AppDbContext ctx, Business business, RouteStatus status, params Order[] orders)
    {
        var route = new DeliveryRoute
        {
            BusinessId = business.Id, Name = "Ruta B", DriverToken = "route-b", Status = status,
        };
        ctx.DeliveryRoutes.Add(route);
        await ctx.SaveChangesAsync();

        var deliveries = new List<Delivery>();
        for (var i = 0; i < orders.Length; i++)
        {
            var delivery = new Delivery
            {
                BusinessId = business.Id,
                OrderId = orders[i].Id,
                Kind = DeliveryKind.Order,
                DeliveryRouteId = route.Id,
                SortOrder = i + 1,
                Status = DeliveryStatus.Pending,
            };
            orders[i].DeliveryRouteId = route.Id;
            deliveries.Add(delivery);
        }
        ctx.Deliveries.AddRange(deliveries);
        await ctx.SaveChangesAsync();
        return deliveries;
    }

    private static ClientViewController ClientView(AppDbContext ctx, IPushNotificationService push, int businessId) =>
        new(ctx,
            new FakeHubContext(),
            push,
            null!,
            new StubTenant(businessId),
            new ConfigurationBuilder().Build(),
            NullLogger<ClientViewController>.Instance,
            new StubCurrentAccount(null));

    private static DriverController Driver(AppDbContext ctx, IPushNotificationService push, int businessId) =>
        new(ctx,
            new FakeHubContext(),
            push,
            null!,
            null!,
            new StubTenant(businessId),
            NullLogger<DriverController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private static Business NewBusiness(string name) => new()
    {
        Name = name,
        Slug = $"tienda-{Guid.NewGuid():N}",
        City = "Nuevo Laredo",
        FrontendUrl = "https://example.com",
        BrandPrimaryColor = "#FF0072",
        DepotLat = 27.4861,
        DepotLng = -99.5069,
        IsActive = true,
        PlanTier = "Elite",
        SubscriptionStatus = SubscriptionStatus.Active,
    };
}
