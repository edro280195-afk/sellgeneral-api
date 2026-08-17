using System.Security.Claims;
using EntregasApi.Controllers;
using EntregasApi.Data;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EntregasApi.Tests;

public class AccountDeletionTests
{
    [Fact]
    public async Task DeleteMyAccount_RemovesIdentityAndAnonymizesLinkedClient()
    {
        using var db = TestDbContextFactory.Create();
        var firebase = new FakeFirebaseAuthService();
        var business = new Business { Name = "Tienda", Slug = "tienda-delete-test" };
        var account = new Account
        {
            DisplayName = "María Pérez",
            Phone = "+528681452299",
            FirebaseUid = "firebase-delete-test",
            PhoneVerifiedAt = DateTime.UtcNow,
        };
        account.Memberships.Add(new Membership
        {
            Account = account,
            Business = business,
            Role = MembershipRole.Owner,
        });
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var client = new Client
        {
            BusinessId = business.Id,
            AccountId = account.Id,
            Name = "María Pérez",
            Phone = "+528681452299",
            Address = "Calle privada 123",
            NormalizedName = "maria perez",
            NormalizedPhone = "+528681452299",
            NormalizedAddress = "calle privada 123",
        };
        db.Clients.Add(client);
        db.RefreshTokens.Add(new RefreshToken
        {
            AccountId = account.Id,
            TokenHash = "refresh-hash-delete-test",
            ExpiresAt = DateTime.UtcNow.AddDays(10),
        });
        db.BuyerDeviceTokens.Add(new BuyerDeviceToken
        {
            AccountId = account.Id,
            Token = "device-token-delete-test",
        });
        db.StoreFollowers.Add(new StoreFollower
        {
            BusinessId = business.Id,
            AccountId = account.Id,
        });
        db.ClientClaimAudits.Add(new ClientClaimAudit
        {
            AccountId = account.Id,
            ClientId = client.Id,
            BusinessId = business.Id,
            Mode = ClientClaimMode.Manual,
        });
        db.Notifications.Add(new Notification
        {
            BusinessId = business.Id,
            AccountId = account.Id,
            Title = "Aviso",
            Message = "Mensaje de prueba",
            Tag = "test",
        });
        await db.SaveChangesAsync();

        var controller = Build(db, firebase, account.Id);
        var result = await controller.DeleteMyAccount();

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(db.Accounts);
        Assert.Empty(db.RefreshTokens);
        Assert.Empty(db.BuyerDeviceTokens);
        Assert.Empty(db.StoreFollowers);
        Assert.Empty(db.ClientClaimAudits);
        Assert.Empty(db.Notifications);
        var anonymizedClient = Assert.Single(db.Clients);
        Assert.Null(anonymizedClient.AccountId);
        Assert.Null(anonymizedClient.Phone);
        Assert.Null(anonymizedClient.Address);
        Assert.Equal("firebase-delete-test", firebase.DeletedUid);
    }

    private static AuthController Build(
        AppDbContext db,
        FakeFirebaseAuthService firebase,
        int accountId)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "test-signing-key-for-account-deletion",
                ["Jwt:Issuer"] = "tests",
                ["Jwt:Audience"] = "tests",
            })
            .Build();

        var controller = new AuthController(
            db,
            new TokenService(config),
            new RefreshTokenService(db),
            new FakeHostEnvironment(),
            config,
            new FakePhoneVerificationService(),
            firebaseAuth: firebase);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("account_id", accountId.ToString())],
                    "test")),
            },
        };
        return controller;
    }

    private sealed class FakeFirebaseAuthService : IFirebaseAuthService
    {
        public bool IsConfigured => true;
        public string? DeletedUid { get; private set; }

        public Task<FirebaseAuthIdentity?> VerifyIdTokenAsync(
            string idToken,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FirebaseAuthIdentity?>(null);

        public Task<bool> DeleteUserAsync(
            string uid,
            CancellationToken cancellationToken = default)
        {
            DeletedUid = uid;
            return Task.FromResult(true);
        }
    }

    private sealed class FakePhoneVerificationService : IPhoneVerificationService
    {
        public bool IsConfigured => true;

        public string? NormalizePhone(string? input) => input;

        public Task<PhoneVerificationOutcome> SendCodeAsync(
            string normalizedPhone,
            CancellationToken cancellationToken) =>
            Task.FromResult(PhoneVerificationOutcome.Sent);

        public Task<PhoneVerificationOutcome> CheckCodeAsync(
            string normalizedPhone,
            string code,
            CancellationToken cancellationToken) =>
            Task.FromResult(PhoneVerificationOutcome.Approved);
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "EntregasApi.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
