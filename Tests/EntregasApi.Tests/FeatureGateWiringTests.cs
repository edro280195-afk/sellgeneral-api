using EntregasApi.Controllers;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace EntregasApi.Tests;

public class FeatureGateWiringTests
{
    [Fact]
    public void Controllers_HaveExpectedFeatureGates()
    {
        AssertClassGate<AdminFinancialsController>(Feature.Financials);
        AssertClassGate<TandaController>(Feature.TandasRaffles);
        AssertClassGate<RafflesController>(Feature.TandasRaffles);
        AssertClassGate<PosController>(Feature.Pos);
        AssertClassGate<CamiController>(Feature.CamiAssistant);
    }

    [Fact]
    public void PremiumActions_HaveExpectedFeatureGates()
    {
        AssertMethodGate<ClientsController>(nameof(ClientsController.FacebookImportPreview), Feature.FacebookImport);
        AssertMethodGate<ClientsController>(nameof(ClientsController.FacebookImportApply), Feature.FacebookImport);
        AssertMethodGate<ClientResolutionController>(nameof(ClientResolutionController.Merge), Feature.FacebookImport);
        AssertMethodGate<ClientResolutionController>(nameof(ClientResolutionController.DuplicateSuggestions), Feature.FacebookImport);
        AssertMethodGate<ClientResolutionController>(nameof(ClientResolutionController.GetMergeAudits), Feature.FacebookImport);
        AssertMethodGate<OrdersController>(nameof(OrdersController.Export), Feature.Exports);
        AssertMethodGate<DriverController>(nameof(DriverController.CamiCommand), Feature.CamiAssistant);
    }

    [Fact]
    public void PublicTandaController_DoesNotRequireTandasRafflesFeature()
    {
        var gates = typeof(PublicTandaController)
            .GetCustomAttributes(typeof(RequiresFeatureAttribute), inherit: true)
            .Cast<RequiresFeatureAttribute>();

        Assert.DoesNotContain(gates, gate => gate.Feature == Feature.TandasRaffles);
    }

    [Fact]
    public void BusinessLifecycleEndpoints_HaveExpectedBypassMetadata()
    {
        var create = typeof(BusinessController).GetMethod(nameof(BusinessController.CreateBusiness))
            ?? throw new InvalidOperationException("No se encontro BusinessController.CreateBusiness.");
        var status = typeof(BusinessController).GetMethod(nameof(BusinessController.GetAccountStatus))
            ?? throw new InvalidOperationException("No se encontro BusinessController.GetAccountStatus.");
        var changePlan = typeof(BusinessController).GetMethod(nameof(BusinessController.ChangePlan))
            ?? throw new InvalidOperationException("No se encontro BusinessController.ChangePlan.");

        Assert.NotNull(create.GetCustomAttributes(typeof(SkipTenantResolutionAttribute), inherit: true).SingleOrDefault());
        Assert.Equal(
            AuthorizationPolicies.AuthenticatedAccount,
            create.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>()
                .Single()
                .Policy);
        Assert.NotNull(status.GetCustomAttributes(typeof(BypassSubscriptionLockAttribute), inherit: true).SingleOrDefault());
        Assert.NotNull(changePlan.GetCustomAttributes(typeof(BypassSubscriptionLockAttribute), inherit: true).SingleOrDefault());
    }

    [Fact]
    public void BuyerControllers_SkipTenantResolution()
    {
        AssertClassAttribute<BuyerController, SkipTenantResolutionAttribute>();
        AssertClassAttribute<BuyerStoreController, SkipTenantResolutionAttribute>();
        AssertClassAttribute<BuyerReserveController, SkipTenantResolutionAttribute>();
        AssertClassAttribute<BuyerNotificationController, SkipTenantResolutionAttribute>();
        AssertClassAttribute<BuyerPaymentController, SkipTenantResolutionAttribute>();
        AssertClassAttribute<BuyerAddressController, SkipTenantResolutionAttribute>();
        AssertClassAttribute<BuyerTandasController, SkipTenantResolutionAttribute>();
        AssertClassAttribute<BuyerRafflesController, SkipTenantResolutionAttribute>();
        AssertClassAttribute<ClientClaimController, SkipTenantResolutionAttribute>();
    }

    private static void AssertClassGate<TController>(Feature expectedFeature)
    {
        var gate = typeof(TController)
            .GetCustomAttributes(typeof(RequiresFeatureAttribute), inherit: true)
            .Cast<RequiresFeatureAttribute>()
            .SingleOrDefault(gate => gate.Feature == expectedFeature);

        Assert.NotNull(gate);
    }

    private static void AssertMethodGate<TController>(string methodName, Feature expectedFeature)
    {
        var method = typeof(TController).GetMethod(methodName)
            ?? throw new InvalidOperationException($"No se encontro {typeof(TController).Name}.{methodName}.");

        var gate = method
            .GetCustomAttributes(typeof(RequiresFeatureAttribute), inherit: true)
            .Cast<RequiresFeatureAttribute>()
            .SingleOrDefault(gate => gate.Feature == expectedFeature);

        Assert.NotNull(gate);
    }

    private static void AssertClassAttribute<TController, TAttribute>()
        where TAttribute : Attribute
    {
        var attribute = typeof(TController)
            .GetCustomAttributes(typeof(TAttribute), inherit: true)
            .SingleOrDefault();

        Assert.NotNull(attribute);
    }
}
