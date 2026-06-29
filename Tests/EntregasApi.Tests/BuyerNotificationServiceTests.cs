using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntregasApi.Tests;

public class BuyerNotificationServiceTests
{
    [Fact]
    public async Task GetMyNotifications_WithNoClaimedClients_ReturnsEmpty()
    {
        using var ctx = TestDbContextFactory.Create();
        var account = new Account { DisplayName = "X", Phone = "8000000000" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var result = await new BuyerNotificationService(ctx)
            .GetMyNotificationsAsync(account.Id, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetMyNotifications_ReturnsNotificationsForMyClientsOnly()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var mine = new Account { DisplayName = "Mía", Phone = "8681452290" };
        var other = new Account { DisplayName = "Otra", Phone = "8682223344" };
        ctx.Accounts.AddRange(mine, other);
        await ctx.SaveChangesAsync();

        var myClient = new Client
        {
            BusinessId = business.Id, AccountId = mine.Id, Name = "Mía",
            NormalizedName = "mia",
        };
        var otherClient = new Client
        {
            BusinessId = business.Id, AccountId = other.Id, Name = "Otra",
            NormalizedName = "otra",
        };
        ctx.Clients.AddRange(myClient, otherClient);
        await ctx.SaveChangesAsync();

        var myNotif1 = NewNotification(business.Id, myClient.Id, "Tu pedido salió", "🚚", "driver-en-route");
        var myNotif2 = NewNotification(business.Id, myClient.Id, "Entregado", "💝", "delivered");
        var otherNotif = NewNotification(business.Id, otherClient.Id, "Secreto", "🔒", "general");
        ctx.Notifications.AddRange(myNotif1, myNotif2, otherNotif);
        await ctx.SaveChangesAsync();

        var result = await new BuyerNotificationService(ctx)
            .GetMyNotificationsAsync(mine.Id, CancellationToken.None);

        Assert.Equal(2, result.Count);
        // Orden desc por CreatedAt.
        Assert.Equal("Entregado", result[0].Title);
        Assert.Equal("delivered", result[0].Tag);
        Assert.True(result[0].ReadAt is null);
        Assert.Equal("Tu pedido salió", result[1].Title);
        Assert.Equal("Regi Bazar", result[0].BusinessName);
        Assert.Equal("#FF0072", result[0].BrandPrimaryColor);
    }

    [Fact]
    public async Task GetMyNotifications_DoesNotLeakOtherAccountsData()
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
            NormalizedName = "otra",
        };
        ctx.Clients.Add(otherClient);
        ctx.Notifications.Add(NewNotification(business.Id, otherClient.Id, "S", "m", "general"));
        await ctx.SaveChangesAsync();

        var result = await new BuyerNotificationService(ctx)
            .GetMyNotificationsAsync(mine.Id, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task MarkAsRead_SetsReadAt_ForMyNotification()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var mine = new Account { DisplayName = "Mía", Phone = "8681452290" };
        ctx.Accounts.Add(mine);
        await ctx.SaveChangesAsync();

        var client = new Client
        {
            BusinessId = business.Id, AccountId = mine.Id, Name = "Mía",
            NormalizedName = "mia",
        };
        ctx.Clients.Add(client);
        var notif = NewNotification(business.Id, client.Id, "Hola", "m", "general");
        ctx.Notifications.Add(notif);
        await ctx.SaveChangesAsync();

        Assert.Null(notif.ReadAt);
        await new BuyerNotificationService(ctx).MarkAsReadAsync(
            mine.Id, notif.Id, CancellationToken.None);

        var dbNotif = await ctx.Notifications.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(n => n.Id == notif.Id);
        Assert.NotNull(dbNotif.ReadAt);
    }

    [Fact]
    public async Task MarkAsRead_OtherAccountNotification_ThrowsNotFound()
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
            NormalizedName = "otra",
        };
        ctx.Clients.Add(otherClient);
        var notif = NewNotification(business.Id, otherClient.Id, "Secreto", "m", "general");
        ctx.Notifications.Add(notif);
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<NotificationNotFoundException>(() =>
            new BuyerNotificationService(ctx).MarkAsReadAsync(
                mine.Id, notif.Id, CancellationToken.None));
    }

    [Fact]
    public async Task MarkAsRead_NonexistentNotification_ThrowsNotFound()
    {
        using var ctx = TestDbContextFactory.Create();
        var mine = new Account { DisplayName = "Mía", Phone = "8000000001" };
        ctx.Accounts.Add(mine);
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<NotificationNotFoundException>(() =>
            new BuyerNotificationService(ctx).MarkAsReadAsync(
                mine.Id, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task MarkAllAsRead_MarksOnlyMyUnreadNotifications()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var mine = new Account { DisplayName = "Mía", Phone = "8681452290" };
        var other = new Account { DisplayName = "Otra", Phone = "8682223344" };
        ctx.Accounts.AddRange(mine, other);
        await ctx.SaveChangesAsync();

        var myClient = new Client
        {
            BusinessId = business.Id, AccountId = mine.Id, Name = "Mía",
            NormalizedName = "mia",
        };
        var otherClient = new Client
        {
            BusinessId = business.Id, AccountId = other.Id, Name = "Otra",
            NormalizedName = "otra",
        };
        ctx.Clients.AddRange(myClient, otherClient);
        await ctx.SaveChangesAsync();

        var n1 = NewNotification(business.Id, myClient.Id, "1", "m", "general");
        var n2 = NewNotification(business.Id, myClient.Id, "2", "m", "general");
        var n3 = NewNotification(business.Id, otherClient.Id, "3", "m", "general");
        // n4 ya leída: NO se cuenta.
        var n4 = NewNotification(business.Id, myClient.Id, "4", "m", "general");
        n4.ReadAt = DateTime.UtcNow;
        ctx.Notifications.AddRange(n1, n2, n3, n4);
        await ctx.SaveChangesAsync();

        var count = await new BuyerNotificationService(ctx)
            .MarkAllAsReadAsync(mine.Id, CancellationToken.None);

        Assert.Equal(2, count);
        var myDb = await ctx.Notifications.AsNoTracking().IgnoreQueryFilters()
            .Where(n => n.ClientId == myClient.Id)
            .ToListAsync();
        Assert.All(myDb, n => Assert.NotNull(n.ReadAt));
        // La de la otra cuenta sigue sin leer.
        var otherDb = await ctx.Notifications.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(n => n.Id == n3.Id);
        Assert.Null(otherDb.ReadAt);
    }

    [Fact]
    public async Task CountUnread_OnlyMyUnread()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var mine = new Account { DisplayName = "Mía", Phone = "8681452290" };
        var other = new Account { DisplayName = "Otra", Phone = "8682223344" };
        ctx.Accounts.AddRange(mine, other);
        await ctx.SaveChangesAsync();

        var myClient = new Client
        {
            BusinessId = business.Id, AccountId = mine.Id, Name = "Mía",
            NormalizedName = "mia",
        };
        var otherClient = new Client
        {
            BusinessId = business.Id, AccountId = other.Id, Name = "Otra",
            NormalizedName = "otra",
        };
        ctx.Clients.AddRange(myClient, otherClient);
        await ctx.SaveChangesAsync();

        // 2 mías (1 leída, 1 no) + 1 de otra.
        var n1 = NewNotification(business.Id, myClient.Id, "1", "m", "general");
        var n2 = NewNotification(business.Id, myClient.Id, "2", "m", "general");
        n1.ReadAt = DateTime.UtcNow;
        var n3 = NewNotification(business.Id, otherClient.Id, "3", "m", "general");
        ctx.Notifications.AddRange(n1, n2, n3);
        await ctx.SaveChangesAsync();

        var count = await new BuyerNotificationService(ctx)
            .CountUnreadAsync(mine.Id, CancellationToken.None);

        Assert.Equal(1, count);
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

    private static Notification NewNotification(
        int businessId, int clientId, string title, string message, string tag) =>
        new()
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            ClientId = clientId,
            Title = title,
            Message = message,
            Tag = tag,
            CreatedAt = DateTime.UtcNow,
        };
}
