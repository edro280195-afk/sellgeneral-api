using EntregasApi.Models;
using EntregasApi.Services;
using Xunit;

namespace EntregasApi.Tests;

public class BuyerRafflesServiceTests
{
    [Fact]
    public async Task GetMyRaffles_WithNoClaimedClients_ReturnsEmpty()
    {
        using var ctx = TestDbContextFactory.Create();
        var account = new Account { DisplayName = "Nueva", Phone = "8000000000" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var result = await new BuyerRafflesService(ctx).GetMyRafflesAsync(account.Id);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetMyRaffles_WithClaimsButNoRaffles_ReturnsEmpty()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Sofía", Phone = "8681452290" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        ctx.Clients.Add(new Client
        {
            BusinessId = business.Id, AccountId = account.Id, Name = "Sofía",
            NormalizedName = "sofia",
        });
        await ctx.SaveChangesAsync();

        var result = await new BuyerRafflesService(ctx).GetMyRafflesAsync(account.Id);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetMyRaffles_ActiveRaffleWithoutEntries_ReturnsZeroAndNotMine()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var client = new Client
        {
            BusinessId = business.Id, AccountId = account.Id, Name = "Ana",
            NormalizedName = "ana",
        };
        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();

        ctx.Raffles.Add(NewRaffle(business.Id, "Sorteo verano",
            DateTime.UtcNow.AddDays(7), status: "Active"));
        await ctx.SaveChangesAsync();

        var result = await new BuyerRafflesService(ctx).GetMyRafflesAsync(account.Id);

        var entry = Assert.Single(result);
        Assert.Equal("Sorteo verano", entry.Name);
        Assert.Equal(0, entry.MyEntryCount);
        Assert.False(entry.IsMineEntered);
        Assert.False(entry.AmIWinner);
    }

    [Fact]
    public async Task GetMyRaffles_RaffleWithMyEntries_CountsBoletos()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var client = new Client
        {
            BusinessId = business.Id, AccountId = account.Id, Name = "Ana",
            NormalizedName = "ana",
        };
        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();

        var raffle = NewRaffle(business.Id, "Aniversario",
            DateTime.UtcNow.AddDays(3), status: "Active");
        ctx.Raffles.Add(raffle);
        await ctx.SaveChangesAsync();

        // Tres órdenes entregadas → tres entradas
        for (var i = 0; i < 3; i++)
        {
            var order = new Order
            {
                BusinessId = business.Id,
                ClientId = client.Id,
                AccessToken = $"tok-{i}",
                Status = OrderStatus.Delivered,
                Total = 100m,
                CreatedAt = DateTime.UtcNow.AddDays(-i),
                ExpiresAt = DateTime.UtcNow.AddDays(3),
            };
            ctx.Orders.Add(order);
        }
        await ctx.SaveChangesAsync();

        var orders = ctx.Orders.ToList();
        ctx.RaffleEntries.AddRange(orders.Select(o => new RaffleEntry
        {
            BusinessId = business.Id,
            RaffleId = raffle.Id,
            ClientId = client.Id,
            OrderId = o.Id,
        }));
        await ctx.SaveChangesAsync();

        var result = await new BuyerRafflesService(ctx).GetMyRafflesAsync(account.Id);

        var entry = Assert.Single(result);
        Assert.Equal(3, entry.MyEntryCount);
        Assert.True(entry.IsMineEntered);
    }

    [Fact]
    public async Task GetMyRaffles_CompletedRaffleWhereAmIWinner_MarksWinner()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var client = new Client
        {
            BusinessId = business.Id, AccountId = account.Id, Name = "Ana",
            NormalizedName = "ana",
        };
        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();

        var raffle = NewRaffle(business.Id, "Gran premio",
            DateTime.UtcNow.AddDays(-1), status: "Completed",
            winnerId: client.Id, announcedAt: DateTime.UtcNow.AddDays(-1));
        ctx.Raffles.Add(raffle);
        await ctx.SaveChangesAsync();

        ctx.RaffleParticipants.Add(new RaffleParticipant
        {
            BusinessId = business.Id,
            RaffleId = raffle.Id,
            ClientId = client.Id,
            IsWinner = true,
        });
        await ctx.SaveChangesAsync();

        var result = await new BuyerRafflesService(ctx).GetMyRafflesAsync(account.Id);

        var entry = Assert.Single(result);
        Assert.Equal("Completed", entry.Status);
        Assert.True(entry.AmIWinner);
        Assert.NotNull(entry.AnnouncedAt);
    }

    [Fact]
    public async Task GetMyRaffles_DraftRaffleIsNotIncluded()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var client = new Client
        {
            BusinessId = business.Id, AccountId = account.Id, Name = "Ana",
            NormalizedName = "ana",
        };
        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();

        ctx.Raffles.Add(NewRaffle(business.Id, "Borrador",
            DateTime.UtcNow.AddDays(7), status: "Draft"));
        await ctx.SaveChangesAsync();

        var result = await new BuyerRafflesService(ctx).GetMyRafflesAsync(account.Id);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetMyRaffles_DoesNotLeakOtherAccountsData()
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
        await ctx.SaveChangesAsync();

        ctx.Raffles.Add(NewRaffle(business.Id, "Secreto",
            DateTime.UtcNow.AddDays(7), status: "Active"));
        await ctx.SaveChangesAsync();

        var result = await new BuyerRafflesService(ctx).GetMyRafflesAsync(mine.Id);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetMyRaffles_FallsBackToDefaultColor_WhenBusinessColorMissing()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = new Business
        {
            Name = "Sin color",
            Slug = "sinc",
            City = "NL",
            FrontendUrl = "https://example.com",
            BrandPrimaryColor = "",
            DepotLat = 0,
            DepotLng = 0,
            GeocodingRegion = "T, MX",
            GeminiBusinessName = "Sin color",
            PlanTier = "Elite",
            SubscriptionStatus = SubscriptionStatus.Active,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "X", Phone = "8000000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        ctx.Clients.Add(new Client
        {
            BusinessId = business.Id, AccountId = account.Id, Name = "X",
            NormalizedName = "x",
        });
        await ctx.SaveChangesAsync();
        ctx.Raffles.Add(NewRaffle(business.Id, "X",
            DateTime.UtcNow.AddDays(7), status: "Active"));
        await ctx.SaveChangesAsync();

        var result = await new BuyerRafflesService(ctx).GetMyRafflesAsync(account.Id);

        Assert.Equal("#FB6F9C", result[0].BrandPrimaryColor);
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

    private static Raffle NewRaffle(
        int businessId,
        string name,
        DateTime raffleDate,
        string status = "Active",
        int? winnerId = null,
        DateTime? announcedAt = null) =>
        new()
        {
            BusinessId = businessId,
            Name = name,
            RaffleDate = raffleDate,
            Status = status,
            WinnerId = winnerId,
            AnnouncedAt = announcedAt,
            AnimationType = "roulette",
            PrizeType = "product",
            EligibilityRule = "purchaseCount",
            ClientSegmentFilter = "all",
            SocialTemplate = "default",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
}
