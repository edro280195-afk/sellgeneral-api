using EntregasApi.Models;
using EntregasApi.Services;
using Xunit;

namespace EntregasApi.Tests;

public class BuyerTandasServiceTests
{
    [Fact]
    public async Task GetMyTandas_WithNoClaimedClients_ReturnsEmpty()
    {
        using var ctx = TestDbContextFactory.Create();
        var account = new Account { DisplayName = "Nueva", Phone = "8000000000" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var result = await new BuyerTandasService(ctx).GetMyTandasAsync(account.Id);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetMyTandas_WithClaimsButNoTandas_ReturnsEmpty()
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
            NormalizedName = "sofia",
        });
        await ctx.SaveChangesAsync();

        var result = await new BuyerTandasService(ctx).GetMyTandasAsync(account.Id);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetMyTandas_AvailableTandaWithoutParticipation_ReturnsIsMineFalse()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
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

        var product = new TandaProduct
        {
            BusinessId = business.Id,
            Name = "Aire acondicionado",
            BasePrice = 10000m,
        };
        ctx.TandaProducts.Add(product);
        await ctx.SaveChangesAsync();

        ctx.Tandas.Add(NewTanda(business.Id, product.Id, "Plan frío", 10, 100m,
            DateTime.UtcNow.Date.AddDays(-7)));
        await ctx.SaveChangesAsync();

        var result = await new BuyerTandasService(ctx).GetMyTandasAsync(account.Id);

        var entry = Assert.Single(result);
        Assert.Equal("Plan frío", entry.Name);
        Assert.Equal("Aire acondicionado", entry.ProductName);
        Assert.False(entry.IsMine);
        Assert.Null(entry.MyTurn);
        Assert.Null(entry.HasPaidThisWeek);
        Assert.Null(entry.AmIThisWeekWinner);
        Assert.Empty(entry.PaidWeeks);
    }

    [Fact]
    public async Task GetMyTandas_ParticipationWithPayment_MarksHasPaidAndWinner()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
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

        var product = new TandaProduct
        {
            BusinessId = business.Id,
            Name = "Refrigerador",
            BasePrice = 15000m,
        };
        ctx.TandaProducts.Add(product);
        await ctx.SaveChangesAsync();

        var tanda = NewTanda(business.Id, product.Id, "Plan semanal", 10, 200m,
            DateTime.UtcNow.Date.AddDays(-7));
        ctx.Tandas.Add(tanda);
        await ctx.SaveChangesAsync();

        var participant = new TandaParticipant
        {
            BusinessId = business.Id,
            TandaId = tanda.Id,
            CustomerId = client.Id,
            AssignedTurn = 2,
            Status = "Active",
        };
        ctx.TandaParticipants.Add(participant);
        await ctx.SaveChangesAsync();

        ctx.TandaPayments.Add(new TandaPayment
        {
            BusinessId = business.Id,
            ParticipantId = participant.Id,
            WeekNumber = 2,
            AmountPaid = 200m,
            IsVerified = true,
            PaymentDate = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var result = await new BuyerTandasService(ctx).GetMyTandasAsync(account.Id);

        var entry = Assert.Single(result);
        Assert.True(entry.IsMine);
        Assert.Equal(2, entry.MyTurn);
        Assert.True(entry.HasPaidThisWeek);
        Assert.True(entry.AmIThisWeekWinner);
        Assert.Equal(new List<int> { 2 }, entry.PaidWeeks);
    }

    [Fact]
    public async Task GetMyTandas_UnverifiedPaymentDoesNotMarkHasPaid()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
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

        var product = new TandaProduct { BusinessId = business.Id, Name = "P", BasePrice = 100m };
        ctx.TandaProducts.Add(product);
        await ctx.SaveChangesAsync();

        var tanda = NewTanda(business.Id, product.Id, "Plan X", 10, 200m,
            DateTime.UtcNow.Date.AddDays(-7));
        ctx.Tandas.Add(tanda);
        await ctx.SaveChangesAsync();

        var participant = new TandaParticipant
        {
            BusinessId = business.Id,
            TandaId = tanda.Id,
            CustomerId = client.Id,
            AssignedTurn = 2,
        };
        ctx.TandaParticipants.Add(participant);
        await ctx.SaveChangesAsync();

        ctx.TandaPayments.Add(new TandaPayment
        {
            BusinessId = business.Id,
            ParticipantId = participant.Id,
            WeekNumber = 2,
            AmountPaid = 200m,
            IsVerified = false, // aún no verificado
            PaymentDate = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var result = await new BuyerTandasService(ctx).GetMyTandasAsync(account.Id);

        Assert.False(result[0].HasPaidThisWeek);
        Assert.Empty(result[0].PaidWeeks);
    }

    [Fact]
    public async Task GetMyTandas_NotThisWeekWinner_WhenAssignedTurnDifferentFromCurrent()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
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

        var product = new TandaProduct { BusinessId = business.Id, Name = "P", BasePrice = 100m };
        ctx.TandaProducts.Add(product);
        await ctx.SaveChangesAsync();

        // Tanda que empezó hace 7 días → CurrentWeek = 2
        var tanda = NewTanda(business.Id, product.Id, "Plan Y", 10, 100m,
            DateTime.UtcNow.Date.AddDays(-7));
        ctx.Tandas.Add(tanda);
        await ctx.SaveChangesAsync();

        // AssignedTurn = 5 → no soy ganador esta semana
        ctx.TandaParticipants.Add(new TandaParticipant
        {
            BusinessId = business.Id,
            TandaId = tanda.Id,
            CustomerId = client.Id,
            AssignedTurn = 5,
        });
        await ctx.SaveChangesAsync();

        var result = await new BuyerTandasService(ctx).GetMyTandasAsync(account.Id);

        Assert.Equal(2, result[0].CurrentWeek);
        Assert.Equal(5, result[0].MyTurn);
        Assert.False(result[0].AmIThisWeekWinner);
    }

    [Fact]
    public async Task GetMyTandas_DoesNotLeakOtherAccountsData()
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
            BusinessId = business.Id,
            AccountId = other.Id,
            Name = "Otra",
            NormalizedName = "otra",
        };
        ctx.Clients.Add(otherClient);
        await ctx.SaveChangesAsync();

        var product = new TandaProduct { BusinessId = business.Id, Name = "P", BasePrice = 100m };
        ctx.TandaProducts.Add(product);
        await ctx.SaveChangesAsync();

        ctx.Tandas.Add(NewTanda(business.Id, product.Id, "Secreta", 10, 100m,
            DateTime.UtcNow.Date.AddDays(-7)));
        await ctx.SaveChangesAsync();

        var result = await new BuyerTandasService(ctx).GetMyTandasAsync(mine.Id);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetMyTandas_CancelledTandaIsNotIncluded()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
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

        var product = new TandaProduct { BusinessId = business.Id, Name = "P", BasePrice = 100m };
        ctx.TandaProducts.Add(product);
        await ctx.SaveChangesAsync();

        ctx.Tandas.Add(NewTanda(business.Id, product.Id, "Cancelada", 10, 100m,
            DateTime.UtcNow.Date.AddDays(-14), status: "Cancelled"));
        await ctx.SaveChangesAsync();

        var result = await new BuyerTandasService(ctx).GetMyTandasAsync(account.Id);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetMyTandas_FallsBackToDefaultColor_WhenBusinessColorMissing()
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
        var product = new TandaProduct { BusinessId = business.Id, Name = "P", BasePrice = 50m };
        ctx.TandaProducts.Add(product);
        await ctx.SaveChangesAsync();
        ctx.Tandas.Add(NewTanda(business.Id, product.Id, "X", 10, 50m,
            DateTime.UtcNow.Date.AddDays(-7)));
        await ctx.SaveChangesAsync();

        var result = await new BuyerTandasService(ctx).GetMyTandasAsync(account.Id);

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

    private static Tanda NewTanda(
        int businessId,
        Guid productId,
        string name,
        int totalWeeks,
        decimal weeklyAmount,
        DateTime startDate,
        string status = "Active") =>
        new()
        {
            BusinessId = businessId,
            ProductId = productId,
            Name = name,
            TotalWeeks = totalWeeks,
            WeeklyAmount = weeklyAmount,
            StartDate = startDate,
            Status = status,
            AccessToken = $"tok-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
        };
}
