using System.Security.Claims;
using EntregasApi.Data;
using EntregasApi.Hubs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntregasApi.Tests;

/// <summary>
/// Los grupos de tiempo real de la vendedora ("Admins" con los avisos de su tienda y el vivo)
/// son SOLO para dueña o administradora del negocio. Un chofer o un escaneador con cuenta no
/// entra: la app de Flutter es solo de clientas y vendedoras, y los choferes tendrán su propia app.
/// </summary>
public class SellerHubAccessTests
{
    [Theory]
    [InlineData("owner", true)]
    [InlineData("admin", true)]
    [InlineData("driver", false)]
    [InlineData("scaner", false)]
    [InlineData("buyer", false)]
    [InlineData("anonymous", false)]
    public async Task EveryAdminGroup_IsOnlyForOwnerAndAdmin(string who, bool allowed)
    {
        using var w = await World.SeedAsync();
        var user = w.UserFor(who);

        foreach (var (name, join) in w.AdminJoins(user))
        {
            var joined = await join();

            Assert.True(joined == allowed, $"{name}: con '{who}' debía {(allowed ? "entrar" : "NO entrar")}.");
        }

        var expectedGroup = SignalRGroupNames.Admins(w.A.Id);
        if (allowed)
        {
            Assert.All(w.Groups.Joined, g => Assert.Equal(expectedGroup, g));
            Assert.Equal(4, w.Groups.Joined.Count); // Delivery, Logistics, Orders y Tracking
        }
        else
        {
            Assert.Empty(w.Groups.Joined);
        }
    }

    [Theory]
    [InlineData("owner", true)]
    [InlineData("admin", true)]
    [InlineData("driver", false)]
    [InlineData("scaner", false)]
    [InlineData("anonymous", false)]
    public async Task LiveAdminJoin_IsOnlyForOwnerAndAdmin(string who, bool allowed)
    {
        using var w = await World.SeedAsync();

        var joined = await w.Live(w.UserFor(who)).JoinAdminLive();

        Assert.Equal(allowed, joined);
        Assert.Equal(allowed ? 1 : 0, w.Groups.Joined.Count);
    }

    [Fact]
    public async Task AnnounceProduct_IsASellerAction_DriversAndScanersCannotDoIt()
    {
        using var w = await World.SeedAsync();
        w.Ctx.LiveAnnouncements.Add(new LiveAnnouncement { BusinessId = w.A.Id, StartedAt = DateTime.UtcNow });
        var product = new Product { BusinessId = w.A.Id, SKU = "sku-1", Name = "Bolsa", Price = 100m, Stock = 3, IsActive = true };
        w.Ctx.Products.Add(product);
        await w.Ctx.SaveChangesAsync();

        Assert.False(await w.Live(w.UserFor("driver")).AnnounceProduct(product.Id));
        Assert.False(await w.Live(w.UserFor("scaner")).AnnounceProduct(product.Id));
        Assert.Null((await w.Ctx.LiveAnnouncements.SingleAsync()).CurrentProductId);

        Assert.True(await w.Live(w.UserFor("owner")).AnnounceProduct(product.Id));
        Assert.Equal(product.Id, (await w.Ctx.LiveAnnouncements.AsNoTracking().SingleAsync()).CurrentProductId);
    }

    [Fact]
    public async Task AdminJoin_WithExplicitBusiness_MustBeOneTheyManage_NotAFallback()
    {
        using var w = await World.SeedAsync();
        // "Mixta": dueña de A y chofer de B.
        var user = w.UserFor("mixed");

        // Pide B (donde solo es chofer): no entra, ni cae silenciosamente a A.
        Assert.False(await w.Delivery(user).JoinAdminGroup(w.B.Id.ToString()));
        Assert.Empty(w.Groups.Joined);

        // Pide A o no pide nada (administra exactamente uno): entra a A.
        Assert.True(await w.Delivery(user).JoinAdminGroup(w.A.Id.ToString()));
        Assert.True(await w.Delivery(user).JoinAdminGroup());
        Assert.All(w.Groups.Joined, g => Assert.Equal(SignalRGroupNames.Admins(w.A.Id), g));
    }

    [Fact]
    public async Task AdminJoin_OfAnInactiveBusiness_IsRejected()
    {
        using var w = await World.SeedAsync();
        w.A.IsActive = false;
        await w.Ctx.SaveChangesAsync();

        Assert.False(await w.Delivery(w.UserFor("owner")).JoinAdminGroup());
    }

    // ═══════════════════════════════════════════
    //  Escenario
    // ═══════════════════════════════════════════

    private sealed class World : IDisposable
    {
        public required AppDbContext Ctx { get; init; }
        public required Business A { get; init; }
        public required Business B { get; init; }
        public required Dictionary<string, ClaimsPrincipal?> Users { get; init; }
        public RecordingGroupManager Groups { get; } = new();

        public void Dispose() => Ctx.Dispose();

        public ClaimsPrincipal? UserFor(string who) => Users[who];

        public IEnumerable<(string Name, Func<Task<bool>> Join)> AdminJoins(ClaimsPrincipal? user)
        {
            yield return ("DeliveryHub", () => Delivery(user).JoinAdminGroup());
            yield return ("LogisticsHub", () => Logistics(user).JoinAdminGroup());
            yield return ("OrderHub", () => Orders(user).JoinAdminGroup());
            yield return ("TrackingHub", () => Tracking(user).JoinAdminGroup());
        }

        public DeliveryHub Delivery(ClaimsPrincipal? user) => Wire(new DeliveryHub(Ctx, new StubTenant(1), null!), user);
        public LogisticsHub Logistics(ClaimsPrincipal? user) => Wire(new LogisticsHub(Ctx, new StubTenant(1)), user);
        public OrderHub Orders(ClaimsPrincipal? user) => Wire(new OrderHub(Ctx, new StubTenant(1)), user);
        public TrackingHub Tracking(ClaimsPrincipal? user) => Wire(new TrackingHub(Ctx, new StubTenant(1)), user);
        public LiveHub Live(ClaimsPrincipal? user) => Wire(new LiveHub(Ctx, new StubTenant(1)), user);

        private THub Wire<THub>(THub hub, ClaimsPrincipal? user) where THub : Hub
        {
            hub.Context = new FakeHubCallerContext(user);
            hub.Groups = Groups;
            hub.Clients = new FakeHubCallerClients();
            return hub;
        }

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

            var accounts = new Dictionary<string, Account>();
            var n = 0;
            foreach (var key in new[] { "owner", "admin", "driver", "scaner", "buyer", "mixed" })
            {
                accounts[key] = new Account { DisplayName = key, Phone = $"86822222{++n:00}" };
            }
            ctx.Accounts.AddRange(accounts.Values);
            await ctx.SaveChangesAsync();

            ctx.Memberships.AddRange(
                new Membership { AccountId = accounts["owner"].Id, BusinessId = a.Id, Role = MembershipRole.Owner },
                new Membership { AccountId = accounts["admin"].Id, BusinessId = a.Id, Role = MembershipRole.Admin },
                new Membership { AccountId = accounts["driver"].Id, BusinessId = a.Id, Role = MembershipRole.Driver },
                new Membership { AccountId = accounts["scaner"].Id, BusinessId = a.Id, Role = MembershipRole.Scaner },
                // "buyer" no tiene ninguna membresía (es solo clienta).
                new Membership { AccountId = accounts["mixed"].Id, BusinessId = a.Id, Role = MembershipRole.Owner },
                new Membership { AccountId = accounts["mixed"].Id, BusinessId = b.Id, Role = MembershipRole.Driver });
            await ctx.SaveChangesAsync();

            var users = accounts.ToDictionary(kv => kv.Key, kv => (ClaimsPrincipal?)Principal(kv.Value.Id));
            users["anonymous"] = null;

            return new World { Ctx = ctx, A = a, B = b, Users = users };
        }

        private static ClaimsPrincipal Principal(int accountId) =>
            new(new ClaimsIdentity(new[] { new Claim("account_id", accountId.ToString()) }, "test"));

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

    private sealed class RecordingGroupManager : IGroupManager
    {
        public List<string> Joined { get; } = new();

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Joined.Add(groupName);
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeHubCallerContext(ClaimsPrincipal? user) : HubCallerContext
    {
        public override string ConnectionId => "conn-1";
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    /// <summary>Clientes de hub que no hacen nada (los avisos en tiempo real no importan en estas pruebas).</summary>
    private sealed class FakeHubCallerClients : IHubCallerClients
    {
        private static readonly IClientProxy Proxy = new QuietClientProxy();

        public IClientProxy Caller => Proxy;
        public IClientProxy Others => Proxy;
        public IClientProxy OthersInGroup(string groupName) => Proxy;
        public IClientProxy All => Proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Client(string connectionId) => Proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
        public IClientProxy Group(string groupName) => Proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
        public IClientProxy User(string userId) => Proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
    }

    private sealed class QuietClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
