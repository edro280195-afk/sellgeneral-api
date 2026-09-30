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
/// El vencimiento del enlace público del pedido protege el enlace anónimo, no a
/// la dueña del pedido: con sesión de la cuenta dueña sigue pudiendo usarlo.
/// </summary>
public class ClientViewExpiryTests
{
    [Fact]
    public async Task ExpiredLink_OwnerWithSession_CanStillUseOrder()
    {
        using var ctx = TestDbContextFactory.Create();
        var (order, ownerAccountId) = await SeedExpiredOrderAsync(ctx);
        var controller = CreateController(ctx, accountId: ownerAccountId);

        var result = await controller.UpdateInstructions(
            order.AccessToken, new UpdateInstructionsRequest { Instructions = "Portón negro" });

        Assert.IsType<OkObjectResult>(result);
        ctx.ChangeTracker.Clear();
        Assert.Equal("Portón negro",
            (await ctx.Orders.SingleAsync(o => o.Id == order.Id)).DeliveryInstructions);
    }

    [Fact]
    public async Task ExpiredLink_Anonymous_StillGets410()
    {
        using var ctx = TestDbContextFactory.Create();
        var (order, _) = await SeedExpiredOrderAsync(ctx);
        var controller = CreateController(ctx, accountId: null);

        var result = await controller.UpdateInstructions(
            order.AccessToken, new UpdateInstructionsRequest { Instructions = "Portón negro" });

        Assert.Equal(410, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task ExpiredLink_OtherAccountWithSession_StillGets410()
    {
        using var ctx = TestDbContextFactory.Create();
        var (order, _) = await SeedExpiredOrderAsync(ctx);
        var stranger = new Account { DisplayName = "Otra", Phone = "8689999999" };
        ctx.Accounts.Add(stranger);
        await ctx.SaveChangesAsync();
        var controller = CreateController(ctx, accountId: stranger.Id);

        var result = await controller.UpdateInstructions(
            order.AccessToken, new UpdateInstructionsRequest { Instructions = "Portón negro" });

        Assert.Equal(410, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task ValidLink_Anonymous_WorksAsBefore()
    {
        using var ctx = TestDbContextFactory.Create();
        var (order, _) = await SeedExpiredOrderAsync(ctx);
        order.ExpiresAt = DateTime.UtcNow.AddDays(1);
        await ctx.SaveChangesAsync();
        var controller = CreateController(ctx, accountId: null);

        var result = await controller.UpdateInstructions(
            order.AccessToken, new UpdateInstructionsRequest { Instructions = "Entregar en la tarde" });

        Assert.IsType<OkObjectResult>(result);
    }

    private static async Task<(Order Order, int OwnerAccountId)> SeedExpiredOrderAsync(AppDbContext ctx)
    {
        var owner = new Account { DisplayName = "Juana", Phone = "8681112222" };
        ctx.Accounts.Add(owner);
        await ctx.SaveChangesAsync();

        var client = new Client
        {
            BusinessId = 1,
            AccountId = owner.Id,
            Name = "Juana López",
            NormalizedName = "juana lopez",
        };
        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();

        var order = new Order
        {
            BusinessId = 1,
            ClientId = client.Id,
            AccessToken = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddDays(-30), // enlace vencido
        };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();
        return (order, owner.Id);
    }

    private static ClientViewController CreateController(AppDbContext ctx, int? accountId) => new(
        ctx,
        null!,
        null!,
        null!,
        null!,
        null!,
        null!,
        new FakeCurrentAccount(accountId));

    private sealed class FakeCurrentAccount(int? accountId) : ICurrentAccount
    {
        public int? AccountId => accountId;
        public bool IsAuthenticated => accountId is not null;
    }
}
