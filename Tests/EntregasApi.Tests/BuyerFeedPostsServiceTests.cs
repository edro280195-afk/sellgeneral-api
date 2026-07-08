using EntregasApi.Data;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntregasApi.Tests;

public class BuyerFeedPostsServiceTests
{
    [Fact]
    public async Task GetStorePosts_WithNoAccessToStore_ThrowsNotFound()
    {
        using var ctx = NewContext();
        var business = NewBusiness();
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<StoreNotFoundException>(() =>
            new BuyerFeedPostsService(ctx).GetStorePostsAsync(account.Id, business.Id, 1, 20));
    }

    [Fact]
    public async Task GetStorePosts_FollowerWithoutClient_SeesNonVipPosts()
    {
        using var ctx = NewContext();
        var business = NewBusiness();
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();
        ctx.StoreFollowers.Add(new StoreFollower { BusinessId = business.Id, AccountId = account.Id });
        ctx.StorePosts.Add(new StorePost { BusinessId = business.Id, Body = "Novedad pública" });
        await ctx.SaveChangesAsync();

        var posts = await new BuyerFeedPostsService(ctx).GetStorePostsAsync(account.Id, business.Id, 1, 20);

        var single = Assert.Single(posts);
        Assert.Equal("Novedad pública", single.Body);
        Assert.False(single.IsLocked);
    }

    [Fact]
    public async Task GetStorePosts_VipOnlyPost_LockedForNonVipFollower()
    {
        using var ctx = NewContext();
        var business = NewBusiness();
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();
        ctx.StoreFollowers.Add(new StoreFollower { BusinessId = business.Id, AccountId = account.Id, IsVip = false });
        ctx.StorePosts.Add(new StorePost { BusinessId = business.Id, Body = "Secreto VIP", IsVipOnly = true });
        await ctx.SaveChangesAsync();

        var posts = await new BuyerFeedPostsService(ctx).GetStorePostsAsync(account.Id, business.Id, 1, 20);

        var single = Assert.Single(posts);
        Assert.True(single.IsLocked);
        Assert.Equal("", single.Body);
    }

    [Fact]
    public async Task GetStorePosts_VipOnlyPost_VisibleForVipFollower()
    {
        using var ctx = NewContext();
        var business = NewBusiness();
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();
        ctx.StoreFollowers.Add(new StoreFollower { BusinessId = business.Id, AccountId = account.Id, IsVip = true });
        ctx.StorePosts.Add(new StorePost { BusinessId = business.Id, Body = "Secreto VIP", IsVipOnly = true });
        await ctx.SaveChangesAsync();

        var posts = await new BuyerFeedPostsService(ctx).GetStorePostsAsync(account.Id, business.Id, 1, 20);

        var single = Assert.Single(posts);
        Assert.False(single.IsLocked);
        Assert.Equal("Secreto VIP", single.Body);
    }

    [Fact]
    public async Task GetStorePosts_ExcludesDeleted()
    {
        using var ctx = NewContext();
        var business = NewBusiness();
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();
        ctx.StoreFollowers.Add(new StoreFollower { BusinessId = business.Id, AccountId = account.Id });
        ctx.StorePosts.Add(new StorePost { BusinessId = business.Id, Body = "Borrada", DeletedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();

        var posts = await new BuyerFeedPostsService(ctx).GetStorePostsAsync(account.Id, business.Id, 1, 20);

        Assert.Empty(posts);
    }

    private static AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid():N}")
            .Options;
        return new AppDbContext(options);
    }

    private static Business NewBusiness() => new()
    {
        Name = "Regi Bazar",
        Slug = "regibazar",
        City = "Nuevo Laredo",
        FrontendUrl = "https://example.com",
        BrandPrimaryColor = "#FF0072",
        DepotLat = 27.4861,
        DepotLng = -99.5069,
    };
}
