using EntregasApi.Models;
using EntregasApi.Services;
using Xunit;

namespace EntregasApi.Tests;

public class BuyerRewardsServiceTests
{
    [Fact]
    public async Task GetRewards_WithNoClaimedClients_ReturnsEmptyList()
    {
        using var ctx = TestDbContextFactory.Create();
        var account = new Account { DisplayName = "Nueva", Phone = "8000000000" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var result = await new BuyerRewardsService(ctx).GetRewardsAsync(account.Id);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetRewards_WithClaimsButNoRewards_ReturnsStoreWithEmptyList()
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
            CurrentPoints = 150,
        });
        await ctx.SaveChangesAsync();

        var result = await new BuyerRewardsService(ctx).GetRewardsAsync(account.Id);

        var entry = Assert.Single(result);
        Assert.Equal(business.Id, entry.BusinessId);
        Assert.Equal("Regi Bazar", entry.BusinessName);
        Assert.Equal("#FF0072", entry.BrandPrimaryColor);
        Assert.Equal(150, entry.StorePoints);
        Assert.Empty(entry.Rewards);
    }

    [Fact]
    public async Task GetRewards_GroupsActiveRewardsByBusiness()
    {
        using var ctx = TestDbContextFactory.Create();
        var bizA = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        var bizB = NewBusiness("Luna Bella", "luna", "#FF7A59");
        ctx.Businesses.AddRange(bizA, bizB);
        var account = new Account { DisplayName = "Multi", Phone = "8680000004" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var clientA = new Client
        {
            BusinessId = bizA.Id, AccountId = account.Id, Name = "Multi A",
            NormalizedName = "multi a", CurrentPoints = 320,
        };
        var clientB = new Client
        {
            BusinessId = bizB.Id, AccountId = account.Id, Name = "Multi B",
            NormalizedName = "multi b", CurrentPoints = 80,
        };
        ctx.Clients.AddRange(clientA, clientB);
        await ctx.SaveChangesAsync();

        ctx.LoyaltyRewards.AddRange(
            NewReward(bizA.Id, "$50 off", 100, LoyaltyRewardType.FixedDiscount, 50m, "💸", 1, true),
            NewReward(bizA.Id, "Envío gratis", 150, LoyaltyRewardType.FreeShipping, 0m, "🚚", 2, true),
            NewReward(bizA.Id, "Premio oculto", 999, LoyaltyRewardType.FixedDiscount, 0m, "X", 3, false),
            NewReward(bizB.Id, "Regalito", 200, LoyaltyRewardType.Gift, 0m, "🎁", 1, true));
        await ctx.SaveChangesAsync();

        var result = await new BuyerRewardsService(ctx).GetRewardsAsync(account.Id);

        Assert.Equal(2, result.Count);

        var first = result[0];
        Assert.Equal(bizA.Id, first.BusinessId);
        Assert.Equal("Regi Bazar", first.BusinessName);
        Assert.Equal(320, first.StorePoints);
        Assert.Equal(2, first.Rewards.Count);
        Assert.Equal("$50 off", first.Rewards[0].Name);
        Assert.Equal("FixedDiscount", first.Rewards[0].Type);
        Assert.Equal("💸", first.Rewards[0].Icon);
        Assert.Equal(100, first.Rewards[0].PointsCost);
        Assert.Equal("Envío gratis", first.Rewards[1].Name);
        Assert.DoesNotContain(first.Rewards, r => r.Name == "Premio oculto");

        var second = result[1];
        Assert.Equal(bizB.Id, second.BusinessId);
        Assert.Equal(80, second.StorePoints);
        Assert.Single(second.Rewards);
        Assert.Equal("Regalito", second.Rewards[0].Name);
    }

    [Fact]
    public async Task GetRewards_OrdersBusinessesByPointsDescending()
    {
        using var ctx = TestDbContextFactory.Create();
        var bizA = NewBusiness("A", "a", "#111111");
        var bizB = NewBusiness("B", "b", "#222222");
        var bizC = NewBusiness("C", "c", "#333333");
        ctx.Businesses.AddRange(bizA, bizB, bizC);
        var account = new Account { DisplayName = "Orden", Phone = "8680000005" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        ctx.Clients.AddRange(
            NewClient(bizA.Id, account.Id, 50),
            NewClient(bizB.Id, account.Id, 500),
            NewClient(bizC.Id, account.Id, 200));
        await ctx.SaveChangesAsync();

        var result = await new BuyerRewardsService(ctx).GetRewardsAsync(account.Id);

        Assert.Equal(new[] { bizB.Id, bizC.Id, bizA.Id }, result.Select(r => r.BusinessId).ToArray());
    }

    [Fact]
    public async Task GetRewards_DoesNotLeakOtherAccountsData()
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
            NormalizedName = "otra", CurrentPoints = 999,
        };
        ctx.Clients.Add(otherClient);
        ctx.LoyaltyRewards.Add(NewReward(
            business.Id, "Premio secreto", 100,
            LoyaltyRewardType.FixedDiscount, 50m, "💰", 1, true));
        await ctx.SaveChangesAsync();

        var result = await new BuyerRewardsService(ctx).GetRewardsAsync(mine.Id);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetRewards_FallsBackToDefaultColor_WhenBusinessColorMissing()
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
            NormalizedName = "x", CurrentPoints = 10,
        });
        await ctx.SaveChangesAsync();

        var result = await new BuyerRewardsService(ctx).GetRewardsAsync(account.Id);

        var entry = Assert.Single(result);
        Assert.Equal("#FB6F9C", entry.BrandPrimaryColor);
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

    private static Client NewClient(int businessId, int accountId, int points) => new()
    {
        BusinessId = businessId,
        AccountId = accountId,
        Name = "C",
        NormalizedName = $"c-{businessId}-{accountId}",
        CurrentPoints = points,
    };

    private static LoyaltyReward NewReward(
        int businessId,
        string name,
        int pointsCost,
        LoyaltyRewardType type,
        decimal value,
        string icon,
        int sortOrder,
        bool isActive) =>
        new()
        {
            BusinessId = businessId,
            Name = name,
            PointsCost = pointsCost,
            Type = type,
            Value = value,
            Icon = icon,
            SortOrder = sortOrder,
            IsActive = isActive,
        };
}
