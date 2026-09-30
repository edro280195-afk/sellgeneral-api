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

    [Fact]
    public async Task SendTestPush_SendsOnlyTheCallersOwnTokens()
    {
        using var ctx = NewContext();
        var fcm = new FakeFcmDiagnostics(sent: 2);
        var service = new BuyerDeviceService(ctx, fcm);
        await service.RegisterAsync(1, new RegisterDeviceRequest("mio-1", "android"));
        await service.RegisterAsync(1, new RegisterDeviceRequest("mio-2", "android"));
        await service.RegisterAsync(2, new RegisterDeviceRequest("ajeno", "android"));

        var result = await service.SendTestPushAsync(accountId: 1);

        Assert.Equal(new[] { "mio-1", "mio-2" }, fcm.ReceivedTokens.OrderBy(t => t));
        Assert.Equal(2, result.TokensRegistered);
        Assert.Equal(2, result.Sent);
        Assert.True(result.FirebaseConfigured);
    }

    [Fact]
    public async Task SendTestPush_WithoutRegisteredDevice_ReportsZeroTokens()
    {
        using var ctx = NewContext();
        var fcm = new FakeFcmDiagnostics(sent: 0);

        var result = await new BuyerDeviceService(ctx, fcm).SendTestPushAsync(accountId: 7);

        Assert.Equal(0, result.TokensRegistered);
        Assert.Equal(0, result.Sent);
        Assert.Empty(fcm.ReceivedTokens);
    }

    [Fact]
    public async Task SendTestPush_WithoutFirebaseCredential_ReportsNotConfigured()
    {
        using var ctx = NewContext();
        var service = new BuyerDeviceService(ctx); // sin IFcmDiagnostics
        await service.RegisterAsync(1, new RegisterDeviceRequest("mio", "android"));

        var result = await service.SendTestPushAsync(accountId: 1);

        Assert.False(result.FirebaseConfigured);
        Assert.Equal(1, result.TokensRegistered);
        Assert.Equal(0, result.Sent);
    }

    [Fact]
    public async Task SendTestPush_SurfacesFirebaseErrors()
    {
        using var ctx = NewContext();
        var fcm = new FakeFcmDiagnostics(sent: 0, failed: 1, error: "SenderIdMismatch");
        var service = new BuyerDeviceService(ctx, fcm);
        await service.RegisterAsync(1, new RegisterDeviceRequest("mio", "android"));

        var result = await service.SendTestPushAsync(accountId: 1);

        Assert.Equal(1, result.Failed);
        Assert.Contains("SenderIdMismatch", result.Errors);
    }

    private sealed class FakeFcmDiagnostics(int sent, int failed = 0, string? error = null) : IFcmDiagnostics
    {
        public List<string> ReceivedTokens { get; } = new();

        public Task<FcmTestResult> SendTestAsync(
            IEnumerable<string> fcmTokens, string title, string body,
            Dictionary<string, string>? data = null)
        {
            ReceivedTokens.AddRange(fcmTokens);
            var errors = error is null ? Array.Empty<string>() : new[] { error };
            return Task.FromResult(new FcmTestResult(true, "proyecto-de-prueba", sent, failed, errors));
        }
    }

    private static IBuyerDeviceService ServiceWith(AppDbContext ctx) => new BuyerDeviceService(ctx);
}
