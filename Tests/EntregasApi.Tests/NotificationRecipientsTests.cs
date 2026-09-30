using EntregasApi.Data;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EntregasApi.Tests;

/// <summary>
/// Blindaje de destinatarios: cada notificación llega SOLO a quien le corresponde
/// (una clienta, las seguidoras de una tienda, las dueñas/administradoras de un
/// negocio o el chofer de una ruta) y jamás se mezclan entre sí, ni entre negocios,
/// ni en el historial de una cuenta que tiene varios papeles.
///
/// El escenario tiene dos negocios (A = Id 1, que además es el "negocio activo por
/// defecto" del DbContext sin tenant, y B = Id 2) y cuentas con todos los papeles.
/// </summary>
public class NotificationRecipientsTests
{
    // ═══════════════════════════════════════════
    //  UNA CLIENTA
    // ═══════════════════════════════════════════

    [Fact]
    public async Task ClientPush_GoesOnlyToThatClientasDevices()
    {
        using var w = await World.SeedAsync();
        var fcm = new RecordingFcmService();

        await ServiceWith(w.Ctx, fcm).SendNotificationToClientAsync(
            w.JuanaClient.Id, "Tu pedido salió", "🚚", url: "/o/abc", tag: "driver-en-route");

        // Ni otra clienta, ni la dueña, ni el chofer, ni otro negocio: solo el teléfono de Juana.
        Assert.Equal(new[] { "tok-juana" }, fcm.AllTokens);

        var call = Assert.Single(fcm.Calls);
        Assert.Equal("driver-en-route", call.Data!["type"]);
        Assert.Equal("buyer", call.Data["audience"]);
        Assert.Equal(w.A.Id.ToString(), call.Data["businessId"]);
        Assert.Equal(w.Juana.Id.ToString(), call.Data["accountId"]);
        Assert.Equal("/o/abc", call.Data["url"]);

        var row = Assert.Single(await w.Ctx.Notifications.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(w.JuanaClient.Id, row.ClientId);
        Assert.Equal(NotificationAudience.Buyer, row.Audience);
        Assert.Equal(w.A.Id, row.BusinessId);
    }

    [Fact]
    public async Task ClientPush_ForClientaWithoutAccount_IsSavedButNotPushedToAnyPhone()
    {
        using var w = await World.SeedAsync();
        var fcm = new RecordingFcmService();

        await ServiceWith(w.Ctx, fcm).SendNotificationToClientAsync(
            w.AnonClient.Id, "Pedido entregado", "🌸", tag: "delivered");

        Assert.Empty(fcm.Calls);
        var row = Assert.Single(await w.Ctx.Notifications.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(w.AnonClient.Id, row.ClientId);
        Assert.Equal(NotificationAudience.Buyer, row.Audience);
    }

    [Fact]
    public async Task ClientPush_ForUnknownClient_DoesNothing()
    {
        using var w = await World.SeedAsync();
        var fcm = new RecordingFcmService();

        await ServiceWith(w.Ctx, fcm).SendNotificationToClientAsync(9999, "T", "M");

        Assert.Empty(fcm.Calls);
        Assert.Empty(await w.Ctx.Notifications.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task ClientPush_AboutDriverEnRoute_ReachesOnlyTheOrdersClienta()
    {
        using var w = await World.SeedAsync();
        var fcm = new RecordingFcmService();

        await ServiceWith(w.Ctx, fcm).NotifyClientDriverEnRouteAsync(w.LupeClient.Id);
        await ServiceWith(w.Ctx, fcm).NotifyClientDeliveredAsync(w.LupeClient.Id);

        Assert.Equal(new[] { "tok-lupe" }, fcm.AllTokens);
        Assert.All(fcm.Calls, c => Assert.Equal(w.Lupe.Id.ToString(), c.Data!["accountId"]));
    }

    // ═══════════════════════════════════════════
    //  LAS VENDEDORAS (dueña / administradoras)
    // ═══════════════════════════════════════════

    [Fact]
    public async Task OwnersPush_GoesOnlyToOwnersAndAdminsOfThatBusiness()
    {
        using var w = await World.SeedAsync();
        var fcm = new RecordingFcmService();

        await ServiceWith(w.Ctx, fcm).SendNotificationToBusinessOwnersAsync(
            w.A.Id, "💰 Saldos sin cobrar", "Juana debe $500", "/orders", "saldos-sin-cobrar");

        // Dueña y administradora de A. NO el chofer ni el escaneador de A, NO las
        // clientas/seguidoras, NO las dueñas de otro negocio.
        Assert.Equal(
            new[] { "tok-admin-a", "tok-owner-a" },
            fcm.AllTokens.OrderBy(t => t).ToArray());

        var call = Assert.Single(fcm.Calls);
        Assert.Equal("saldos-sin-cobrar", call.Data!["type"]);
        Assert.Equal("seller", call.Data["audience"]);
        Assert.Equal(w.A.Id.ToString(), call.Data["businessId"]);
        Assert.False(call.Data.ContainsKey("accountId")); // el aviso es para varias cuentas

        var rows = await w.Ctx.Notifications.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(NotificationAudience.Seller, r.Audience);
            Assert.Equal(w.A.Id, r.BusinessId);
            Assert.Null(r.ClientId);
        });
        Assert.Equal(
            new[] { w.OwnerA.Id, w.AdminA.Id }.OrderBy(i => i),
            rows.Select(r => r.AccountId!.Value).OrderBy(i => i));
    }

    [Fact]
    public async Task OwnersPush_IgnoresTheRequestsActiveTenant()
    {
        using var w = await World.SeedAsync();
        var fcm = new RecordingFcmService();

        // El DbContext sin tenant cae al negocio #1 (A). El aviso es del negocio B (Id 2):
        // debe llegar a las dueñas de B y NUNCA a las de A.
        Assert.Equal(1, w.Ctx.ActiveBusinessId);
        await ServiceWith(w.Ctx, fcm).SendNotificationToBusinessOwnersAsync(
            w.B.Id, "Nuevo apartado", "Ana apartó algo", "/orders", "reserve");

        Assert.Equal(
            new[] { "tok-dual", "tok-owner-b" },
            fcm.AllTokens.OrderBy(t => t).ToArray());
        Assert.DoesNotContain("tok-owner-a", fcm.AllTokens);
        Assert.DoesNotContain("tok-admin-a", fcm.AllTokens);
    }

    [Fact]
    public async Task OwnersPush_NeverLinksTheNotificationToAClientRow()
    {
        using var w = await World.SeedAsync();
        // La dueña de A también es clienta de su propia tienda.
        w.Ctx.Clients.Add(new Client
        {
            BusinessId = w.A.Id, AccountId = w.OwnerA.Id, Name = "Dueña A", NormalizedName = "duena a",
        });
        await w.Ctx.SaveChangesAsync();

        await ServiceWith(w.Ctx, new RecordingFcmService()).SendNotificationToBusinessOwnersAsync(
            w.A.Id, "Pulso", "Vendiste mucho", "/home", "pulso-negocio");

        var ownerRow = await w.Ctx.Notifications.IgnoreQueryFilters()
            .SingleAsync(n => n.AccountId == w.OwnerA.Id);
        Assert.Equal(NotificationAudience.Seller, ownerRow.Audience);
        Assert.Null(ownerRow.ClientId);
    }

    [Fact]
    public async Task OwnersPush_WithNoOwners_DoesNothing()
    {
        using var w = await World.SeedAsync();
        var emptyBusiness = NewBusiness("Sin equipo");
        w.Ctx.Businesses.Add(emptyBusiness);
        await w.Ctx.SaveChangesAsync();
        var fcm = new RecordingFcmService();

        await ServiceWith(w.Ctx, fcm).SendNotificationToBusinessOwnersAsync(emptyBusiness.Id, "T", "M");

        Assert.Empty(fcm.Calls);
        Assert.Empty(await w.Ctx.Notifications.IgnoreQueryFilters().ToListAsync());
    }

    // ═══════════════════════════════════════════
    //  LAS CLIENTAS (seguidoras de una tienda)
    // ═══════════════════════════════════════════

    [Fact]
    public async Task FollowersPush_GoesOnlyToFollowers_AndNeverToTheSellers()
    {
        using var w = await World.SeedAsync();
        var fcm = new RecordingFcmService();

        await ServiceWith(w.Ctx, fcm).SendNotificationToFollowersAsync(
            w.A.Id, "¡Estamos en vivo!", "Entra ya", "/store/1", "live-started", requireNotifyOnLive: true);

        // Seguidoras de A: Juana, Lupe y Dual. No la dueña/administradora/chofer de A.
        Assert.Equal(
            new[] { "tok-dual", "tok-juana", "tok-lupe" },
            fcm.AllTokens.OrderBy(t => t).ToArray());

        var call = Assert.Single(fcm.Calls);
        Assert.Equal("buyer", call.Data!["audience"]);
        Assert.Equal(w.A.Id.ToString(), call.Data["businessId"]);
        Assert.False(call.Data.ContainsKey("accountId"));

        var rows = await w.Ctx.Notifications.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(NotificationAudience.Buyer, r.Audience));
    }

    // ═══════════════════════════════════════════
    //  UNA CUENTA CON VARIOS PAPELES
    // ═══════════════════════════════════════════

    [Fact]
    public async Task AccountWithBothRoles_NeverMixesSellerAndBuyerInbox()
    {
        using var w = await World.SeedAsync();
        var push = ServiceWith(w.Ctx, new RecordingFcmService());

        // "Dual" es dueña de B (avisos de su tienda) y seguidora de A (avisos de compradora).
        await push.SendNotificationToBusinessOwnersAsync(w.B.Id, "⌛ Pedidos por vencer", "2 pedidos", "/orders", "pedidos-por-vencer");
        await push.SendNotificationToFollowersAsync(w.A.Id, "¡Estamos en vivo!", "Entra ya", "/store/1", "live-started");

        var inbox = new BuyerNotificationService(w.Ctx);

        var seller = await inbox.GetMyNotificationsAsync(w.Dual.Id, CancellationToken.None, "seller");
        var sellerItem = Assert.Single(seller);
        Assert.Equal("⌛ Pedidos por vencer", sellerItem.Title);
        Assert.Equal("seller", sellerItem.Audience);
        Assert.Equal(w.B.Id, sellerItem.BusinessId);

        var buyer = await inbox.GetMyNotificationsAsync(w.Dual.Id, CancellationToken.None, "buyer");
        var buyerItem = Assert.Single(buyer);
        Assert.Equal("¡Estamos en vivo!", buyerItem.Title);
        Assert.Equal("buyer", buyerItem.Audience);
        Assert.Equal(w.A.Id, buyerItem.BusinessId);

        // Sin filtro (versiones anteriores de la app) se devuelve todo, como antes.
        Assert.Equal(2, (await inbox.GetMyNotificationsAsync(w.Dual.Id, CancellationToken.None)).Count);

        // El contador de cada campanita solo cuenta lo suyo.
        Assert.Equal(1, await inbox.CountUnreadAsync(w.Dual.Id, CancellationToken.None, "seller"));
        Assert.Equal(1, await inbox.CountUnreadAsync(w.Dual.Id, CancellationToken.None, "buyer"));

        // "Marcar todas" en un lado no toca el otro.
        Assert.Equal(1, await inbox.MarkAllAsReadAsync(w.Dual.Id, CancellationToken.None, "buyer"));
        Assert.Equal(0, await inbox.CountUnreadAsync(w.Dual.Id, CancellationToken.None, "buyer"));
        Assert.Equal(1, await inbox.CountUnreadAsync(w.Dual.Id, CancellationToken.None, "seller"));
    }

    [Fact]
    public async Task PureClienta_NeverSeesStoreNotifications_EvenWithoutFilter()
    {
        using var w = await World.SeedAsync();
        var push = ServiceWith(w.Ctx, new RecordingFcmService());
        await push.SendNotificationToBusinessOwnersAsync(w.A.Id, "💰 Saldos sin cobrar", "Juana debe $500", "/orders", "saldos-sin-cobrar");

        var inbox = new BuyerNotificationService(w.Ctx);

        Assert.Empty(await inbox.GetMyNotificationsAsync(w.Juana.Id, CancellationToken.None));
        Assert.Empty(await inbox.GetMyNotificationsAsync(w.Juana.Id, CancellationToken.None, "seller"));
        Assert.Equal(0, await inbox.CountUnreadAsync(w.Juana.Id, CancellationToken.None));
        Assert.Equal(0, await inbox.MarkAllAsReadAsync(w.Juana.Id, CancellationToken.None));
    }

    [Fact]
    public async Task StoreNotifications_DisappearWhenTheAccountLeavesTheTeam()
    {
        using var w = await World.SeedAsync();
        await ServiceWith(w.Ctx, new RecordingFcmService()).SendNotificationToBusinessOwnersAsync(
            w.B.Id, "💰 Saldos sin cobrar", "Juana debe $500", "/orders", "saldos-sin-cobrar");

        var inbox = new BuyerNotificationService(w.Ctx);
        var visible = Assert.Single(await inbox.GetMyNotificationsAsync(w.Dual.Id, CancellationToken.None, "seller"));

        // La sacan del equipo de B: deja de ver los avisos de la tienda (nombres, saldos).
        w.Ctx.Memberships.RemoveRange(
            w.Ctx.Memberships.Where(m => m.AccountId == w.Dual.Id && m.BusinessId == w.B.Id));
        await w.Ctx.SaveChangesAsync();

        Assert.Empty(await inbox.GetMyNotificationsAsync(w.Dual.Id, CancellationToken.None, "seller"));
        Assert.Empty(await inbox.GetMyNotificationsAsync(w.Dual.Id, CancellationToken.None));
        Assert.Equal(0, await inbox.CountUnreadAsync(w.Dual.Id, CancellationToken.None, "seller"));
        await Assert.ThrowsAsync<NotificationNotFoundException>(() =>
            inbox.MarkAsReadAsync(w.Dual.Id, visible.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Inbox_RejectsAnUnknownAudience()
    {
        using var w = await World.SeedAsync();
        var inbox = new BuyerNotificationService(w.Ctx);

        await Assert.ThrowsAsync<InvalidNotificationAudienceException>(() =>
            inbox.GetMyNotificationsAsync(w.Juana.Id, CancellationToken.None, "admin"));
        await Assert.ThrowsAsync<InvalidNotificationAudienceException>(() =>
            inbox.CountUnreadAsync(w.Juana.Id, CancellationToken.None, "todos"));
        await Assert.ThrowsAsync<InvalidNotificationAudienceException>(() =>
            inbox.MarkAllAsReadAsync(w.Juana.Id, CancellationToken.None, "vendedora"));
    }

    [Fact]
    public async Task Inbox_AudienceIsCaseInsensitive_AndBlankMeansAll()
    {
        using var w = await World.SeedAsync();
        await ServiceWith(w.Ctx, new RecordingFcmService()).SendNotificationToClientAsync(
            w.JuanaClient.Id, "Hola", "m", tag: "general");
        var inbox = new BuyerNotificationService(w.Ctx);

        Assert.Single(await inbox.GetMyNotificationsAsync(w.Juana.Id, CancellationToken.None, " BUYER "));
        Assert.Single(await inbox.GetMyNotificationsAsync(w.Juana.Id, CancellationToken.None, ""));
        Assert.Single(await inbox.GetMyNotificationsAsync(w.Juana.Id, CancellationToken.None, null));
    }

    [Fact]
    public void NotificationWithoutExplicitAudience_DefaultsToBuyer()
    {
        // Red de seguridad: un aviso nuevo nunca queda sin destinatario ni cae del lado de la tienda.
        Assert.Equal(NotificationAudience.Buyer, new Notification().Audience);
        Assert.True(NotificationAudience.IsValid("buyer"));
        Assert.True(NotificationAudience.IsValid("seller"));
        Assert.False(NotificationAudience.IsValid("admin"));
        Assert.False(NotificationAudience.IsValid(null));
    }

    // ═══════════════════════════════════════════
    //  CHOFERES
    // ═══════════════════════════════════════════

    [Fact]
    public async Task NewRoute_AlertsOnlyTheRoutesDriver_WhenHisDeviceIsRegistered()
    {
        using var ctx = NewContext();
        var business = NewBusiness("Regi Bazar");
        ctx.Businesses.Add(business);
        await ctx.SaveChangesAsync();
        ctx.FcmTokens.AddRange(
            new FcmToken { BusinessId = business.Id, Token = "drv-1", Role = "driver", DriverRouteToken = "route-1" },
            new FcmToken { BusinessId = business.Id, Token = "drv-2", Role = "driver", DriverRouteToken = "route-2" },
            new FcmToken { BusinessId = business.Id, Token = "drv-3", Role = "driver" });
        await ctx.SaveChangesAsync();
        var fcm = new RecordingFcmService();

        await ServiceWith(ctx, fcm).NotifyDriversNewRouteAsync("Ruta Norte", "route-1", 4);

        Assert.Equal(new[] { "drv-1" }, fcm.AllTokens);
    }

    [Fact]
    public async Task NewRoute_FallsBackToTheBusinessDrivers_OnlyWhenNobodyHasOpenedTheRoute()
    {
        using var ctx = NewContext();
        var business = NewBusiness("Regi Bazar");
        var other = NewBusiness("Otra tienda");
        ctx.Businesses.AddRange(business, other);
        await ctx.SaveChangesAsync();
        ctx.FcmTokens.AddRange(
            new FcmToken { BusinessId = business.Id, Token = "drv-1", Role = "driver", DriverRouteToken = "route-1" },
            new FcmToken { BusinessId = business.Id, Token = "drv-2", Role = "driver" },
            // Chofer de OTRO negocio y dispositivo de administración: nunca reciben la alerta.
            new FcmToken { BusinessId = other.Id, Token = "drv-other", Role = "driver" },
            new FcmToken { BusinessId = business.Id, Token = "admin-device", Role = "admin" });
        await ctx.SaveChangesAsync();
        var fcm = new RecordingFcmService();

        await ServiceWith(ctx, fcm).NotifyDriversNewRouteAsync("Ruta Sur", "route-9", 2);

        Assert.Equal(new[] { "drv-1", "drv-2" }, fcm.AllTokens.OrderBy(t => t).ToArray());
    }

    [Fact]
    public async Task RouteUpdate_ReachesOnlyTheDriverOfThatRoute()
    {
        using var ctx = NewContext();
        var business = NewBusiness("Regi Bazar");
        ctx.Businesses.Add(business);
        await ctx.SaveChangesAsync();
        ctx.FcmTokens.AddRange(
            new FcmToken { BusinessId = business.Id, Token = "drv-1", Role = "driver", DriverRouteToken = "route-1" },
            new FcmToken { BusinessId = business.Id, Token = "drv-2", Role = "driver", DriverRouteToken = "route-2" });
        await ctx.SaveChangesAsync();
        var fcm = new RecordingFcmService();

        await ServiceWith(ctx, fcm).NotifyDriverFcmAsync("route-2", "🔄 Ruta reordenada", "Nuevo orden");

        Assert.Equal(new[] { "drv-2" }, fcm.AllTokens);
    }

    [Fact]
    public async Task DriverAlerts_GoThroughTheDriversFirebaseProject_NeverTheAppOne()
    {
        using var ctx = NewContext();
        var business = NewBusiness("Regi Bazar");
        ctx.Businesses.Add(business);
        await ctx.SaveChangesAsync();
        ctx.FcmTokens.Add(new FcmToken { BusinessId = business.Id, Token = "drv-1", Role = "driver", DriverRouteToken = "route-1" });
        await ctx.SaveChangesAsync();
        var app = new RecordingFcmService();
        var drivers = new RecordingDriverFcmService();
        var push = ServiceWith(ctx, app, drivers);

        await push.NotifyDriversNewRouteAsync("Ruta Norte", "route-1", 4);
        await push.NotifyDriverFcmAsync("route-1", "🔄 Ruta reordenada", "Nuevo orden");

        // Los dispositivos de choferes son de su propio proyecto de Firebase: por el de la app no se manda nada.
        Assert.Empty(app.Calls);
        Assert.Equal(new[] { "drv-1" }, drivers.AllTokens);
        Assert.Equal(2, drivers.Calls.Count);
    }

    [Fact]
    public async Task AppAlerts_NeverUseTheDriversFirebaseProject()
    {
        using var w = await World.SeedAsync();
        var app = new RecordingFcmService();
        var drivers = new RecordingDriverFcmService();
        var push = ServiceWith(w.Ctx, app, drivers);

        await push.SendNotificationToClientAsync(w.JuanaClient.Id, "Tu pedido salió", "🚚", tag: "driver-en-route");
        await push.SendNotificationToBusinessOwnersAsync(w.A.Id, "Saldos", "m", "/orders", "saldos-sin-cobrar");
        await push.SendNotificationToFollowersAsync(w.A.Id, "En vivo", "m", "/store/1", "live-started");

        // Clientas y vendedoras usan el proyecto de la app; el de choferes no se toca.
        Assert.Equal(3, app.Calls.Count);
        Assert.Empty(drivers.Calls);
    }

    [Fact]
    public async Task DriverAlerts_UseTheAppProject_WhenThereIsNoSeparateDriversCredential()
    {
        using var ctx = NewContext();
        var business = NewBusiness("Regi Bazar");
        ctx.Businesses.Add(business);
        await ctx.SaveChangesAsync();
        ctx.FcmTokens.Add(new FcmToken { BusinessId = business.Id, Token = "drv-1", Role = "driver", DriverRouteToken = "route-1" });
        await ctx.SaveChangesAsync();
        var app = new RecordingFcmService();

        // Sin IDriverFcmService (no hay segunda credencial): el aviso sale por el proyecto de la app.
        await ServiceWith(ctx, app).NotifyDriversNewRouteAsync("Ruta Norte", "route-1", 4);

        Assert.Equal(new[] { "drv-1" }, app.AllTokens);
    }

    // ═══════════════════════════════════════════
    //  Escenario
    // ═══════════════════════════════════════════

    private sealed class World : IDisposable
    {
        public required AppDbContext Ctx { get; init; }
        public required Business A { get; init; }
        public required Business B { get; init; }
        public required Account OwnerA { get; init; }
        public required Account AdminA { get; init; }
        public required Account Juana { get; init; }
        public required Account Lupe { get; init; }
        public required Account OwnerB { get; init; }
        public required Account Dual { get; init; }
        public required Client JuanaClient { get; init; }
        public required Client LupeClient { get; init; }
        public required Client AnonClient { get; init; }

        public void Dispose() => Ctx.Dispose();

        public static async Task<World> SeedAsync()
        {
            var ctx = NewContext();
            var a = NewBusiness("Regi Bazar");
            var b = NewBusiness("Luna Bella");
            ctx.Businesses.AddRange(a, b);
            await ctx.SaveChangesAsync();

            var ownerA = NewAccount("Dueña A", 1);
            var adminA = NewAccount("Administradora A", 2);
            var driverA = NewAccount("Chofer A", 3);
            var scanerA = NewAccount("Escaneador A", 4);
            var ownerB = NewAccount("Dueña B", 5);
            var juana = NewAccount("Juana", 6);
            var lupe = NewAccount("Lupe", 7);
            var dual = NewAccount("Dual", 8);
            ctx.Accounts.AddRange(ownerA, adminA, driverA, scanerA, ownerB, juana, lupe, dual);
            await ctx.SaveChangesAsync();

            ctx.Memberships.AddRange(
                new Membership { AccountId = ownerA.Id, BusinessId = a.Id, Role = MembershipRole.Owner },
                new Membership { AccountId = adminA.Id, BusinessId = a.Id, Role = MembershipRole.Admin },
                new Membership { AccountId = driverA.Id, BusinessId = a.Id, Role = MembershipRole.Driver },
                new Membership { AccountId = scanerA.Id, BusinessId = a.Id, Role = MembershipRole.Scaner },
                new Membership { AccountId = ownerB.Id, BusinessId = b.Id, Role = MembershipRole.Owner },
                // "Dual" es dueña de B y, a la vez, clienta y seguidora de A.
                new Membership { AccountId = dual.Id, BusinessId = b.Id, Role = MembershipRole.Owner });

            var juanaClient = ClientOf(a, juana, "Juana");
            var lupeClient = ClientOf(a, lupe, "Lupe");
            var anonClient = ClientOf(a, null, "Sin cuenta");
            var dualClient = ClientOf(a, dual, "Dual");
            ctx.Clients.AddRange(juanaClient, lupeClient, anonClient, dualClient);

            ctx.StoreFollowers.AddRange(
                new StoreFollower { BusinessId = a.Id, AccountId = juana.Id },
                new StoreFollower { BusinessId = a.Id, AccountId = lupe.Id },
                new StoreFollower { BusinessId = a.Id, AccountId = dual.Id });

            ctx.BuyerDeviceTokens.AddRange(
                Device(ownerA, "tok-owner-a"),
                Device(adminA, "tok-admin-a"),
                Device(driverA, "tok-driver-a"),
                Device(scanerA, "tok-scaner-a"),
                Device(ownerB, "tok-owner-b"),
                Device(juana, "tok-juana"),
                Device(lupe, "tok-lupe"),
                Device(dual, "tok-dual"));
            await ctx.SaveChangesAsync();

            return new World
            {
                Ctx = ctx, A = a, B = b,
                OwnerA = ownerA, AdminA = adminA, OwnerB = ownerB,
                Juana = juana, Lupe = lupe, Dual = dual,
                JuanaClient = juanaClient, LupeClient = lupeClient, AnonClient = anonClient,
            };
        }

        private static Account NewAccount(string name, int n) => new()
        {
            DisplayName = name,
            Phone = $"86800000{n:00}",
        };

        private static Client ClientOf(Business business, Account? account, string name) => new()
        {
            BusinessId = business.Id,
            AccountId = account?.Id,
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
        };

        private static BuyerDeviceToken Device(Account account, string token) => new()
        {
            AccountId = account.Id,
            Token = token,
            Platform = "android",
        };
    }

    private static AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid():N}")
            .Options;
        return new AppDbContext(options);
    }

    private static IPushNotificationService ServiceWith(
        AppDbContext ctx, IFcmService fcm, IDriverFcmService? driverFcm = null) =>
        new PushNotificationService(ctx, new ConfigurationBuilder().Build(),
            NullLogger<PushNotificationService>.Instance, fcm, driverFcm);

    private static Business NewBusiness(string name) => new()
    {
        Name = name,
        Slug = $"tienda-{Guid.NewGuid():N}",
        City = "Nuevo Laredo",
        FrontendUrl = "https://example.com",
        BrandPrimaryColor = "#FF0072",
        DepotLat = 27.4861,
        DepotLng = -99.5069,
    };
}
