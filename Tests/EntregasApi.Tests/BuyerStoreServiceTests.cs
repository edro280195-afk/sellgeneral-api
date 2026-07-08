using EntregasApi.Models;
using EntregasApi.Services;
using Xunit;

namespace EntregasApi.Tests;

public class BuyerStoreServiceTests
{
    [Fact]
    public async Task GetStore_WithNonexistentBusiness_ThrowsNotFound()
    {
        using var ctx = TestDbContextFactory.Create();
        var account = new Account { DisplayName = "X", Phone = "8000000000" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<StoreNotFoundException>(
            () => new BuyerStoreService(ctx).GetStoreAsync(account.Id, 9999));
    }

    [Fact]
    public async Task GetStore_WithNoClaimedClientInBusiness_ThrowsNotFound()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Sofía", Phone = "8681452290" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        // No creamos Client en este Business.

        await Assert.ThrowsAsync<StoreNotFoundException>(
            () => new BuyerStoreService(ctx).GetStoreAsync(account.Id, business.Id));
    }

    [Fact]
    public async Task GetStore_FollowerWithoutClient_CanViewStore()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Sofía", Phone = "8681452290" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        // Sigue la tienda pero nunca le ha comprado (sin Client).
        ctx.StoreFollowers.Add(new StoreFollower { BusinessId = business.Id, AccountId = account.Id });
        await ctx.SaveChangesAsync();

        var store = await new BuyerStoreService(ctx).GetStoreAsync(account.Id, business.Id);

        Assert.True(store.IsFollowing);
        Assert.Equal(1, store.FollowerCount);
        Assert.Equal(0, store.Points.CurrentPoints);
    }

    [Fact]
    public async Task GetStore_WithActiveLiveAnnouncement_ReportsIsLiveNow()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();
        ctx.StoreFollowers.Add(new StoreFollower { BusinessId = business.Id, AccountId = account.Id });
        ctx.LiveAnnouncements.Add(new LiveAnnouncement
        {
            BusinessId = business.Id,
            Title = "Rebajas",
            StartedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var store = await new BuyerStoreService(ctx).GetStoreAsync(account.Id, business.Id);

        Assert.True(store.IsLiveNow);
        Assert.Equal("Rebajas", store.LiveAnnouncementTitle);
    }

    [Fact]
    public async Task GetStore_HappyPath_ReturnsHeaderAndPoints()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        ctx.Clients.Add(new Client
        {
            BusinessId = business.Id,
            AccountId = account.Id,
            Name = "Ana",
            NormalizedName = "ana",
            CurrentPoints = 320,
        });
        await ctx.SaveChangesAsync();

        var store = await new BuyerStoreService(ctx).GetStoreAsync(account.Id, business.Id);

        Assert.Equal("Regi Bazar", store.Name);
        Assert.Equal("#FF0072", store.BrandPrimaryColor);
        Assert.Equal(320, store.Points.CurrentPoints);
        Assert.Null(store.Points.NextRewardAt); // sin rewards
        Assert.Null(store.Live);
        Assert.Empty(store.Products);
        Assert.Equal(0, store.ActiveTandasCount);
        Assert.Equal(0, store.ActiveRafflesCount);
        Assert.Equal(1, store.ClientCount);
    }

    [Fact]
    public async Task GetStore_NextRewardAt_ReturnsLowestActiveRewardCost()
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
            NormalizedName = "ana", CurrentPoints = 50,
        });
        ctx.LoyaltyRewards.AddRange(
            new LoyaltyReward
            {
                BusinessId = business.Id, Name = "R1", PointsCost = 500,
                Type = LoyaltyRewardType.FixedDiscount, Value = 50m, IsActive = true,
                SortOrder = 2,
            },
            new LoyaltyReward
            {
                BusinessId = business.Id, Name = "R2", PointsCost = 100,
                Type = LoyaltyRewardType.FixedDiscount, Value = 10m, IsActive = true,
                SortOrder = 1,
            },
            new LoyaltyReward
            {
                BusinessId = business.Id, Name = "R3-off", PointsCost = 1000,
                Type = LoyaltyRewardType.FixedDiscount, Value = 0m, IsActive = false,
                SortOrder = 3,
            });
        await ctx.SaveChangesAsync();

        var store = await new BuyerStoreService(ctx).GetStoreAsync(account.Id, business.Id);

        Assert.Equal(100, store.Points.NextRewardAt); // la más barata activa
    }

    [Fact]
    public async Task GetStore_WithActiveLive_ReturnsLiveSummary()
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

        var live = new LiveSession
        {
            BusinessId = business.Id,
            FacebookUrl = "https://facebook.com/live/123",
            Title = "Live de sartenes",
            Status = LiveSessionStatus.Ready,
            ImportedAt = DateTime.UtcNow.AddDays(-1),
            ProcessedAt = DateTime.UtcNow,
        };
        ctx.LiveSessions.Add(live);
        await ctx.SaveChangesAsync();

        ctx.LiveProducts.AddRange(
            new LiveProduct
            {
                BusinessId = business.Id, LiveSessionId = live.Id,
                Keyword = "sartén", Description = "Sartén 26cm", Price = 590m,
                AnnouncedAtSeconds = 120,
            },
            new LiveProduct
            {
                BusinessId = business.Id, LiveSessionId = live.Id,
                Keyword = "blusa", Description = "Blusa coqueta", Price = 249m,
                AnnouncedAtSeconds = 60,
            });
        await ctx.SaveChangesAsync();

        var store = await new BuyerStoreService(ctx).GetStoreAsync(account.Id, business.Id);

        Assert.NotNull(store.Live);
        Assert.Equal("Live de sartenes", store.Live!.Title);
        Assert.Equal(live.Id, store.Live.SessionId);
        Assert.Equal(0, store.Live.ViewerCount); // stub
        Assert.Contains("blusa", store.Live.Topics ?? "");
        Assert.Contains("sartén", store.Live.Topics ?? "");
    }

    [Fact]
    public async Task GetStore_OnlyReadyLivesAreReturned()
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
        ctx.LiveSessions.Add(new LiveSession
        {
            BusinessId = business.Id,
            FacebookUrl = "https://fb.com/queued",
            Status = LiveSessionStatus.Queued, // aún no publicado
            ImportedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var store = await new BuyerStoreService(ctx).GetStoreAsync(account.Id, business.Id);

        Assert.Null(store.Live);
    }

    [Fact]
    public async Task GetStore_WithActiveProducts_ReturnsTop24()
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
        for (var i = 0; i < 30; i++)
        {
            ctx.Products.Add(new Product
            {
                BusinessId = business.Id,
                SKU = $"SKU-{i:000}",
                Name = $"Producto {i}",
                Price = 100m + i,
                Stock = 10,
                IsActive = i % 5 != 0, // algunos inactivos
            });
        }
        await ctx.SaveChangesAsync();

        var store = await new BuyerStoreService(ctx).GetStoreAsync(account.Id, business.Id);

        Assert.Equal(24, store.Products.Count); // cap a 24
        Assert.All(store.Products, p => Assert.True(p.Stock > 0 || p.Stock == 10));
    }

    [Fact]
    public async Task GetStore_CountsActiveTandasAndRaffles()
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
        var product = new TandaProduct { BusinessId = business.Id, Name = "P", BasePrice = 100m };
        ctx.TandaProducts.Add(product);
        await ctx.SaveChangesAsync();

        ctx.Tandas.AddRange(
            new Tanda
            {
                BusinessId = business.Id, ProductId = product.Id,
                Name = "T1", TotalWeeks = 10, WeeklyAmount = 100m,
                StartDate = DateTime.UtcNow.Date,
                Status = "Active", AccessToken = "t1",
            },
            new Tanda
            {
                BusinessId = business.Id, ProductId = product.Id,
                Name = "T2", TotalWeeks = 10, WeeklyAmount = 100m,
                StartDate = DateTime.UtcNow.Date,
                Status = "Draft", AccessToken = "t2",
            },
            new Tanda
            {
                BusinessId = business.Id, ProductId = product.Id,
                Name = "T3-old", TotalWeeks = 10, WeeklyAmount = 100m,
                StartDate = DateTime.UtcNow.Date,
                Status = "Completed", AccessToken = "t3",
            });
        ctx.Raffles.AddRange(
            new Raffle
            {
                BusinessId = business.Id, Name = "R1", Status = "Active",
                RaffleDate = DateTime.UtcNow.AddDays(7),
                AnimationType = "roulette", PrizeType = "product",
                EligibilityRule = "purchaseCount",
                ClientSegmentFilter = "all", SocialTemplate = "default",
            },
            new Raffle
            {
                BusinessId = business.Id, Name = "R2-done", Status = "Completed",
                RaffleDate = DateTime.UtcNow.AddDays(-1),
                AnimationType = "roulette", PrizeType = "product",
                EligibilityRule = "purchaseCount",
                ClientSegmentFilter = "all", SocialTemplate = "default",
            });
        await ctx.SaveChangesAsync();

        var store = await new BuyerStoreService(ctx).GetStoreAsync(account.Id, business.Id);

        Assert.Equal(2, store.ActiveTandasCount); // Active + Draft
        Assert.Equal(1, store.ActiveRafflesCount); // solo Active
    }

    [Fact]
    public async Task GetStore_DoesNotLeakOtherAccountsData()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var mine = new Account { DisplayName = "Mía", Phone = "8681452290" };
        var other = new Account { DisplayName = "Otra", Phone = "8682223344" };
        ctx.Accounts.AddRange(mine, other);
        await ctx.SaveChangesAsync();

        // Solo "Otra" tiene Client en esta tienda.
        ctx.Clients.Add(new Client
        {
            BusinessId = business.Id, AccountId = other.Id, Name = "Otra",
            NormalizedName = "otra", CurrentPoints = 999,
        });
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<StoreNotFoundException>(
            () => new BuyerStoreService(ctx).GetStoreAsync(mine.Id, business.Id));
    }

    [Fact]
    public async Task GetStore_IsVerifiedTrue_WhenLogoAndAccentColorPresent()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        business.LogoUrl = "https://cdn.example.com/logo.png";
        business.BrandAccentColor = "#FFD700";
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Ana", Phone = "8000000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        ctx.Clients.Add(new Client
        {
            BusinessId = business.Id, AccountId = account.Id, Name = "Ana",
            NormalizedName = "ana",
        });
        await ctx.SaveChangesAsync();

        var store = await new BuyerStoreService(ctx).GetStoreAsync(account.Id, business.Id);

        Assert.True(store.IsVerified);
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
}
