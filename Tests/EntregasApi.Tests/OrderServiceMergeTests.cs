using EntregasApi.Data;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntregasApi.Tests;

public class OrderServiceMergeTests
{
    [Fact]
    public async Task MergeOrdersAsync_CrossClient_MovesItemsAndTagsOriginalClient()
    {
        using var ctx = TestDbContextFactory.Create();
        var mom = await SeedClientAsync(ctx, "Mamá");
        var daughter = await SeedClientAsync(ctx, "Hija");

        var target = await SeedOrderAsync(ctx, mom, new[] { ("Blusa", 1, 200m) });
        var source = await SeedOrderAsync(ctx, daughter, new[] { ("Falda", 2, 150m) });

        var service = new EntregasApi.Services.OrderService(ctx);
        var result = await service.MergeOrdersAsync(target.Id, source.Id);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.ItemsMoved);

        ctx.ChangeTracker.Clear();
        var persistedTarget = await ctx.Orders.Include(o => o.Items).SingleAsync(o => o.Id == target.Id);
        var persistedSource = await ctx.Orders.SingleAsync(o => o.Id == source.Id);

        Assert.Equal(2, persistedTarget.Items.Count);
        var movedItem = persistedTarget.Items.Single(i => i.ProductName == "Falda");
        Assert.Equal(daughter.Id, movedItem.OriginalClientId);
        Assert.Equal("Hija", movedItem.OriginalClientName);
        Assert.Equal(source.Id, movedItem.OriginalOrderId);

        // El item que ya era de la mamá no se etiqueta como "movido" de nadie.
        var ownItem = persistedTarget.Items.Single(i => i.ProductName == "Blusa");
        Assert.Null(ownItem.OriginalClientId);

        Assert.Equal(200m + 300m, persistedTarget.Subtotal);
        Assert.Equal(persistedTarget.ShippingCost + 500m, persistedTarget.Total);

        Assert.Equal(OrderStatus.Canceled, persistedSource.Status);
        Assert.Equal(target.Id, persistedSource.MergedIntoOrderId);
        Assert.NotNull(persistedSource.MergedAt);
        Assert.Equal(0m, persistedSource.Total);

        var audit = await ctx.OrderMergeAudits.SingleAsync();
        Assert.Equal(source.Id, audit.SourceOrderId);
        Assert.Equal(target.Id, audit.TargetOrderId);
        Assert.Equal("Hija", audit.SourceClientName);
        Assert.Equal(1, audit.ItemsMoved);
    }

    [Fact]
    public async Task MergeOrdersAsync_SameClient_DoesNotTagOriginalClient()
    {
        using var ctx = TestDbContextFactory.Create();
        var client = await SeedClientAsync(ctx, "Juana");

        var target = await SeedOrderAsync(ctx, client, new[] { ("Vestido", 1, 400m) });
        var source = await SeedOrderAsync(ctx, client, new[] { ("Aretes", 1, 100m) });

        var service = new EntregasApi.Services.OrderService(ctx);
        var result = await service.MergeOrdersAsync(target.Id, source.Id);

        Assert.True(result.Success, result.Error);

        ctx.ChangeTracker.Clear();
        var persistedTarget = await ctx.Orders.Include(o => o.Items).SingleAsync(o => o.Id == target.Id);
        Assert.All(persistedTarget.Items, i => Assert.Null(i.OriginalClientId));
    }

    [Fact]
    public async Task MergeOrdersAsync_MovesPaymentsSoBalanceStaysCorrect()
    {
        using var ctx = TestDbContextFactory.Create();
        var mom = await SeedClientAsync(ctx, "Mamá");
        var daughter = await SeedClientAsync(ctx, "Hija");

        var target = await SeedOrderAsync(ctx, mom, new[] { ("Blusa", 1, 200m) });
        var source = await SeedOrderAsync(ctx, daughter, new[] { ("Falda", 1, 150m) });
        ctx.OrderPayments.Add(new OrderPayment { BusinessId = 1, OrderId = source.Id, Amount = 150m, Method = "Efectivo" });
        await ctx.SaveChangesAsync();

        var service = new EntregasApi.Services.OrderService(ctx);
        var result = await service.MergeOrdersAsync(target.Id, source.Id);

        Assert.True(result.Success, result.Error);

        ctx.ChangeTracker.Clear();
        var persistedTarget = await ctx.Orders.Include(o => o.Payments).SingleAsync(o => o.Id == target.Id);
        Assert.Equal(150m, persistedTarget.AmountPaid);
        Assert.Equal(persistedTarget.Total - 150m, persistedTarget.BalanceDue);
    }

    [Theory]
    [InlineData(OrderStatus.Delivered)]
    [InlineData(OrderStatus.Canceled)]
    [InlineData(OrderStatus.InRoute)]
    public async Task MergeOrdersAsync_RejectsWhenSourceAlreadyPastPacking(OrderStatus blockedStatus)
    {
        using var ctx = TestDbContextFactory.Create();
        var client = await SeedClientAsync(ctx, "Juana");
        var target = await SeedOrderAsync(ctx, client, new[] { ("Vestido", 1, 400m) });
        var source = await SeedOrderAsync(ctx, client, new[] { ("Aretes", 1, 100m) });
        source.Status = blockedStatus;
        await ctx.SaveChangesAsync();

        var service = new EntregasApi.Services.OrderService(ctx);
        var result = await service.MergeOrdersAsync(target.Id, source.Id);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task MergeOrdersAsync_RejectsWhenSourceAlreadyHasPackages()
    {
        using var ctx = TestDbContextFactory.Create();
        var client = await SeedClientAsync(ctx, "Juana");
        var target = await SeedOrderAsync(ctx, client, new[] { ("Vestido", 1, 400m) });
        var source = await SeedOrderAsync(ctx, client, new[] { ("Aretes", 1, 100m) });
        ctx.OrderPackages.Add(new OrderPackage { BusinessId = 1, OrderId = source.Id, PackageNumber = 1, QrCodeValue = "NN-TEST-1" });
        await ctx.SaveChangesAsync();

        var service = new EntregasApi.Services.OrderService(ctx);
        var result = await service.MergeOrdersAsync(target.Id, source.Id);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task MergeOrdersAsync_RejectsSelfMerge()
    {
        using var ctx = TestDbContextFactory.Create();
        var client = await SeedClientAsync(ctx, "Juana");
        var order = await SeedOrderAsync(ctx, client, new[] { ("Vestido", 1, 400m) });

        var service = new EntregasApi.Services.OrderService(ctx);
        var result = await service.MergeOrdersAsync(order.Id, order.Id);

        Assert.False(result.Success);
    }

    private static async Task<Client> SeedClientAsync(AppDbContext ctx, string name)
    {
        var client = new Client { BusinessId = 1, Name = name, NormalizedName = name.ToLower() };
        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();
        return client;
    }

    private static async Task<Order> SeedOrderAsync(AppDbContext ctx, Client client, (string Name, int Qty, decimal Price)[] items)
    {
        var order = new Order
        {
            BusinessId = 1,
            ClientId = client.Id,
            AccessToken = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            ShippingCost = 60m,
        };
        foreach (var (name, qty, price) in items)
        {
            order.Items.Add(new OrderItem { ProductName = name, Quantity = qty, UnitPrice = price, LineTotal = qty * price });
        }
        order.Subtotal = order.Items.Sum(i => i.LineTotal);
        order.Total = order.Subtotal + order.ShippingCost;
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();
        return order;
    }
}
