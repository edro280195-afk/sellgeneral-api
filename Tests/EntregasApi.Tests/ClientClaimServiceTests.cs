using EntregasApi.Data;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntregasApi.Tests;

/// <summary>
/// Tests del flujo de "reclamar perfil" (plan 0.3). Cubre los tres escenarios
/// pedidos en ROLYCONTEXTO.md:
///   - enlace por token OK
///   - enlace sin prueba RECHAZADO
///   - reclamar una Client no expone otra
/// </summary>
public class ClientClaimServiceTests
{
    [Fact]
    public async Task ClaimByOrderToken_LinksClientToAccount()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedBusinessAsync(ctx, "Regi Bazar", "regibazar");
        var account = await SeedAccountAsync(ctx, "Lupita", phone: "8671234567");
        var client = await SeedClientAsync(ctx, business.Id, "Lupita Pérez", phone: "8671234567");
        var order = await SeedOrderAsync(ctx, business.Id, client.Id, "TOKEN-LUPITA-1");

        var service = new ClientClaimService(ctx);
        var outcome = await service.ClaimByOrderTokenAsync(account.Id, order.AccessToken);

        Assert.Equal(ClaimStatus.Linked, outcome.Status);
        Assert.NotNull(outcome.Result);
        Assert.Equal(client.Id, outcome.Result!.ClientId);
        Assert.Equal(business.Id, outcome.Result.BusinessId);
        Assert.Equal("order-token", outcome.Result.LinkedBy);
        Assert.Equal(account.Id, client.AccountId);

        var audit = await ctx.ClientClaimAudits.SingleAsync();
        Assert.Equal(account.Id, audit.AccountId);
        Assert.Equal(client.Id, audit.ClientId);
        Assert.Equal(ClientClaimMode.OrderToken, audit.Mode);
    }

    [Fact]
    public async Task ClaimByOrderToken_WithoutAccessToken_ReturnsNoProof()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedBusinessAsync(ctx, "Regi Bazar", "regibazar");
        var account = await SeedAccountAsync(ctx, "Ana", phone: "8675555555");
        var client = await SeedClientAsync(ctx, business.Id, "Ana López", phone: "8675555555");

        var service = new ClientClaimService(ctx);
        var outcome = await service.ClaimByOrderTokenAsync(account.Id, "");

        Assert.Equal(ClaimStatus.NoProof, outcome.Status);
        Assert.Null(outcome.Result);
        Assert.Null(client.AccountId);
    }

    [Fact]
    public async Task ClaimByOrderToken_WithUnknownToken_ReturnsNotFound()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedBusinessAsync(ctx, "Regi Bazar", "regibazar");
        var account = await SeedAccountAsync(ctx, "María", phone: "8679999999");

        var service = new ClientClaimService(ctx);
        var outcome = await service.ClaimByOrderTokenAsync(account.Id, "NO-EXISTE");

        Assert.Equal(ClaimStatus.NotFound, outcome.Status);
        Assert.Null(outcome.Result);
    }

    [Fact]
    public async Task ClaimByOrderToken_RejectsWhenClientAlreadyClaimedByOther()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedBusinessAsync(ctx, "Regi Bazar", "regibazar");

        var realOwner = await SeedAccountAsync(ctx, "Real Dueña", phone: "8671111111");
        var intruder = await SeedAccountAsync(ctx, "Intruso", phone: "8672222222");
        var client = await SeedClientAsync(ctx, business.Id, "Marta", phone: "8671111111");
        client.AccountId = realOwner.Id;
        await ctx.SaveChangesAsync();

        var order = await SeedOrderAsync(ctx, business.Id, client.Id, "TOKEN-MARTA-1");

        var service = new ClientClaimService(ctx);
        var outcome = await service.ClaimByOrderTokenAsync(intruder.Id, order.AccessToken);

        Assert.Equal(ClaimStatus.AlreadyClaimedByOther, outcome.Status);
        Assert.Equal(realOwner.Id, client.AccountId);
    }

    [Fact]
    public async Task ClaimByPhoneMatch_RejectsWhenPhoneDoesNotMatch()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedBusinessAsync(ctx, "Regi Bazar", "regibazar");
        var account = await SeedAccountAsync(ctx, "Sofía", phone: "8673333333");
        var client = await SeedClientAsync(ctx, business.Id, "Sofía Otro Tel", phone: "8674444444");

        var service = new ClientClaimService(ctx);
        var outcome = await service.ClaimByPhoneMatchAsync(account.Id, client.Id);

        Assert.Equal(ClaimStatus.NoProof, outcome.Status);
        Assert.Null(client.AccountId);
    }

    [Fact]
    public async Task ClaimByPhoneMatch_RejectsWhenAccountHasNoPhone()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedBusinessAsync(ctx, "Regi Bazar", "regibazar");
        var account = await SeedAccountAsync(ctx, "Sin Teléfono", phone: null);
        var client = await SeedClientAsync(ctx, business.Id, "Cualquiera", phone: "8675555555");

        var service = new ClientClaimService(ctx);
        var outcome = await service.ClaimByPhoneMatchAsync(account.Id, client.Id);

        Assert.Equal(ClaimStatus.Forbidden, outcome.Status);
    }

    [Fact]
    public async Task ClaimByPhoneMatch_LinksAcrossDifferentTenants()
    {
        using var ctx = TestDbContextFactory.Create();
        var regi = await SeedBusinessAsync(ctx, "Regi Bazar", "regibazar");
        var rodriguez = await SeedBusinessAsync(ctx, "Bazar Rodríguez", "rodriguez");

        var account = await SeedAccountAsync(ctx, "Lupita Multi", phone: "8671234567");
        var clientRegi = await SeedClientAsync(ctx, regi.Id, "Lupita en Regi", phone: "8671234567");
        var clientRodriguez = await SeedClientAsync(ctx, rodriguez.Id, "Lupita en Rodríguez", phone: "8671234567");

        var service = new ClientClaimService(ctx);
        var outcome1 = await service.ClaimByPhoneMatchAsync(account.Id, clientRegi.Id);
        var outcome2 = await service.ClaimByPhoneMatchAsync(account.Id, clientRodriguez.Id);

        Assert.Equal(ClaimStatus.Linked, outcome1.Status);
        Assert.Equal(ClaimStatus.Linked, outcome2.Status);
        Assert.Equal(account.Id, clientRegi.AccountId);
        Assert.Equal(account.Id, clientRodriguez.AccountId);
    }

    [Fact]
    public async Task CandidatesByPhone_DoesNotLeakPhoneOrAddress()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedBusinessAsync(ctx, "Regi Bazar", "regibazar");
        var account = await SeedAccountAsync(ctx, "Cuenta", phone: "8671234567");
        await SeedClientAsync(
            ctx,
            business.Id,
            "Lupita",
            phone: "8671234567",
            address: "Calle Secreta 123, Colonia Privada");

        var service = new ClientClaimService(ctx);
        var candidates = await service.FindClaimCandidatesByPhoneAsync(account.Id);

        Assert.Single(candidates);
        var c = candidates[0];
        Assert.Equal("Lupita", c.ClientName);
        // Datos sensibles NO se exponen en el DTO de candidato
        Assert.Null(GetDtoProperty<string?>(c, "Phone"));
        Assert.Null(GetDtoProperty<string?>(c, "Address"));
    }

    [Fact]
    public async Task ListClaimedClients_OnlyReturnsClaimsForThisAccount()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedBusinessAsync(ctx, "Regi Bazar", "regibazar");

        var accountA = await SeedAccountAsync(ctx, "Cuenta A", phone: "8671111111");
        var accountB = await SeedAccountAsync(ctx, "Cuenta B", phone: "8672222222");

        var clientA = await SeedClientAsync(ctx, business.Id, "Clienta A", phone: "8671111111");
        var clientB = await SeedClientAsync(ctx, business.Id, "Clienta B", phone: "8672222222");
        clientA.AccountId = accountA.Id;
        clientB.AccountId = accountB.Id;
        await ctx.SaveChangesAsync();

        ctx.ClientClaimAudits.Add(new ClientClaimAudit
        {
            AccountId = accountA.Id, ClientId = clientA.Id, BusinessId = business.Id,
            Mode = ClientClaimMode.PhoneMatch, Reason = "test", ClaimedAt = DateTime.UtcNow
        });
        ctx.ClientClaimAudits.Add(new ClientClaimAudit
        {
            AccountId = accountB.Id, ClientId = clientB.Id, BusinessId = business.Id,
            Mode = ClientClaimMode.PhoneMatch, Reason = "test", ClaimedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        var service = new ClientClaimService(ctx);
        var claimedByA = await service.ListClaimedClientsAsync(accountA.Id);
        var claimedByB = await service.ListClaimedClientsAsync(accountB.Id);

        Assert.Single(claimedByA);
        Assert.Equal(clientA.Id, claimedByA[0].ClientId);
        Assert.DoesNotContain(claimedByA, c => c.ClientId == clientB.Id);

        Assert.Single(claimedByB);
        Assert.Equal(clientB.Id, claimedByB[0].ClientId);
        Assert.DoesNotContain(claimedByB, c => c.ClientId == clientA.Id);
    }

    [Fact]
    public async Task ClaimByOrderToken_IsIdempotent()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = await SeedBusinessAsync(ctx, "Regi Bazar", "regibazar");
        var account = await SeedAccountAsync(ctx, "Lupita", phone: "8671234567");
        var client = await SeedClientAsync(ctx, business.Id, "Lupita Pérez", phone: "8671234567");
        var order = await SeedOrderAsync(ctx, business.Id, client.Id, "TOKEN-LUPITA-IDEMP");

        var service = new ClientClaimService(ctx);
        var first = await service.ClaimByOrderTokenAsync(account.Id, order.AccessToken);
        var second = await service.ClaimByOrderTokenAsync(account.Id, order.AccessToken);

        Assert.Equal(ClaimStatus.Linked, first.Status);
        Assert.Equal(ClaimStatus.Linked, second.Status);
        Assert.Equal(account.Id, client.AccountId);
        Assert.Single(await ctx.ClientClaimAudits.ToListAsync());
    }

    // ── Helpers ──

    private static async Task<Business> SeedBusinessAsync(AppDbContext ctx, string name, string slug)
    {
        var b = new Business
        {
            Name = name,
            Slug = slug,
            City = "Nuevo Laredo",
            FrontendUrl = "https://example.com",
            DepotLat = 27.4861,
            DepotLng = -99.5069,
            GeocodingRegion = "Nuevo Laredo, Tamaulipas, MX",
            GeminiBusinessName = name,
            PlanTier = "Elite",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Businesses.Add(b);
        await ctx.SaveChangesAsync();
        return b;
    }

    private static async Task<Account> SeedAccountAsync(AppDbContext ctx, string name, string? phone)
    {
        var a = new Account
        {
            DisplayName = name,
            Phone = phone,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Accounts.Add(a);
        await ctx.SaveChangesAsync();
        return a;
    }

    private static async Task<Client> SeedClientAsync(
        AppDbContext ctx, int businessId, string name, string? phone, string? address = null)
    {
        var c = new Client
        {
            BusinessId = businessId,
            Name = name,
            NormalizedName = TextNormalizer.NormalizeName(name),
            Phone = phone,
            NormalizedPhone = TextNormalizer.NormalizePhone(phone),
            Address = address,
            NormalizedAddress = TextNormalizer.NormalizeAddress(address),
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Clients.Add(c);
        await ctx.SaveChangesAsync();
        return c;
    }

    private static async Task<Order> SeedOrderAsync(
        AppDbContext ctx, int businessId, int clientId, string accessToken)
    {
        var o = new Order
        {
            BusinessId = businessId,
            ClientId = clientId,
            AccessToken = accessToken,
            Status = OrderStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(2),
            Subtotal = 0m,
            Total = 0m,
        };
        ctx.Orders.Add(o);
        await ctx.SaveChangesAsync();
        return o;
    }

    private static T? GetDtoProperty<T>(object dto, string propertyName)
    {
        var prop = dto.GetType().GetProperty(propertyName);
        if (prop is null) return default;
        return (T?)prop.GetValue(dto);
    }
}

internal static class TestDbContextFactory
{
    public static AppDbContext Create()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid():N}")
            .Options;
        return new AppDbContext(options);
    }
}
