using EntregasApi.Data;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EntregasApi.Tests;

public class SellerTrialPolicyTests
{
    private const string DeviceA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Evaluate_VerifiedAccountAndUnusedDevice_GrantsTrial()
    {
        using var db = TestDbContextFactory.Create();
        var account = await AddVerifiedAccountAsync(db, "8681452290", "a@example.com");
        var policy = CreatePolicy(db);

        var decision = await policy.EvaluateAsync(account, DeviceA);
        await db.SaveChangesAsync();

        Assert.True(decision.Granted);
        Assert.NotNull(account.SellerTrialGrantedAtUtc);
        Assert.NotNull(account.SellerTrialDeviceHash);
        Assert.NotEqual(DeviceA, account.SellerTrialDeviceHash);
        Assert.Null(account.SellerTrialRestrictionReason);
    }

    [Fact]
    public async Task Evaluate_SameAccountTwice_DoesNotGrantAnotherTrial()
    {
        using var db = TestDbContextFactory.Create();
        var account = await AddVerifiedAccountAsync(db, "8681452290", "a@example.com");
        var policy = CreatePolicy(db);

        Assert.True((await policy.EvaluateAsync(account, DeviceA)).Granted);
        await db.SaveChangesAsync();

        var second = await policy.EvaluateAsync(
            account,
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        Assert.False(second.Granted);
        Assert.Equal("trial_already_used", second.RestrictionReason);
    }

    [Fact]
    public async Task Evaluate_DeviceUsedByAnotherAccount_RequiresReview()
    {
        using var db = TestDbContextFactory.Create();
        var first = await AddVerifiedAccountAsync(db, "8681452290", "a@example.com");
        var second = await AddVerifiedAccountAsync(db, "8681452291", "b@example.com");
        var policy = CreatePolicy(db);

        Assert.True((await policy.EvaluateAsync(first, DeviceA)).Granted);
        await db.SaveChangesAsync();

        var decision = await policy.EvaluateAsync(second, DeviceA);

        Assert.False(decision.Granted);
        Assert.Equal("trial_review_required", decision.RestrictionReason);
        Assert.Null(second.SellerTrialGrantedAtUtc);
    }

    [Fact]
    public async Task Evaluate_MissingDevice_DoesNotGrantTrial()
    {
        using var db = TestDbContextFactory.Create();
        var account = await AddVerifiedAccountAsync(db, "8681452290", "a@example.com");

        var decision = await CreatePolicy(db).EvaluateAsync(account, null);

        Assert.False(decision.Granted);
        Assert.Equal("device_verification_required", decision.RestrictionReason);
    }

    private static SellerTrialPolicy CreatePolicy(AppDbContext db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TrialProtection:DeviceHashPepper"] =
                    "test-pepper-with-at-least-thirty-two-characters"
            })
            .Build();
        return new SellerTrialPolicy(db, configuration, TimeProvider.System);
    }

    private static async Task<Account> AddVerifiedAccountAsync(
        AppDbContext db,
        string phone,
        string email)
    {
        var account = new Account
        {
            DisplayName = "Prueba",
            Phone = phone,
            Email = email,
            PhoneVerifiedAt = DateTime.UtcNow
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }
}
