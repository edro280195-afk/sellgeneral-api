using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntregasApi.Tests;

public class BuyerDeviceServiceTests
{
    [Fact]
    public async Task Register_NewToken_CreatesRow()
    {
        using var ctx = NewContext();

        await ServiceWith(ctx).RegisterAsync(
            accountId: 10, new RegisterDeviceRequest("token-abc", "android"), CancellationToken.None);

        var row = await ctx.BuyerDeviceTokens.SingleAsync();
        Assert.Equal(10, row.AccountId);
        Assert.Equal("token-abc", row.Token);
        Assert.Equal("android", row.Platform);
    }

    [Fact]
    public async Task Register_SameTokenDifferentAccount_ReassignsInsteadOfDuplicating()
    {
        using var ctx = NewContext();
        var service = ServiceWith(ctx);

        await service.RegisterAsync(10, new RegisterDeviceRequest("token-abc", "android"), CancellationToken.None);
        await service.RegisterAsync(20, new RegisterDeviceRequest("token-abc", "android"), CancellationToken.None);

        Assert.Equal(1, await ctx.BuyerDeviceTokens.CountAsync());
        var row = await ctx.BuyerDeviceTokens.SingleAsync();
        Assert.Equal(20, row.AccountId);
    }

    [Fact]
    public async Task Register_BlankPlatform_DefaultsToAndroid()
    {
        using var ctx = NewContext();

        await ServiceWith(ctx).RegisterAsync(
            10, new RegisterDeviceRequest("token-abc", ""), CancellationToken.None);

        var row = await ctx.BuyerDeviceTokens.SingleAsync();
        Assert.Equal("android", row.Platform);
    }

    [Fact]
    public async Task Unregister_RemovesRow()
    {
        using var ctx = NewContext();
        var service = ServiceWith(ctx);
        await service.RegisterAsync(10, new RegisterDeviceRequest("token-abc", "ios"), CancellationToken.None);

        await service.UnregisterAsync("token-abc", CancellationToken.None);

        Assert.Equal(0, await ctx.BuyerDeviceTokens.CountAsync());
    }

    [Fact]
    public async Task Unregister_NonexistentToken_DoesNotThrow()
    {
        using var ctx = NewContext();
        await ServiceWith(ctx).UnregisterAsync("no-existe", CancellationToken.None);
    }

    private static AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid():N}")
            .Options;
        return new AppDbContext(options);
    }

    private static IBuyerDeviceService ServiceWith(AppDbContext ctx) => new BuyerDeviceService(ctx);
}
