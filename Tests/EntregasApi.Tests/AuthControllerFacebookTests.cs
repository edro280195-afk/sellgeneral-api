using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace EntregasApi.Tests;

public class AuthControllerFacebookTests
{
    private const string FacebookAppId = "test-facebook-app";
    private const string FacebookUserId = "fb-user-123";
    private const string LimitedKeyId = "facebook-limited-test-key";
    private static readonly RSAParameters LimitedRsaParameters =
        CreateLimitedRsaParameters();

    [Fact]
    public async Task FacebookLogin_LinkedAndVerified_ReturnsSession()
    {
        using var db = TestDbContextFactory.Create();
        db.Accounts.Add(new Account
        {
            DisplayName = "Ana López",
            FirstName = "Ana",
            LastName = "López",
            Email = "ana@correo.com",
            Phone = "8681452290",
            PhoneVerifiedAt = DateTime.UtcNow,
            FacebookUserId = FacebookUserId
        });
        await db.SaveChangesAsync();
        var controller = Build(db);

        var result = await controller.FacebookLogin(
            new FacebookLoginRequest("valid-token", "client"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var session = Assert.IsType<LoginResponse>(ok.Value);
        Assert.Equal("Ana López", session.Name);
        Assert.Empty(session.Memberships);
    }

    [Fact]
    public async Task FacebookLogin_NewSeller_RequestsProfileAndBusiness()
    {
        using var db = TestDbContextFactory.Create();
        var controller = Build(db);

        var result = await controller.FacebookLogin(
            new FacebookLoginRequest("valid-token", "seller"));

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        var continuation = Assert.IsType<FacebookContinuationResponse>(conflict.Value);
        Assert.True(continuation.NeedsProfile);
        Assert.Contains("phone", continuation.MissingFields);
        Assert.Contains("businessName", continuation.MissingFields);
        Assert.Equal("Ana", continuation.FirstName);
        Assert.Equal("López", continuation.LastName);
        Assert.Empty(await db.Accounts.ToListAsync());
    }

    [Fact]
    public async Task CompleteFacebookProfile_NewSeller_RequiresOtpBeforeBusiness()
    {
        using var db = TestDbContextFactory.Create();
        var controller = Build(db);

        var result = await controller.CompleteFacebookProfile(
            SellerCompletion());

        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        var continuation = Assert.IsType<FacebookContinuationResponse>(accepted.Value);
        Assert.True(continuation.NeedsPhoneVerification);
        Assert.True(continuation.DevMode);

        var account = await db.Accounts.SingleAsync();
        Assert.Equal(FacebookUserId, account.FacebookUserId);
        Assert.Equal("8681452290", account.Phone);
        Assert.Null(account.PhoneVerifiedAt);
        Assert.Empty(await db.Businesses.ToListAsync());
        Assert.Empty(await db.Memberships.ToListAsync());
    }

    [Fact]
    public async Task ConfirmPhone_ForFacebookSeller_CreatesOwnerBusiness()
    {
        using var db = TestDbContextFactory.Create();
        var controller = Build(db);
        await controller.CompleteFacebookProfile(SellerCompletion());

        var result = await controller.ConfirmPhone(
            new VerifyPhoneLoginRequest(
                "8681452290",
                "000000",
                "seller",
                "Luna Bonita",
                "Matamoros"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var session = Assert.IsType<LoginResponse>(ok.Value);
        var membership = Assert.Single(session.Memberships);
        Assert.Equal("Owner", membership.Role);
        Assert.Equal("Luna Bonita", membership.BusinessName);

        var account = await db.Accounts.SingleAsync();
        Assert.NotNull(account.PhoneVerifiedAt);
        var business = await db.Businesses.SingleAsync();
        Assert.Equal("luna-bonita", business.Slug);
        Assert.Equal(SubscriptionStatus.Trialing, business.SubscriptionStatus);
        Assert.Equal(PlanTiers.Pro, business.PlanTier);
    }

    [Fact]
    public async Task ConfirmPhone_ForFacebookSeller_IsIdempotent()
    {
        using var db = TestDbContextFactory.Create();
        var controller = Build(db);
        await controller.CompleteFacebookProfile(SellerCompletion());
        var request = new VerifyPhoneLoginRequest(
            "8681452290",
            "000000",
            "seller",
            "Luna Bonita",
            "Matamoros");

        await controller.ConfirmPhone(request);
        await controller.ConfirmPhone(request);

        Assert.Single(await db.Businesses.ToListAsync());
        Assert.Single(await db.Memberships.ToListAsync());
    }

    [Fact]
    public async Task CompleteFacebookProfile_ExistingAccount_RequiresCurrentPassword()
    {
        using var db = TestDbContextFactory.Create();
        var account = new Account
        {
            DisplayName = "Ana López",
            Email = "ana@correo.com",
            Phone = "8681452290",
            PhoneVerifiedAt = DateTime.UtcNow,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("secret123")
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        var controller = Build(db);

        var result = await controller.CompleteFacebookProfile(
            SellerCompletion(existingPassword: null));

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        var continuation = Assert.IsType<FacebookContinuationResponse>(conflict.Value);
        Assert.True(continuation.RequiresExistingPassword);
        Assert.Null(account.FacebookUserId);
        Assert.Empty(await db.Businesses.ToListAsync());
    }

    [Fact]
    public async Task CompleteFacebookProfile_ExistingAccount_WithPassword_LinksAndCreatesBusiness()
    {
        using var db = TestDbContextFactory.Create();
        db.Accounts.Add(new Account
        {
            DisplayName = "Ana López",
            FirstName = "Ana",
            LastName = "López",
            Email = "ana@correo.com",
            Phone = "8681452290",
            PhoneVerifiedAt = DateTime.UtcNow,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("secret123")
        });
        await db.SaveChangesAsync();
        var controller = Build(db);

        var result = await controller.CompleteFacebookProfile(
            SellerCompletion(existingPassword: "secret123"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var session = Assert.IsType<LoginResponse>(ok.Value);
        Assert.Single(session.Memberships);
        var account = await db.Accounts.SingleAsync();
        Assert.Equal(FacebookUserId, account.FacebookUserId);
        Assert.Single(await db.Businesses.ToListAsync());
    }

    [Fact]
    public async Task FacebookLogin_TokenFromDifferentApp_IsRejected()
    {
        using var db = TestDbContextFactory.Create();
        var controller = Build(db, debugAppId: "another-facebook-app");

        var result = await controller.FacebookLogin(
            new FacebookLoginRequest("wrong-app-token", "client"));

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Empty(await db.Accounts.ToListAsync());
    }

    [Fact]
    public async Task FacebookLogin_ValidLimitedToken_ReturnsSession()
    {
        using var db = TestDbContextFactory.Create();
        db.Accounts.Add(new Account
        {
            DisplayName = "Ana López",
            Email = "ana@correo.com",
            Phone = "8681452290",
            PhoneVerifiedAt = DateTime.UtcNow,
            FacebookUserId = FacebookUserId
        });
        await db.SaveChangesAsync();
        var controller = Build(db);

        var result = await controller.FacebookLogin(
            new FacebookLoginRequest(
                CreateLimitedToken(FacebookAppId),
                "client",
                "limited"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<LoginResponse>(ok.Value);
    }

    [Fact]
    public async Task FacebookLogin_LimitedTokenWithWrongAudience_IsRejected()
    {
        using var db = TestDbContextFactory.Create();
        var controller = Build(db);

        var result = await controller.FacebookLogin(
            new FacebookLoginRequest(
                CreateLimitedToken("another-facebook-app"),
                "client",
                "limited"));

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Empty(await db.Accounts.ToListAsync());
    }

    [Fact]
    public async Task CompleteFacebookProfile_NewSellerWithLimitedToken_RequiresOtp()
    {
        using var db = TestDbContextFactory.Create();
        var controller = Build(db);

        var result = await controller.CompleteFacebookProfile(
            SellerCompletion(
                accessToken: CreateLimitedToken(FacebookAppId),
                tokenType: "limited"));

        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        var continuation = Assert.IsType<FacebookContinuationResponse>(accepted.Value);
        Assert.True(continuation.NeedsPhoneVerification);
        var account = await db.Accounts.SingleAsync();
        Assert.Equal(FacebookUserId, account.FacebookUserId);
        Assert.Null(account.PhoneVerifiedAt);
        Assert.Empty(await db.Businesses.ToListAsync());
    }

    [Fact]
    public async Task RegisterPhone_ReplacesUnverifiedFacebookLink()
    {
        using var db = TestDbContextFactory.Create();
        var controller = Build(db);
        await controller.CompleteFacebookProfile(SellerCompletion());

        await controller.RegisterPhone(new PhoneRegisterRequest(
            FirstName: "Ana",
            LastName: "López",
            Phone: "8681452290",
            Email: "ana@correo.com",
            Password: "new-secret-123"));

        var account = await db.Accounts.SingleAsync();
        Assert.Null(account.FacebookUserId);
        Assert.Null(account.ProfilePhotoUrl);

        await controller.ConfirmPhone(
            new VerifyPhoneLoginRequest("8681452290", "000000"));
        var facebookResult = await controller.FacebookLogin(
            new FacebookLoginRequest("valid-token", "client"));

        Assert.IsType<ConflictObjectResult>(facebookResult.Result);
    }

    private static FacebookCompleteProfileRequest SellerCompletion(
        string? existingPassword = null,
        string accessToken = "valid-token",
        string tokenType = "classic")
    {
        return new FacebookCompleteProfileRequest(
            AccessToken: accessToken,
            AccountType: "seller",
            FirstName: "Ana",
            LastName: "López",
            Email: "ana@correo.com",
            Phone: "8681452290",
            TokenType: tokenType,
            BusinessName: "Luna Bonita",
            City: "Matamoros",
            ExistingPassword: existingPassword);
    }

    private static string CreateLimitedToken(string audience)
    {
        using var rsa = RSA.Create();
        rsa.ImportParameters(LimitedRsaParameters);
        var signingKey = new RsaSecurityKey(rsa)
        {
            KeyId = LimitedKeyId,
            CryptoProviderFactory = new CryptoProviderFactory
            {
                CacheSignatureProviders = false
            }
        };
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: "https://www.facebook.com",
            audience: audience,
            claims:
            [
                new Claim("sub", FacebookUserId),
                new Claim("given_name", "Ana"),
                new Claim("family_name", "López"),
                new Claim("name", "Ana López"),
                new Claim("email", "ana@correo.com")
            ],
            notBefore: now.AddMinutes(-1),
            expires: now.AddMinutes(30),
            signingCredentials: new SigningCredentials(
                signingKey,
                SecurityAlgorithms.RsaSha256));
        token.Header["kid"] = LimitedKeyId;
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static RSAParameters CreateLimitedRsaParameters()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportParameters(includePrivateParameters: true);
    }

    private static string CreateLimitedJwks()
    {
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    kid = LimitedKeyId,
                    alg = "RS256",
                    n = Base64UrlEncoder.Encode(LimitedRsaParameters.Modulus),
                    e = Base64UrlEncoder.Encode(LimitedRsaParameters.Exponent)
                }
            }
        });
    }

    private static AuthController Build(
        AppDbContext db,
        string debugAppId = FacebookAppId)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "test-signing-key-for-facebook-tests",
                ["Jwt:Issuer"] = "tests",
                ["Jwt:Audience"] = "tests",
                ["Facebook:AppId"] = FacebookAppId,
                ["Facebook:AppSecret"] = "test-facebook-secret",
                ["Facebook:GraphApiVersion"] = "v25.0",
                ["Auth:DevOtpCode"] = "000000"
            })
            .Build();

        return new AuthController(
            db,
            new TokenService(config),
            new FakeHostEnvironment(),
            config,
            new FakePhoneVerificationService(),
            new FakeFacebookHttpClientFactory(debugAppId));
    }

    private sealed class FakeFacebookHttpClientFactory(string debugAppId)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new FacebookHandler(debugAppId));
        }
    }

    private sealed class FacebookHandler(string debugAppId) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var json = path.EndsWith("/openid/jwks/", StringComparison.Ordinal)
                ? CreateLimitedJwks()
                : path.EndsWith("/debug_token", StringComparison.Ordinal)
                ? $$"""
                  {
                    "data": {
                      "app_id": "{{debugAppId}}",
                      "is_valid": true,
                      "user_id": "{{FacebookUserId}}",
                      "expires_at": {{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}
                    }
                  }
                  """
                : $$"""
                  {
                    "id": "{{FacebookUserId}}",
                    "first_name": "Ana",
                    "last_name": "López",
                    "name": "Ana López",
                    "email": "ana@correo.com",
                    "picture": {
                      "data": {
                        "url": "https://example.com/profile.jpg"
                      }
                    }
                  }
                  """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
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
            if (digits?.Length == 12 &&
                digits.StartsWith("52", StringComparison.Ordinal))
            {
                return digits[2..];
            }

            return digits?.Length == 10 ? digits : null;
        }

        public Task<PhoneVerificationOutcome> SendCodeAsync(
            string normalizedPhone,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(PhoneVerificationOutcome.Sent);
        }

        public Task<PhoneVerificationOutcome> CheckCodeAsync(
            string normalizedPhone,
            string code,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(PhoneVerificationOutcome.Approved);
        }
    }
}
