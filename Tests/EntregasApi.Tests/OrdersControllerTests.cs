using EntregasApi.Controllers;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntregasApi.Tests;

public class OrdersControllerTests
{
    [Fact]
    public async Task GeneratePackages_WhenOrderHasNoPackages_SetsTotalToGeneratedCount()
    {
        using var ctx = TestDbContextFactory.Create();
        var order = await SeedOrderAsync(ctx);
        var controller = CreateController(ctx);

        var result = await controller.GeneratePackages(order.Id, new GeneratePackagesRequest(1));

        Assert.IsType<OkObjectResult>(result);
        ctx.ChangeTracker.Clear();
        var persistedOrder = await ctx.Orders
            .Include(o => o.Packages)
            .SingleAsync(o => o.Id == order.Id);
        Assert.Equal(1, persistedOrder.TotalPackages);
        Assert.Single(persistedOrder.Packages);
    }

    [Fact]
    public async Task GeneratePackages_WhenOrderAlreadyHasTwoPackages_IncrementsTotalByOne()
    {
        using var ctx = TestDbContextFactory.Create();
        var order = await SeedOrderAsync(ctx);
        ctx.OrderPackages.AddRange(
            new OrderPackage
            {
                BusinessId = 1,
                OrderId = order.Id,
                PackageNumber = 1,
                QrCodeValue = "TEST-ORDER-PACKAGE-1",
            },
            new OrderPackage
            {
                BusinessId = 1,
                OrderId = order.Id,
                PackageNumber = 2,
                QrCodeValue = "TEST-ORDER-PACKAGE-2",
            });
        order.TotalPackages = 2;
        await ctx.SaveChangesAsync();
        var controller = CreateController(ctx);

        var result = await controller.GeneratePackages(order.Id, new GeneratePackagesRequest(1));

        Assert.IsType<OkObjectResult>(result);
        ctx.ChangeTracker.Clear();
        var persistedOrder = await ctx.Orders
            .Include(o => o.Packages)
            .SingleAsync(o => o.Id == order.Id);
        Assert.Equal(3, persistedOrder.TotalPackages);
        Assert.Equal(3, persistedOrder.Packages.Count);
        Assert.Equal(3, persistedOrder.Packages.Max(p => p.PackageNumber));
    }

    [Fact]
    public async Task GetCaptureSettings_ReturnsConfiguredDefaultShippingCost()
    {
        using var ctx = TestDbContextFactory.Create();
        ctx.AppSettings.Add(new AppSettings
        {
            BusinessId = 1,
            DefaultShippingCost = 85m,
            LinkExpirationHours = 72,
        });
        await ctx.SaveChangesAsync();
        var controller = new OrdersController(
            ctx,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        var result = await controller.GetCaptureSettings();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var settings = Assert.IsType<OrderCaptureSettingsDto>(ok.Value);
        Assert.Equal(85m, settings.DefaultShippingCost);
    }

    private static OrdersController CreateController(AppDbContext ctx) => new(
        ctx,
        null!,
        null!,
        null!,
        null!,
        null!,
        null!,
        null!,
        null!,
        null!,
        null!,
        null!);

    private static async Task<Order> SeedOrderAsync(AppDbContext ctx)
    {
        var client = new Client
        {
            BusinessId = 1,
            Name = "Clienta de prueba",
            NormalizedName = "clienta de prueba",
        };
        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();

        var order = new Order
        {
            BusinessId = 1,
            ClientId = client.Id,
            AccessToken = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();
        return order;
    }
}
