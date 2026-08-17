using EntregasApi.Controllers;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EntregasApi.Tests;

public class AuthControllerFirebaseTests
{
    [Fact]
    public async Task FirebaseLogin_InvalidToken_ReturnsUnauthorized()
    {
        using var db = TestDbContextFactory.Create();
        var controller = Build(db, new FakeFirebaseAuthService(null));

        var result = await controller.FirebaseLogin(
            new FirebaseLoginRequest("invalid-token"));

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Empty(db.Accounts);
    }

    [Fact]
    public async Task FirebaseLogin_ExistingLegacyPhone_LinksUidAndUpgradesPhoneToE164()
    {
        using var db = TestDbContextFactory.Create();
        db.Accounts.Add(new Account
        {
            DisplayName = "Ana López",
            FirstName = "Ana",
            LastName = "López",
            Phone = "8681452290",
            PhoneVerifiedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = Build(
            db,
            new FakeFirebaseAuthService(
                new FirebaseAuthIdentity("firebase-ana", "+528681452290", false)));

        var result = await controller.FirebaseLogin(
            new FirebaseLoginRequest("valid-token"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<LoginResponse>(ok.Value);
        var account = Assert.Single(db.Accounts);
        Assert.Equal("firebase-ana", account.FirebaseUid);
        Assert.Equal("+528681452290", account.Phone);
        Assert.NotNull(account.PhoneVerifiedAt);
    }

    [Fact]
    public async Task FirebaseLogin_ExistingUid_ReturnsSameLocalAccount()
    {
        using var db = TestDbContextFactory.Create();
        db.Accounts.Add(new Account
        {
            DisplayName = "Luis García",
            Phone = "+528681452291",
            FirebaseUid = "firebase-luis",
            PhoneVerifiedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = Build(
            db,
            new FakeFirebaseAuthService(
                new FirebaseAuthIdentity("firebase-luis", "+528681452291", false)));

        var result = await controller.FirebaseLogin(
            new FirebaseLoginRequest("valid-token"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var session = Assert.IsType<LoginResponse>(ok.Value);
        Assert.Equal(1, session.AccountId);
        Assert.Equal(1, await db.Accounts.CountAsync());
    }

    [Fact]
    public async Task FirebaseLogin_PreservesLocalMembershipsWithoutAcceptingTenantFromClient()
    {
        using var db = TestDbContextFactory.Create();
        var account = new Account
        {
            DisplayName = "Sofía Gómez",
            Phone = "+528681452295",
            FirebaseUid = "firebase-tenant-safe",
            PhoneVerifiedAt = DateTime.UtcNow
        };
        var business = new Business
        {
            Name = "Tienda Sofía",
            Slug = "tienda-sofia"
        };
        account.Memberships.Add(new Membership
        {
            Account = account,
            Business = business,
            Role = MembershipRole.Owner
        });
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var controller = Build(
            db,
            new FakeFirebaseAuthService(
                new FirebaseAuthIdentity("firebase-tenant-safe", "+528681452295", false)));

        var result = await controller.FirebaseLogin(
            new FirebaseLoginRequest("valid-token"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var session = Assert.IsType<LoginResponse>(ok.Value);
        var membership = Assert.Single(session.Memberships);
        Assert.Equal("Tienda Sofía", membership.BusinessName);
        Assert.Equal("Owner", membership.Role);
    }

    [Fact]
    public async Task FirebaseLogin_NewClientWithLegalAcceptance_CreatesAccount()
    {
        using var db = TestDbContextFactory.Create();
        var controller = Build(
            db,
            new FakeFirebaseAuthService(
                new FirebaseAuthIdentity("firebase-new", "+528681452292", false)));

        var result = await controller.FirebaseLogin(
            new FirebaseLoginRequest(
                IdToken: "valid-token",
                FirstName: "María",
                LastName: "Pérez",
                Email: "maria@example.com",
                AcceptedLegal: true,
                LegalVersion: "2026-08-17"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<LoginResponse>(ok.Value);
        var account = Assert.Single(db.Accounts);
        Assert.Equal("María Pérez", account.DisplayName);
        Assert.Equal("+528681452292", account.Phone);
        Assert.Equal("firebase-new", account.FirebaseUid);
        Assert.Equal("maria@example.com", account.Email);
        Assert.NotNull(account.LegalAcceptedAtUtc);
    }

    [Fact]
    public async Task FirebaseLogin_DisabledFirebaseUser_ReturnsForbidden()
    {
        using var db = TestDbContextFactory.Create();
        var controller = Build(
            db,
            new FakeFirebaseAuthService(
                new FirebaseAuthIdentity("firebase-disabled", "+528681452293", true)));

        var result = await controller.FirebaseLogin(
            new FirebaseLoginRequest("valid-token"));

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Empty(db.Accounts);
    }

    [Fact]
    public async Task FirebaseLogin_WithoutLegalAcceptance_DoesNotCreateNewAccount()
    {
        using var db = TestDbContextFactory.Create();
        var controller = Build(
            db,
            new FakeFirebaseAuthService(
                new FirebaseAuthIdentity("firebase-legal", "+528681452294", false)));

        var result = await controller.FirebaseLogin(
            new FirebaseLoginRequest("valid-token"));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(db.Accounts);
    }

    private static AuthController Build(
        AppDbContext db,
        IFirebaseAuthService firebaseAuth)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "test-signing-key-for-firebase-tests",
                ["Jwt:Issuer"] = "tests",
                ["Jwt:Audience"] = "tests"
            })
            .Build();

        var controller = new AuthController(
            db,
            new TokenService(config),
            new RefreshTokenService(db),
            new FakeHostEnvironment(),
            config,
            new FakePhoneVerificationService(),
            firebaseAuth: firebaseAuth);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.Request.Headers[SellerTrialPolicy.DeviceHeaderName] =
            "firebase-auth-test-device-00000001";
        return controller;
    }

    private sealed class FakeFirebaseAuthService(FirebaseAuthIdentity? identity)
        : IFirebaseAuthService
    {
        public bool IsConfigured => true;

        public Task<FirebaseAuthIdentity?> VerifyIdTokenAsync(
            string idToken,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(identity);

        public Task<bool> DeleteUserAsync(
            string uid,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "EntregasApi.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }

    private sealed class FakePhoneVerificationService : IPhoneVerificationService
    {
        public bool IsConfigured => false;

        public string? NormalizePhone(string? input)
        {
            var digits = TextNormalizer.NormalizePhone(input);
            return digits?.Length == 10 ? digits : null;
        }

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
}
