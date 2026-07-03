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
        string? configuredOtpCode = null)
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

        return new AuthController(ctx, new TokenService(config), new FakeHostEnvironment(env), config);
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
    public async Task VerifyPhoneOtp_InProduction_StaysNotImplemented()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = Build(ctx, env: "Production");

        var result = await controller.VerifyPhoneOtp(new VerifyPhoneLoginRequest("8681452290", "000000"));

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status501NotImplemented, obj.StatusCode);
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public FakeHostEnvironment(string environmentName) => EnvironmentName = environmentName;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "EntregasApi.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
