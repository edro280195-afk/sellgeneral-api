using System.Net.Http;
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

public class AuthControllerDevOtpTests
{
    private static AuthController Build(
        AppDbContext ctx,
        string env = "Development",
        string? configuredOtpCode = null,
        IPhoneVerificationService? phoneVerification = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "test-signing-key-for-dev-otp-tests",
                ["Jwt:Issuer"] = "tests",
                ["Jwt:Audience"] = "tests",
                ["Auth:DevOtpCode"] = configuredOtpCode
            })
            .Build();

        return new AuthController(
            ctx,
            new TokenService(config),
            new FakeHostEnvironment(env),
            config,
            phoneVerification ?? new FakePhoneVerificationService(),
            new FakeHttpClientFactory());
    }

    [Fact]
    public async Task VerifyPhoneOtp_DevCode_NewPhone_CreatesAccountAndReturnsToken()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = Build(ctx);

        var result = await controller.VerifyPhoneOtp(new VerifyPhoneLoginRequest("868-145-2290", "000000"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var resp = Assert.IsType<LoginResponse>(ok.Value);
        Assert.True(resp.AccountId > 0);
        Assert.False(string.IsNullOrWhiteSpace(resp.Token));

        var account = await ctx.Accounts.SingleAsync();
        Assert.False(string.IsNullOrWhiteSpace(account.Phone));
        Assert.Empty(account.Memberships);
    }

    [Fact]
    public async Task VerifyPhoneOtp_DevCode_SamePhoneTwice_DoesNotDuplicate()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = Build(ctx);

        await controller.VerifyPhoneOtp(new VerifyPhoneLoginRequest("8681452290", "000000"));
        await controller.VerifyPhoneOtp(new VerifyPhoneLoginRequest("8681452290", "000000"));

        Assert.Equal(1, await ctx.Accounts.CountAsync());
    }

    [Fact]
    public async Task VerifyPhoneOtp_WrongCode_Unauthorized_AndCreatesNothing()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = Build(ctx);

        var result = await controller.VerifyPhoneOtp(new VerifyPhoneLoginRequest("8681452290", "111111"));

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Equal(0, await ctx.Accounts.CountAsync());
    }

    [Fact]
    public async Task VerifyPhoneOtp_InvalidConfiguredCode_FallsBackToSixDigitDevCode()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = Build(ctx, configuredOtpCode: "not-a-six-digit-code");

        var result = await controller.VerifyPhoneOtp(
            new VerifyPhoneLoginRequest("8681452290", "000000"));

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(1, await ctx.Accounts.CountAsync());
    }

    [Fact]
    public async Task VerifyPhoneOtp_InProduction_WithoutProvider_ReturnsServiceUnavailable()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = Build(ctx, env: "Production");

        var result = await controller.VerifyPhoneOtp(new VerifyPhoneLoginRequest("8681452290", "000000"));

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, obj.StatusCode);
    }

    [Fact]
    public async Task VerifyPhoneOtp_InProduction_ApprovedByProvider_CreatesAccount()
    {
        using var ctx = TestDbContextFactory.Create();
        var provider = new FakePhoneVerificationService(
            configured: true,
            checkOutcome: PhoneVerificationOutcome.Approved);
        var controller = Build(ctx, env: "Production", phoneVerification: provider);

        var result = await controller.VerifyPhoneOtp(
            new VerifyPhoneLoginRequest("+52 868 145 2290", "123456"));

        Assert.IsType<OkObjectResult>(result.Result);
        var account = await ctx.Accounts.SingleAsync();
        Assert.Equal("8681452290", account.Phone);
    }

    // ── Nuevo flujo: registro por teléfono + contraseña (confirmación WhatsApp) ──

    [Fact]
    public async Task RegisterPhone_ThenConfirm_CreatesVerifiedAccountWithName()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = Build(ctx);

        var register = await controller.RegisterPhone(new PhoneRegisterRequest(
            "Ana", "López", "868-145-2290", "ana@correo.com", "secret123"));
        Assert.IsType<AcceptedResult>(register);

        var confirm = await controller.ConfirmPhone(
            new VerifyPhoneLoginRequest("8681452290", "000000"));
        var ok = Assert.IsType<OkObjectResult>(confirm.Result);
        var resp = Assert.IsType<LoginResponse>(ok.Value);
        Assert.Equal("Ana López", resp.Name);

        var account = await ctx.Accounts.SingleAsync();
        Assert.Equal("8681452290", account.Phone);
        Assert.Equal("ana@correo.com", account.Email);
        Assert.NotNull(account.PhoneVerifiedAt);
    }

    [Fact]
    public async Task LoginPhone_AfterConfirm_ReturnsToken()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = Build(ctx);

        await controller.RegisterPhone(new PhoneRegisterRequest(
            "Ana", "López", "8681452290", "ana@correo.com", "secret123"));
        await controller.ConfirmPhone(new VerifyPhoneLoginRequest("8681452290", "000000"));

        var login = await controller.LoginPhone(
            new PhonePasswordLoginRequest("8681452290", "secret123"));

        var ok = Assert.IsType<OkObjectResult>(login.Result);
        Assert.IsType<LoginResponse>(ok.Value);
    }

    [Fact]
    public async Task LoginPhone_WrongPassword_Unauthorized()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = Build(ctx);

        await controller.RegisterPhone(new PhoneRegisterRequest(
            "Ana", "López", "8681452290", "ana@correo.com", "secret123"));
        await controller.ConfirmPhone(new VerifyPhoneLoginRequest("8681452290", "000000"));

        var login = await controller.LoginPhone(
            new PhonePasswordLoginRequest("8681452290", "otraClave"));

        Assert.IsType<UnauthorizedObjectResult>(login.Result);
    }

    [Fact]
    public async Task LoginPhone_BeforeConfirm_ReturnsNeedsVerification()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = Build(ctx);

        await controller.RegisterPhone(new PhoneRegisterRequest(
            "Ana", "López", "8681452290", "ana@correo.com", "secret123"));

        var login = await controller.LoginPhone(
            new PhonePasswordLoginRequest("8681452290", "secret123"));

        var obj = Assert.IsType<ObjectResult>(login.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
    }

    [Fact]
    public async Task RegisterPhone_AlreadyVerified_Conflict()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = Build(ctx);

        await controller.RegisterPhone(new PhoneRegisterRequest(
            "Ana", "López", "8681452290", "ana@correo.com", "secret123"));
        await controller.ConfirmPhone(new VerifyPhoneLoginRequest("8681452290", "000000"));

        var again = await controller.RegisterPhone(new PhoneRegisterRequest(
            "Ana", "López", "8681452290", "ana@correo.com", "secret123"));

        Assert.IsType<ConflictObjectResult>(again);
    }

    [Fact]
    public async Task RegisterPhone_PasswordLongerThan128_BadRequest()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = Build(ctx);

        var result = await controller.RegisterPhone(new PhoneRegisterRequest(
            "Ana",
            "López",
            "8681452290",
            "ana@correo.com",
            new string('x', 129)));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(0, await ctx.Accounts.CountAsync());
    }

    [Fact]
    public async Task RegisterLegacy_PasswordLongerThan128_BadRequest()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = Build(ctx);

        var result = await controller.Register(new RegisterRequest(
            "Ana López",
            "ana@correo.com",
            new string('x', 129)));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(0, await ctx.Accounts.CountAsync());
    }

    [Fact]
    public async Task RequestPasswordReset_VerifiedPhone_ReturnsAccepted()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = Build(ctx);
        await controller.RegisterPhone(new PhoneRegisterRequest(
            "Ana", "López", "8681452290", "ana@correo.com", "secret123"));
        await controller.ConfirmPhone(
            new VerifyPhoneLoginRequest("8681452290", "000000"));

        var result = await controller.RequestPasswordReset(
            new PasswordResetRequest("868-145-2290"));

        Assert.IsType<AcceptedResult>(result);
    }

    [Fact]
    public async Task ConfirmPasswordReset_ValidCode_ReplacesPassword()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = Build(ctx);
        await controller.RegisterPhone(new PhoneRegisterRequest(
            "Ana", "López", "8681452290", "ana@correo.com", "secret123"));
        await controller.ConfirmPhone(
            new VerifyPhoneLoginRequest("8681452290", "000000"));

        var result = await controller.ConfirmPasswordReset(
            new ConfirmPasswordResetRequest(
                "8681452290",
                "000000",
                "nueva-clave-123"));

        Assert.IsType<OkObjectResult>(result);
        var account = await ctx.Accounts.SingleAsync();
        Assert.True(BCrypt.Net.BCrypt.Verify(
            "nueva-clave-123",
            account.PasswordHash));
        Assert.False(BCrypt.Net.BCrypt.Verify(
            "secret123",
            account.PasswordHash));

        var login = await controller.LoginPhone(
            new PhonePasswordLoginRequest("8681452290", "nueva-clave-123"));
        Assert.IsType<OkObjectResult>(login.Result);
    }

    [Fact]
    public async Task ConfirmPasswordReset_WrongCode_DoesNotChangePassword()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = Build(ctx);
        await controller.RegisterPhone(new PhoneRegisterRequest(
            "Ana", "López", "8681452290", "ana@correo.com", "secret123"));
        await controller.ConfirmPhone(
            new VerifyPhoneLoginRequest("8681452290", "000000"));

        var result = await controller.ConfirmPasswordReset(
            new ConfirmPasswordResetRequest(
                "8681452290",
                "111111",
                "nueva-clave-123"));

        Assert.IsType<UnauthorizedObjectResult>(result);
        var account = await ctx.Accounts.SingleAsync();
        Assert.True(BCrypt.Net.BCrypt.Verify(
            "secret123",
            account.PasswordHash));
    }

    [Fact]
    public async Task ConfirmPasswordReset_UnverifiedPhone_IsRejected()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = Build(ctx);
        await controller.RegisterPhone(new PhoneRegisterRequest(
            "Ana", "López", "8681452290", "ana@correo.com", "secret123"));

        var result = await controller.ConfirmPasswordReset(
            new ConfirmPasswordResetRequest(
                "8681452290",
                "000000",
                "nueva-clave-123"));

        Assert.IsType<UnauthorizedObjectResult>(result);
        var account = await ctx.Accounts.SingleAsync();
        Assert.True(BCrypt.Net.BCrypt.Verify(
            "secret123",
            account.PasswordHash));
    }

    [Fact]
    public async Task FacebookLogin_NotConfigured_ReturnsNotImplemented()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = Build(ctx);

        var result = await controller.FacebookLogin(new FacebookLoginRequest("fake-token"));

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status501NotImplemented, obj.StatusCode);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ThrowingHandler());

        private sealed class ThrowingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Sin red en tests.");
        }
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public FakeHostEnvironment(string environmentName) => EnvironmentName = environmentName;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "EntregasApi.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakePhoneVerificationService(
        bool configured = false,
        PhoneVerificationOutcome checkOutcome = PhoneVerificationOutcome.Invalid)
        : IPhoneVerificationService
    {
        public bool IsConfigured => configured;

        public string? NormalizePhone(string? input)
        {
            var digits = TextNormalizer.NormalizePhone(input);
            if (digits?.Length == 12 && digits.StartsWith("52", StringComparison.Ordinal))
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
            return Task.FromResult(checkOutcome);
        }
    }
}
