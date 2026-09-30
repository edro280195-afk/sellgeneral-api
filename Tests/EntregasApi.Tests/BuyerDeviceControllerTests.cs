using EntregasApi.Controllers;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntregasApi.Tests;

/// <summary>
/// Registro y baja del dispositivo: el token de push es lo que decide a qué teléfono
/// llegan los avisos de una cuenta. Al cerrar sesión tiene que poder quitarse siempre.
/// </summary>
public class BuyerDeviceControllerTests
{
    [Fact]
    public async Task Register_StoresTheDeviceForTheCallingAccount()
    {
        using var ctx = TestDbContextFactory.Create();

        var result = await Controller(ctx, accountId: 5)
            .Register(new RegisterDeviceRequest("tok-ana", "android"), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        var row = await ctx.BuyerDeviceTokens.SingleAsync();
        Assert.Equal(5, row.AccountId);
        Assert.Equal("tok-ana", row.Token);
    }

    [Fact]
    public async Task Register_WithoutSession_IsRejected()
    {
        using var ctx = TestDbContextFactory.Create();

        var result = await Controller(ctx, accountId: null)
            .Register(new RegisterDeviceRequest("tok-ana", "android"), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Empty(await ctx.BuyerDeviceTokens.ToListAsync());
    }

    [Fact]
    public async Task Register_WithTokenOrPlatformLongerThanTheColumns_IsRejected()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = Controller(ctx, accountId: 5);

        var longToken = await controller.Register(
            new RegisterDeviceRequest(new string('x', 513), "android"), CancellationToken.None);
        var longPlatform = await controller.Register(
            new RegisterDeviceRequest("tok-ana", new string('p', 21)), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(longToken);
        Assert.IsType<BadRequestObjectResult>(longPlatform);
        Assert.Empty(await ctx.BuyerDeviceTokens.ToListAsync());
    }

    [Fact]
    public async Task Unregister_WorksWithoutASession_SoLogoutAlwaysStopsThePushes()
    {
        using var ctx = TestDbContextFactory.Create();
        ctx.BuyerDeviceTokens.AddRange(
            new BuyerDeviceToken { AccountId = 5, Token = "tok-ana" },
            new BuyerDeviceToken { AccountId = 6, Token = "tok-otra" });
        await ctx.SaveChangesAsync();

        // La sesión ya expiró o se borró: sin credenciales.
        var result = await Controller(ctx, accountId: null).Unregister("tok-ana", CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        var remaining = await ctx.BuyerDeviceTokens.SingleAsync();
        Assert.Equal("tok-otra", remaining.Token); // solo se quitó ESE dispositivo
    }

    [Fact]
    public async Task Unregister_OfAnUnknownOrBlankToken_IsHarmless()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = Controller(ctx, accountId: null);

        Assert.IsType<NoContentResult>(await controller.Unregister("no-existe", CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.Unregister("  ", CancellationToken.None));
    }

    [Fact]
    public async Task Register_ReassignsADeviceThatChangedHands_SoTheOldAccountStopsReceiving()
    {
        using var ctx = TestDbContextFactory.Create();
        await Controller(ctx, accountId: 5).Register(new RegisterDeviceRequest("tok-compartido", "android"), CancellationToken.None);

        // Otra persona inicia sesión en el mismo teléfono.
        await Controller(ctx, accountId: 6).Register(new RegisterDeviceRequest("tok-compartido", "android"), CancellationToken.None);

        var row = await ctx.BuyerDeviceTokens.SingleAsync();
        Assert.Equal(6, row.AccountId);
    }

    private static BuyerDeviceController Controller(AppDbContext ctx, int? accountId) =>
        new(new BuyerDeviceService(ctx), new StubCurrentAccount(accountId));
}
