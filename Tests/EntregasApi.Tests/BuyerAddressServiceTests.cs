using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntregasApi.Tests;

public class BuyerAddressServiceTests
{
    [Fact]
    public async Task GetMyAddresses_WithNoClaimedClients_ReturnsEmpty()
    {
        using var ctx = TestDbContextFactory.Create();
        var account = new Account { DisplayName = "X", Phone = "8000000000" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var result = await new BuyerAddressService(ctx).GetMyAddressesAsync(account.Id);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetMyAddresses_HappyPath_ReturnsAddressPerBusiness()
    {
        using var ctx = TestDbContextFactory.Create();
        var bizA = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        var bizB = NewBusiness("Luna Bella", "luna", "#FF7A59");
        ctx.Businesses.AddRange(bizA, bizB);
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        ctx.Clients.AddRange(
            new Client
            {
                BusinessId = bizA.Id, AccountId = account.Id, Name = "Ana",
                NormalizedName = "ana",
                Address = "Av. Reforma 123",
                Latitude = 27.4861, Longitude = -99.5069,
                DeliveryInstructions = "Casa blanca con portón negro",
            },
            new Client
            {
                BusinessId = bizB.Id, AccountId = account.Id, Name = "Ana",
                NormalizedName = "ana luna",
                Address = null,
            });
        await ctx.SaveChangesAsync();

        var result = await new BuyerAddressService(ctx).GetMyAddressesAsync(account.Id);

        Assert.Equal(2, result.Count);
        // Ordenado por nombre de tienda: Luna Bella, Regi Bazar.
        var luna = result[0];
        Assert.Equal("Luna Bella", luna.BusinessName);
        Assert.Null(luna.Address);

        var regi = result[1];
        Assert.Equal("Regi Bazar", regi.BusinessName);
        Assert.Equal("Av. Reforma 123", regi.Address);
        Assert.Equal(27.4861, regi.Latitude);
        Assert.Equal(-99.5069, regi.Longitude);
        Assert.Equal("Casa blanca con portón negro", regi.DeliveryInstructions);
        Assert.Equal("#FF0072", regi.BrandPrimaryColor);
    }

    [Fact]
    public async Task GetMyAddresses_DoesNotLeakOtherAccountsData()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var mine = new Account { DisplayName = "Mía", Phone = "8681452290" };
        var other = new Account { DisplayName = "Otra", Phone = "8682223344" };
        ctx.Accounts.AddRange(mine, other);
        await ctx.SaveChangesAsync();

        ctx.Clients.Add(new Client
        {
            BusinessId = business.Id, AccountId = other.Id, Name = "Otra",
            NormalizedName = "otra",
            Address = "Calle privada",
        });
        await ctx.SaveChangesAsync();

        var result = await new BuyerAddressService(ctx).GetMyAddressesAsync(mine.Id);

        Assert.Empty(result);
    }

    [Fact]
    public async Task UpdateAddress_ForClientNotMine_ThrowsNotFound()
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
            NormalizedName = "otra", Address = "Antes",
        };
        ctx.Clients.Add(otherClient);
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<AddressNotFoundException>(() =>
            new BuyerAddressService(ctx).UpdateAddressAsync(
                mine.Id,
                otherClient.Id,
                new UpdateBuyerAddressRequest(Address: "Hackeado"),
                CancellationToken.None));
    }

    [Fact]
    public async Task UpdateAddress_ForNonexistentClient_ThrowsNotFound()
    {
        using var ctx = TestDbContextFactory.Create();
        var account = new Account { DisplayName = "X", Phone = "8000000000" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<AddressNotFoundException>(() =>
            new BuyerAddressService(ctx).UpdateAddressAsync(
                account.Id,
                9999,
                new UpdateBuyerAddressRequest(Address: "X"),
                CancellationToken.None));
    }

    [Fact]
    public async Task UpdateAddress_OnlyTouchesProvidedFields()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var client = new Client
        {
            BusinessId = business.Id, AccountId = account.Id, Name = "Ana",
            NormalizedName = "ana",
            Address = "Original",
            Latitude = 1.0, Longitude = 2.0,
            DeliveryInstructions = "Original ref",
        };
        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();

        // Solo actualizar Address; lat/lng/instructions no deben cambiar.
        var updated = await new BuyerAddressService(ctx).UpdateAddressAsync(
            account.Id,
            client.Id,
            new UpdateBuyerAddressRequest(Address: "Nueva dirección"),
            CancellationToken.None);

        Assert.Equal("Nueva dirección", updated.Address);
        Assert.Equal(1.0, updated.Latitude);
        Assert.Equal(2.0, updated.Longitude);
        Assert.Equal("Original ref", updated.DeliveryInstructions);

        var db = await ctx.Clients.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(c => c.Id == client.Id);
        Assert.Equal("Nueva dirección", db.Address);
        Assert.Equal(1.0, db.Latitude);
    }

    [Fact]
    public async Task UpdateAddress_ClearsAddressWithEmptyString()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = NewBusiness("Regi Bazar", "regibazar", "#FF0072");
        ctx.Businesses.Add(business);
        var account = new Account { DisplayName = "Ana", Phone = "8680000001" };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();

        var client = new Client
        {
            BusinessId = business.Id, AccountId = account.Id, Name = "Ana",
            NormalizedName = "ana", Address = "Existente",
        };
        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();

        var updated = await new BuyerAddressService(ctx).UpdateAddressAsync(
            account.Id,
            client.Id,
            new UpdateBuyerAddressRequest(Address: "   "),
            CancellationToken.None);

        Assert.Null(updated.Address);
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
}
