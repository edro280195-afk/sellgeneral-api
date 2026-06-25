using EntregasApi.Services;
using Xunit;

namespace EntregasApi.Tests;

public class PlanCatalogPricingTests
{
    [Theory]
    [InlineData(PlanTiers.Entrada, 129.00)]
    [InlineData(PlanTiers.Pro, 250.00)]
    [InlineData(PlanTiers.Elite, 460.00)]
    [InlineData(PlanTiers.Locked, 0)]
    public void GetMonthlyPrice_ReturnsExpectedBase(string tier, decimal expected)
    {
        Assert.Equal(expected, PlanCatalog.GetMonthlyPrice(tier));
    }

    [Fact]
    public void MonthlyPeriod_HasNoDiscount()
    {
        Assert.Equal(129m, PlanCatalog.GetPeriodAmount(PlanTiers.Entrada, SubscriptionPeriodicity.Monthly));
        Assert.Equal(250m, PlanCatalog.GetPeriodAmount(PlanTiers.Pro, SubscriptionPeriodicity.Monthly));
        Assert.Equal(460m, PlanCatalog.GetPeriodAmount(PlanTiers.Elite, SubscriptionPeriodicity.Monthly));
    }

    [Fact]
    public void QuarterlyPeriod_AppliesTenPercentDiscount()
    {
        // Entrada: 129 * 3 = 387, con 10% = 348.30
        Assert.Equal(348.30m, PlanCatalog.GetPeriodAmount(PlanTiers.Entrada, SubscriptionPeriodicity.Quarterly));
        // Pro: 250 * 3 = 750, con 10% = 675
        Assert.Equal(675.00m, PlanCatalog.GetPeriodAmount(PlanTiers.Pro, SubscriptionPeriodicity.Quarterly));
        // Elite: 460 * 3 = 1380, con 10% = 1242
        Assert.Equal(1242.00m, PlanCatalog.GetPeriodAmount(PlanTiers.Elite, SubscriptionPeriodicity.Quarterly));
    }

    [Fact]
    public void AnnualPeriod_AppliesTwentyPercentDiscount()
    {
        // Entrada: 129 * 12 = 1548, con 20% = 1238.40
        Assert.Equal(1238.40m, PlanCatalog.GetPeriodAmount(PlanTiers.Entrada, SubscriptionPeriodicity.Annual));
        // Pro: 250 * 12 = 3000, con 20% = 2400
        Assert.Equal(2400.00m, PlanCatalog.GetPeriodAmount(PlanTiers.Pro, SubscriptionPeriodicity.Annual));
        // Elite: 460 * 12 = 5520, con 20% = 4416
        Assert.Equal(4416.00m, PlanCatalog.GetPeriodAmount(PlanTiers.Elite, SubscriptionPeriodicity.Annual));
    }

    [Fact]
    public void Discounts_CanBeZero_ForNoDiscount()
    {
        Assert.Equal(387m, PlanCatalog.GetPeriodAmount(
            PlanTiers.Entrada,
            SubscriptionPeriodicity.Quarterly,
            quarterlyDiscountPct: 0m));
        Assert.Equal(1548m, PlanCatalog.GetPeriodAmount(
            PlanTiers.Entrada,
            SubscriptionPeriodicity.Annual,
            annualDiscountPct: 0m));
    }

    [Fact]
    public void CustomDiscounts_AreApplied()
    {
        // 5% trimestral sobre Entrada: 387 * 0.95 = 367.65
        Assert.Equal(367.65m, PlanCatalog.GetPeriodAmount(
            PlanTiers.Entrada,
            SubscriptionPeriodicity.Quarterly,
            quarterlyDiscountPct: 5m));

        // 15% anual sobre Pro: 3000 * 0.85 = 2550
        Assert.Equal(2550.00m, PlanCatalog.GetPeriodAmount(
            PlanTiers.Pro,
            SubscriptionPeriodicity.Annual,
            annualDiscountPct: 15m));
    }

    [Theory]
    [InlineData("monthly", SubscriptionPeriodicity.Monthly, true)]
    [InlineData("Monthly", SubscriptionPeriodicity.Monthly, true)]
    [InlineData("mensual", SubscriptionPeriodicity.Monthly, true)]
    [InlineData("quarterly", SubscriptionPeriodicity.Quarterly, true)]
    [InlineData("Quarterly", SubscriptionPeriodicity.Quarterly, true)]
    [InlineData("trimestral", SubscriptionPeriodicity.Quarterly, true)]
    [InlineData("annual", SubscriptionPeriodicity.Annual, true)]
    [InlineData("Anual", SubscriptionPeriodicity.Annual, true)]
    [InlineData("1", SubscriptionPeriodicity.Monthly, true)]
    [InlineData("3", SubscriptionPeriodicity.Quarterly, true)]
    [InlineData("12", SubscriptionPeriodicity.Annual, true)]
    [InlineData("  monthly  ", SubscriptionPeriodicity.Monthly, true)]
    [InlineData("foo", SubscriptionPeriodicity.Monthly, false)]
    [InlineData("", SubscriptionPeriodicity.Monthly, false)]
    [InlineData(null, SubscriptionPeriodicity.Monthly, false)]
    public void TryParse_Periodicity(string? input, SubscriptionPeriodicity expected, bool success)
    {
        var actual = SubscriptionPeriodicities.TryParse(input, out var periodicity);
        Assert.Equal(success, actual);
        if (success)
        {
            Assert.Equal(expected, periodicity);
        }
    }
}
