using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntregasApi.Tests;

public class BuyerFollowServiceTests
{
    [Fact]
    public async Task Follow_WithNonexistentBusiness_ThrowsNotFound()
    {
        using var ctx = NewContext();
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<FollowNotFoundException>(() =>
            ServiceWith(ctx).FollowAsync(account.Id, businessId: 999, preferences: null, CancellationToken.None));

        Assert.Contains("no encontrada", ex.Message);
    }

    [Fact]
    public async Task Follow_CreatesNewFollow_WithDefaultPreferences()
    {
        using var ctx = NewContext();
        var business = NewBusiness();
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Businesses.Add(business);
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var state = await ServiceWith(ctx).FollowAsync(account.Id, business.Id, null, CancellationToken.None);

        Assert.True(state.IsFollowing);
        Assert.True(state.NotifyOnPost);
        Assert.True(state.NotifyOnLive);
        Assert.False(state.IsVip);

        var stored = await ctx.StoreFollowers.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(business.Id, stored.BusinessId);
        Assert.Equal(account.Id, stored.AccountId);
    }

    [Fact]
    public async Task Follow_WithCustomPreferences_RespectsThem()
    {
        using var ctx = NewContext();
        var business = NewBusiness();
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Businesses.Add(business);
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var state = await ServiceWith(ctx).FollowAsync(
            account.Id, business.Id,
            new FollowPreferencesRequest(NotifyOnPost: false, NotifyOnLive: true),
            CancellationToken.None);

        Assert.False(state.NotifyOnPost);
        Assert.True(state.NotifyOnLive);
    }

    [Fact]
    public async Task Follow_Twice_IsIdempotent()
    {
        using var ctx = NewContext();
        var business = NewBusiness();
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Businesses.Add(business);
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var service = ServiceWith(ctx);
        await service.FollowAsync(account.Id, business.Id, null, CancellationToken.None);
        var second = await service.FollowAsync(account.Id, business.Id, null, CancellationToken.None);

        Assert.True(second.IsFollowing);
        Assert.Equal(1, await ctx.StoreFollowers.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Unfollow_ThenFollow_Reactivates_AndKeepsVip()
    {
        using var ctx = NewContext();
        var business = NewBusiness();
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Businesses.Add(business);
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var service = ServiceWith(ctx);
        await service.FollowAsync(account.Id, business.Id, null, CancellationToken.None);

        // La vendedora la marca VIP mientras sigue activa.
        var row = await ctx.StoreFollowers.IgnoreQueryFilters().SingleAsync();
        row.IsVip = true;
        row.VipSince = DateTime.UtcNow;
        await ctx.SaveChangesAsync();

        await service.UnfollowAsync(account.Id, business.Id, CancellationToken.None);
        var afterUnfollow = await service.GetStateAsync(account.Id, business.Id, CancellationToken.None);
        Assert.False(afterUnfollow.IsFollowing);

        var reactivated = await service.FollowAsync(account.Id, business.Id, null, CancellationToken.None);
        Assert.True(reactivated.IsFollowing);
        Assert.True(reactivated.IsVip); // conserva el estatus VIP al reactivar
    }

    [Fact]
    public async Task Unfollow_WhenNotFollowing_DoesNotThrow()
    {
        using var ctx = NewContext();
        var business = NewBusiness();
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Businesses.Add(business);
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        await ServiceWith(ctx).UnfollowAsync(account.Id, business.Id, CancellationToken.None);
        // No debe lanzar.
    }

    [Fact]
    public async Task UpdatePreferences_WhenNotFollowing_ThrowsNotFound()
    {
        using var ctx = NewContext();
        var business = NewBusiness();
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Businesses.Add(business);
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<FollowNotFoundException>(() =>
            ServiceWith(ctx).UpdatePreferencesAsync(
                account.Id, business.Id,
                new FollowPreferencesRequest(false, false),
                CancellationToken.None));
    }

    [Fact]
    public async Task UpdatePreferences_WhenFollowing_UpdatesFlags()
    {
        using var ctx = NewContext();
        var business = NewBusiness();
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Businesses.Add(business);
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var service = ServiceWith(ctx);
        await service.FollowAsync(account.Id, business.Id, null, CancellationToken.None);
        var updated = await service.UpdatePreferencesAsync(
            account.Id, business.Id,
            new FollowPreferencesRequest(NotifyOnPost: false, NotifyOnLive: false),
            CancellationToken.None);

        Assert.False(updated.NotifyOnPost);
        Assert.False(updated.NotifyOnLive);
    }

    [Fact]
    public async Task GetState_WhenNeverFollowed_ReturnsDefaultNotFollowing()
    {
        using var ctx = NewContext();
        var business = NewBusiness();
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Businesses.Add(business);
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var state = await ServiceWith(ctx).GetStateAsync(account.Id, business.Id, CancellationToken.None);

        Assert.False(state.IsFollowing);
        Assert.False(state.IsVip);
    }

    [Fact]
    public async Task Follow_IsolatedBetweenAccounts()
    {
        using var ctx = NewContext();
        var business = NewBusiness();
        var accountA = new Account { DisplayName = "Ana", Phone = "8680000001" };
        var accountB = new Account { DisplayName = "Beto", Phone = "8680000002" };
        ctx.Businesses.Add(business);
        ctx.Accounts.AddRange(accountA, accountB);
        await ctx.SaveChangesAsync();

        await ServiceWith(ctx).FollowAsync(accountA.Id, business.Id, null, CancellationToken.None);
        var stateB = await ServiceWith(ctx).GetStateAsync(accountB.Id, business.Id, CancellationToken.None);

        Assert.False(stateB.IsFollowing);
    }

    private static AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid():N}")
            .Options;
        return new AppDbContext(options);
    }

    private static IBuyerFollowService ServiceWith(AppDbContext ctx) => new BuyerFollowService(ctx);

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
