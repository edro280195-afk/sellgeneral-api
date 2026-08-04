using EntregasApi.Controllers;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntregasApi.Tests;

public class OnboardingControllerTests
{
    [Fact]
    public async Task Complete_Client_PersistsOnlyBuyerTour()
    {
        using var db = TestDbContextFactory.Create();
        var account = await AddAccountAsync(db);
        var controller = new OnboardingController(
            db,
            new FakeCurrentAccount(account.Id),
            TimeProvider.System);

        var result = await controller.Complete(
            new CompleteOnboardingRequest("client"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<AccountOnboardingDto>(ok.Value);
        Assert.True(response.BuyerCompleted);
        Assert.False(response.SellerCompleted);
        Assert.NotNull((await db.Accounts.SingleAsync()).BuyerOnboardingCompletedAtUtc);
    }

    [Fact]
    public async Task Complete_Seller_RequiresOwnerOrAdminMembership()
    {
        using var db = TestDbContextFactory.Create();
        var account = await AddAccountAsync(db);
        var controller = new OnboardingController(
            db,
            new FakeCurrentAccount(account.Id),
            TimeProvider.System);

        var result = await controller.Complete(
            new CompleteOnboardingRequest("seller"),
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Null((await db.Accounts.SingleAsync()).SellerOnboardingCompletedAtUtc);
    }

    [Fact]
    public async Task Complete_Seller_PersistsForOwner()
    {
        using var db = TestDbContextFactory.Create();
        var account = await AddAccountAsync(db);
        db.Memberships.Add(new Membership
        {
            AccountId = account.Id,
            Business = new Business { Name = "Tienda", Slug = "tienda" },
            Role = MembershipRole.Owner
        });
        await db.SaveChangesAsync();
        var controller = new OnboardingController(
            db,
            new FakeCurrentAccount(account.Id),
            TimeProvider.System);

        var result = await controller.Complete(
            new CompleteOnboardingRequest("seller"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<AccountOnboardingDto>(ok.Value);
        Assert.True(response.SellerCompleted);
    }

    private static async Task<Account> AddAccountAsync(AppDbContext db)
    {
        var account = new Account
        {
            DisplayName = "Ana",
            Phone = "8681452290",
            PhoneVerifiedAt = DateTime.UtcNow
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private sealed class FakeCurrentAccount(int accountId) : ICurrentAccount
    {
        public int? AccountId => accountId;
        public bool IsAuthenticated => true;
    }
}
