using EntregasApi.Models;
using EntregasApi.Services;
using Xunit;

namespace EntregasApi.Tests;

public class TandaPaymentConfigurationTests
{
    [Fact]
    public async Task GetTandaByToken_ReturnsTenantPublicKeyOnlyWhenBothCredentialsExist()
    {
        using var ctx = TestDbContextFactory.Create();
        var business = new Business
        {
            Name = "Tienda",
            Slug = "tienda",
            BrandPrimaryColor = "#FF0072",
            DepotLat = 0,
            DepotLng = 0,
            GeocodingRegion = "MX",
            PlanTier = "Elite",
            SubscriptionStatus = SubscriptionStatus.Active,
            MercadoPagoPublicKey = "TENANT-PUBLIC",
            MercadoPagoAccessToken = "TENANT-SECRET"
        };
        ctx.Businesses.Add(business);
        await ctx.SaveChangesAsync();

        var product = new TandaProduct
        {
            BusinessId = business.Id,
            Name = "Producto",
            BasePrice = 100m
        };
        ctx.TandaProducts.Add(product);
        await ctx.SaveChangesAsync();

        var tanda = new Tanda
        {
            BusinessId = business.Id,
            ProductId = product.Id,
            Name = "Tanda",
            TotalWeeks = 10,
            WeeklyAmount = 100m,
            StartDate = DateTime.UtcNow.Date,
            Status = "Active",
            AccessToken = $"tok-{Guid.NewGuid():N}"
        };
        ctx.Tandas.Add(tanda);
        await ctx.SaveChangesAsync();

        var service = new TandaService(ctx);
        var configured = await service.GetTandaByTokenAsync(tanda.AccessToken);
        Assert.Equal("TENANT-PUBLIC", configured!.MercadoPagoPublicKey);

        business.MercadoPagoAccessToken = null;
        await ctx.SaveChangesAsync();

        var incomplete = await service.GetTandaByTokenAsync(tanda.AccessToken);
        Assert.Null(incomplete!.MercadoPagoPublicKey);
    }
}
