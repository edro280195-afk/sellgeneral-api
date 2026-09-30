using System.Reflection;
using EntregasApi.Controllers;
using EntregasApi.Data;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EntregasApi.Tests;

/// <summary>
/// La suscripción a push es anónima (la clienta y el chofer entran por enlace), así
/// que nada de lo que diga el cuerpo se toma por bueno: el rol exige su prueba y el
/// negocio/clienta salen del recurso validado. Sin esto, cualquiera podía registrar
/// su navegador como "administración" del negocio #1 y leer los avisos de la dueña,
/// o suscribirse a los de otra clienta diciendo su ClientId.
/// </summary>
public class PushControllerSecurityTests
{
    // ═══════════════════════════════════════════
    //  ADMIN: solo dueña/administradora del negocio
    // ═══════════════════════════════════════════

    [Fact]
    public async Task Subscribe_Admin_WithoutSession_IsRejected_AndStoresNothing()
    {
        using var w = await World.SeedAsync();

        var result = await Controller(w.Ctx, accountId: null).Subscribe(Req("admin"), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Empty(await w.Ctx.PushSubscriptions.IgnoreQueryFilters().ToListAsync());
    }

    [Theory]
    [InlineData("buyer")]
    [InlineData("driver")]
    public async Task Subscribe_Admin_ForAccountsThatAreNotOwnerOrAdmin_IsForbidden(string who)
    {
        using var w = await World.SeedAsync();
        var accountId = who == "buyer" ? w.BuyerOnly.Id : w.DriverOnly.Id;

        var result = await Controller(w.Ctx, accountId).Subscribe(Req("admin"), CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Empty(await w.Ctx.PushSubscriptions.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Subscribe_Admin_AsOwner_IsStoredForTheirOwnBusiness_NotTheDefaultOne()
    {
        using var w = await World.SeedAsync();

        // La dueña de B (Id 2): la suscripción queda en B, no en el negocio #1 por defecto.
        var result = await Controller(w.Ctx, w.OwnerB.Id).Subscribe(Req("admin"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var sub = Assert.Single(await w.Ctx.PushSubscriptions.IgnoreQueryFilters().ToListAsync());
        Assert.Equal("admin", sub.Role);
        Assert.Equal(w.B.Id, sub.BusinessId);
        Assert.Null(sub.ClientId);
    }

    [Fact]
    public async Task Subscribe_Admin_WithSeveralBusinesses_NeedsAHeaderThatIsTheirs()
    {
        using var w = await World.SeedAsync();

        // Varias tiendas y sin X-Business-Id: ambiguo.
        var noHeader = await Controller(w.Ctx, w.Multi.Id).Subscribe(Req("admin"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(noHeader);

        // Un negocio del que no es dueña ni administradora (el 999 no existe).
        var foreign = await Controller(w.Ctx, w.Multi.Id, businessHeader: "999").Subscribe(Req("admin"), CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(foreign).StatusCode);

        var ok = await Controller(w.Ctx, w.Multi.Id, businessHeader: w.B.Id.ToString()).Subscribe(Req("admin"), CancellationToken.None);
        Assert.IsType<OkObjectResult>(ok);
        Assert.Equal(w.B.Id, Assert.Single(await w.Ctx.PushSubscriptions.IgnoreQueryFilters().ToListAsync()).BusinessId);
    }

    // ═══════════════════════════════════════════
    //  CLIENTA: sale del token del pedido, no del cuerpo
    // ═══════════════════════════════════════════

    [Fact]
    public async Task Subscribe_Client_WithoutOrderToken_IsRejected()
    {
        using var w = await World.SeedAsync();

        // Lo que mandaban las versiones anteriores del panel: solo un ClientId.
        var result = await Controller(w.Ctx).Subscribe(Req("client", clientId: w.VictimClient.Id), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await w.Ctx.PushSubscriptions.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Subscribe_Client_WithUnknownOrderToken_IsRejected()
    {
        using var w = await World.SeedAsync();

        var result = await Controller(w.Ctx).Subscribe(Req("client", orderToken: "no-existe"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await w.Ctx.PushSubscriptions.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Subscribe_Client_TakesClientAndBusinessFromTheOrder_NotFromTheBody()
    {
        using var w = await World.SeedAsync();

        // La atacante tiene el enlace de SU pedido pero manda el ClientId de la víctima.
        var result = await Controller(w.Ctx).Subscribe(
            Req("client", orderToken: w.AttackerOrder.AccessToken, clientId: w.VictimClient.Id),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var sub = Assert.Single(await w.Ctx.PushSubscriptions.IgnoreQueryFilters().ToListAsync());
        Assert.Equal("client", sub.Role);
        Assert.Equal(w.AttackerClient.Id, sub.ClientId);
        Assert.NotEqual(w.VictimClient.Id, sub.ClientId);
        Assert.Equal(w.B.Id, sub.BusinessId);
    }

    [Fact]
    public async Task Subscribe_Client_DefaultsToClientRole_WhenRoleIsOmitted()
    {
        using var w = await World.SeedAsync();

        var result = await Controller(w.Ctx).Subscribe(
            Req(role: null, orderToken: w.AttackerOrder.AccessToken), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("client", Assert.Single(await w.Ctx.PushSubscriptions.IgnoreQueryFilters().ToListAsync()).Role);
    }

    // ═══════════════════════════════════════════
    //  CHOFER: sale del token de una ruta que existe
    // ═══════════════════════════════════════════

    [Fact]
    public async Task Subscribe_Driver_WithUnknownRoute_IsRejected()
    {
        using var w = await World.SeedAsync();

        var none = await Controller(w.Ctx).Subscribe(Req("driver"), CancellationToken.None);
        var unknown = await Controller(w.Ctx).Subscribe(Req("driver", routeToken: "ruta-inventada"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(none);
        Assert.IsType<BadRequestObjectResult>(unknown);
        Assert.Empty(await w.Ctx.PushSubscriptions.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Subscribe_Driver_StampsTheBusinessOfTheRoute()
    {
        using var w = await World.SeedAsync();

        var result = await Controller(w.Ctx).Subscribe(Req("driver", routeToken: "route-b"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var sub = Assert.Single(await w.Ctx.PushSubscriptions.IgnoreQueryFilters().ToListAsync());
        Assert.Equal("driver", sub.Role);
        Assert.Equal("route-b", sub.DriverRouteToken);
        Assert.Equal(w.B.Id, sub.BusinessId);
    }

    // ═══════════════════════════════════════════
    //  Reutilizar un navegador
    // ═══════════════════════════════════════════

    [Fact]
    public async Task Subscribe_AnAdminBrowser_CannotBeTurnedIntoAClientByAnAnonymousCall()
    {
        using var w = await World.SeedAsync();
        const string endpoint = "https://push.example/dueña-celular";
        await Controller(w.Ctx, w.OwnerB.Id).Subscribe(Req("admin", endpoint), CancellationToken.None);

        // La dueña abre el enlace de un pedido en el mismo navegador.
        var result = await Controller(w.Ctx).Subscribe(
            Req("client", endpoint, orderToken: w.AttackerOrder.AccessToken), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.False((bool)ok.Value!.GetType().GetProperty("success")!.GetValue(ok.Value)!);
        var sub = Assert.Single(await w.Ctx.PushSubscriptions.IgnoreQueryFilters().ToListAsync());
        Assert.Equal("admin", sub.Role);
        Assert.Null(sub.ClientId);
    }

    [Fact]
    public async Task Subscribe_SameEndpoint_IsReDerivedFromTheNewOrder_NotAccumulated()
    {
        using var w = await World.SeedAsync();
        const string endpoint = "https://push.example/celular-compartido";
        await Controller(w.Ctx).Subscribe(Req("client", endpoint, orderToken: w.AttackerOrder.AccessToken), CancellationToken.None);

        await Controller(w.Ctx).Subscribe(Req("client", endpoint, orderToken: w.VictimOrder.AccessToken), CancellationToken.None);

        var sub = Assert.Single(await w.Ctx.PushSubscriptions.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(w.VictimClient.Id, sub.ClientId);
    }

    // ═══════════════════════════════════════════
    //  FCM de choferes
    // ═══════════════════════════════════════════

    [Fact]
    public async Task SubscribeFcm_Admin_WithoutSession_IsRejected()
    {
        using var w = await World.SeedAsync();

        var result = await Controller(w.Ctx).SubscribeFcm(
            new FcmSubscribeRequest { FcmToken = "fcm-x", Role = "admin" }, CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Empty(await w.Ctx.FcmTokens.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task SubscribeFcm_Driver_WithRealRoute_StampsBusinessAndRoute()
    {
        using var w = await World.SeedAsync();

        var result = await Controller(w.Ctx).SubscribeFcm(
            new FcmSubscribeRequest { FcmToken = "fcm-driver", Role = "driver", DriverRouteToken = "route-b" },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var token = Assert.Single(await w.Ctx.FcmTokens.IgnoreQueryFilters().ToListAsync());
        Assert.Equal("driver", token.Role);
        Assert.Equal("route-b", token.DriverRouteToken);
        Assert.Equal(w.B.Id, token.BusinessId);
    }

    [Fact]
    public async Task SubscribeFcm_Driver_WithMadeUpRoute_IsStoredWithoutRoute()
    {
        using var w = await World.SeedAsync();

        var result = await Controller(w.Ctx).SubscribeFcm(
            new FcmSubscribeRequest { FcmToken = "fcm-driver", Role = "driver", DriverRouteToken = "inventada" },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var token = Assert.Single(await w.Ctx.FcmTokens.IgnoreQueryFilters().ToListAsync());
        Assert.Null(token.DriverRouteToken);
    }

    [Fact]
    public async Task SubscribeFcm_ExistingToken_IsFoundAcrossBusinesses_NotDuplicated()
    {
        using var w = await World.SeedAsync();
        await Controller(w.Ctx).SubscribeFcm(
            new FcmSubscribeRequest { FcmToken = "fcm-driver", Role = "driver", DriverRouteToken = "route-b" }, CancellationToken.None);

        // Mismo dispositivo, ahora con la ruta de A: se actualiza la misma fila (el token es único).
        await Controller(w.Ctx).SubscribeFcm(
            new FcmSubscribeRequest { FcmToken = "fcm-driver", Role = "driver", DriverRouteToken = "route-a" }, CancellationToken.None);

        var token = Assert.Single(await w.Ctx.FcmTokens.IgnoreQueryFilters().ToListAsync());
        Assert.Equal("route-a", token.DriverRouteToken);
        Assert.Equal(w.A.Id, token.BusinessId);
    }

    // ═══════════════════════════════════════════
    //  Quién puede llamar a cada endpoint
    // ═══════════════════════════════════════════

    [Fact]
    public void TestNotification_IsAdminOnly()
    {
        var method = typeof(PushController).GetMethod(nameof(PushController.TestNotification))!;

        var authorize = Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(AuthorizationPolicies.Admin, authorize.Policy);
        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>());
    }

    [Theory]
    [InlineData(nameof(PushController.SendToClient))]
    [InlineData(nameof(PushController.SendToDriver))]
    [InlineData(nameof(PushController.SendToAdmins))]
    public void TargetedSends_AreAdminOnly(string action)
    {
        var method = typeof(PushController).GetMethod(action)!;

        var authorize = Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(AuthorizationPolicies.Admin, authorize.Policy);
    }

    [Fact]
    public void DeviceUnregister_IsAnonymous_SoLogoutAlwaysWorks()
    {
        var method = typeof(BuyerDeviceController).GetMethod(nameof(BuyerDeviceController.Unregister))!;

        Assert.NotEmpty(method.GetCustomAttributes<AllowAnonymousAttribute>());
    }

    [Fact]
    public void DeviceRegisterAndTestPush_StillRequireASession()
    {
        foreach (var name in new[] { nameof(BuyerDeviceController.Register), nameof(BuyerDeviceController.TestPush) })
        {
            var method = typeof(BuyerDeviceController).GetMethod(name)!;
            Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>());
        }
        Assert.NotNull(typeof(BuyerDeviceController).GetCustomAttribute<AuthorizeAttribute>());
    }

    // ═══════════════════════════════════════════
    //  Escenario
    // ═══════════════════════════════════════════

    private static PushController Controller(AppDbContext ctx, int? accountId = null, string? businessHeader = null)
    {
        var http = new DefaultHttpContext();
        if (businessHeader is not null) http.Request.Headers["X-Business-Id"] = businessHeader;

        return new PushController(
            ctx, new ConfigurationBuilder().Build(), new RecordingFcmService(), new StubCurrentAccount(accountId))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static PushSubscriptionRequest Req(
        string? role,
        string endpoint = "https://push.example/ep-1",
        string? orderToken = null,
        string? routeToken = null,
        int? clientId = null) => new()
    {
        Endpoint = endpoint,
        Keys = new PushKeys { P256dh = "p256dh-key", Auth = "auth-key" },
        Role = role,
        OrderToken = orderToken,
        DriverRouteToken = routeToken,
        ClientId = clientId,
    };

    private sealed class World : IDisposable
    {
        public required AppDbContext Ctx { get; init; }
        public required Business A { get; init; }
        public required Business B { get; init; }
        public required Account OwnerB { get; init; }
        public required Account Multi { get; init; }
        public required Account DriverOnly { get; init; }
        public required Account BuyerOnly { get; init; }
        public required Client VictimClient { get; init; }
        public required Client AttackerClient { get; init; }
        public required Order VictimOrder { get; init; }
        public required Order AttackerOrder { get; init; }

        public void Dispose() => Ctx.Dispose();

        public static async Task<World> SeedAsync()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid():N}")
                .Options;
            var ctx = new AppDbContext(options);

            var a = NewBusiness("Regi Bazar");
            var b = NewBusiness("Luna Bella");
            ctx.Businesses.AddRange(a, b);
            await ctx.SaveChangesAsync();

            var ownerB = NewAccount("Dueña B", 1);
            var multi = NewAccount("Multi", 2);
            var driverOnly = NewAccount("Chofer", 3);
            var buyerOnly = NewAccount("Compradora", 4);
            ctx.Accounts.AddRange(ownerB, multi, driverOnly, buyerOnly);
            await ctx.SaveChangesAsync();

            ctx.Memberships.AddRange(
                new Membership { AccountId = ownerB.Id, BusinessId = b.Id, Role = MembershipRole.Owner },
                new Membership { AccountId = multi.Id, BusinessId = a.Id, Role = MembershipRole.Owner },
                new Membership { AccountId = multi.Id, BusinessId = b.Id, Role = MembershipRole.Admin },
                new Membership { AccountId = driverOnly.Id, BusinessId = a.Id, Role = MembershipRole.Driver });

            var victim = new Client { BusinessId = b.Id, Name = "Víctima", NormalizedName = "victima" };
            var attacker = new Client { BusinessId = b.Id, Name = "Atacante", NormalizedName = "atacante" };
            ctx.Clients.AddRange(victim, attacker);
            await ctx.SaveChangesAsync();

            var victimOrder = NewOrder(b, victim, "order-victim");
            var attackerOrder = NewOrder(b, attacker, "order-attacker");
            ctx.Orders.AddRange(victimOrder, attackerOrder);

            ctx.DeliveryRoutes.AddRange(
                new DeliveryRoute { BusinessId = a.Id, Name = "Ruta A", DriverToken = "route-a" },
                new DeliveryRoute { BusinessId = b.Id, Name = "Ruta B", DriverToken = "route-b" });
            await ctx.SaveChangesAsync();

            return new World
            {
                Ctx = ctx, A = a, B = b,
                OwnerB = ownerB, Multi = multi, DriverOnly = driverOnly, BuyerOnly = buyerOnly,
                VictimClient = victim, AttackerClient = attacker,
                VictimOrder = victimOrder, AttackerOrder = attackerOrder,
            };
        }

        private static Account NewAccount(string name, int n) => new()
        {
            DisplayName = name,
            Phone = $"86811111{n:00}",
        };

        private static Order NewOrder(Business business, Client client, string token) => new()
        {
            BusinessId = business.Id,
            ClientId = client.Id,
            AccessToken = token,
            ExpiresAt = DateTime.UtcNow.AddDays(1),
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
        };
    }
}
