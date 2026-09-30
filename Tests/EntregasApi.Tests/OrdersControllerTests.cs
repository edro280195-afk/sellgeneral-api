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

    [Fact]
    public async Task SaveChanges_AssignsOrderNumberPerBusiness()
    {
        using var ctx = TestDbContextFactory.Create();

        var clientA = new Client { BusinessId = 1, Name = "Clienta A", NormalizedName = "clienta a" };
        var clientB = new Client { BusinessId = 2, Name = "Clienta B", NormalizedName = "clienta b" };
        ctx.Clients.AddRange(clientA, clientB);
        await ctx.SaveChangesAsync();

        var firstBusinessOrder = new Order
        {
            BusinessId = 1,
            ClientId = clientA.Id,
            AccessToken = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        };
        var secondBusinessOrder = new Order
        {
            BusinessId = 2,
            ClientId = clientB.Id,
            AccessToken = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        };
        var secondOrderSameBusiness = new Order
        {
            BusinessId = 1,
            ClientId = clientA.Id,
            AccessToken = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        };

        ctx.Orders.AddRange(firstBusinessOrder, secondBusinessOrder, secondOrderSameBusiness);
        await ctx.SaveChangesAsync();

        Assert.Equal(1, firstBusinessOrder.OrderNumber);
        Assert.Equal(1, secondBusinessOrder.OrderNumber);
        Assert.Equal(2, secondOrderSameBusiness.OrderNumber);
        Assert.NotEqual(firstBusinessOrder.Id, secondBusinessOrder.Id);
    }

    /// <summary>
    /// Regresión: cambiar el estatus de un pedido con un premio ya canjeado
    /// (o cualquier descuento) reescribía Total = Subtotal + Envío, borrando el
    /// descuento mientras los puntos de la clienta ya se habían descontado.
    /// </summary>
    [Fact]
    public async Task UpdateStatus_KeepsDiscountInTotal()
    {
        using var ctx = TestDbContextFactory.Create();
        var order = await SeedOrderAsync(ctx);
        order.Subtotal = 300m;
        order.ShippingCost = 60m;
        order.DiscountAmount = 50m;
        order.Total = 310m; // 300 + 60 - 50
        await ctx.SaveChangesAsync();
        var controller = CreateControllerWithBusiness(ctx);

        var result = await controller.UpdateStatus(
            order.Id,
            new UpdateOrderStatusRequest("Confirmed", null, null, null));

        Assert.IsType<OkObjectResult>(result.Result);
        ctx.ChangeTracker.Clear();
        var persisted = await ctx.Orders.SingleAsync(o => o.Id == order.Id);
        Assert.Equal(OrderStatus.Confirmed, persisted.Status);
        Assert.Equal(50m, persisted.DiscountAmount);
        Assert.Equal(310m, persisted.Total);
    }

    [Fact]
    public async Task UpdateStatus_WithoutDiscount_TotalIsSubtotalPlusShipping()
    {
        using var ctx = TestDbContextFactory.Create();
        var order = await SeedOrderAsync(ctx);
        order.Subtotal = 300m;
        order.ShippingCost = 60m;
        order.Total = 360m;
        await ctx.SaveChangesAsync();
        var controller = CreateControllerWithBusiness(ctx);

        await controller.UpdateStatus(
            order.Id,
            new UpdateOrderStatusRequest("Confirmed", null, null, null));

        ctx.ChangeTracker.Clear();
        Assert.Equal(360m, (await ctx.Orders.SingleAsync(o => o.Id == order.Id)).Total);
    }

    private static OrdersController CreateControllerWithBusiness(AppDbContext ctx) => new(
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
        new FakeCurrentBusiness(new Business
        {
            Id = 1,
            Name = "Regi Bazar",
            Slug = "regibazar",
            FrontendUrl = "https://tienda.test",
        }));

    private sealed class FakeCurrentBusiness(Business business)
        : EntregasApi.Services.ICurrentBusiness
    {
        public Business Current => business;

        public Task<Business> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(business);
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
