using EntregasApi.Controllers;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntregasApi.Tests;

public class BusinessPayoutAccountsControllerTests
{
    private const string ValidClabe = "032180000118359719";

    [Fact]
    public async Task Create_FirstAccount_IsDefaultAndDoesNotExposeFullNumber()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = BuildController(ctx);

        var result = await controller.Create(new CreatePayoutAccountRequest(
            Kind: "clabe",
            HolderName: "Yazmin Vara",
            AccountNumber: ValidClabe,
            BankName: "BBVA",
            Alias: "Principal"), default);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<PayoutAccountDto>(created.Value);
        Assert.Equal("clabe", dto.Kind);
        Assert.True(dto.IsDefault);
        Assert.Equal("**** **** **** **9719", dto.MaskedNumber);
        Assert.DoesNotContain(ValidClabe, dto.ToString());
    }

    [Fact]
    public async Task Create_RejectsInvalidClabeCheckDigit()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = BuildController(ctx);

        var result = await controller.Create(new CreatePayoutAccountRequest(
            Kind: "clabe",
            HolderName: "Yazmin Vara",
            AccountNumber: "032180000118359718",
            BankName: "BBVA"), default);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("CLABE", badRequest.Value!.ToString());
        Assert.Empty(ctx.PayoutAccounts);
    }

    [Fact]
    public async Task Create_NewDefault_ClearsPreviousDefault()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = BuildController(ctx);

        var first = await controller.Create(new CreatePayoutAccountRequest(
            Kind: "clabe",
            HolderName: "Yazmin Vara",
            AccountNumber: ValidClabe,
            BankName: "BBVA"), default);
        var firstDto = Assert.IsType<PayoutAccountDto>(
            Assert.IsType<CreatedResult>(first.Result).Value);

        var second = await controller.Create(new CreatePayoutAccountRequest(
            Kind: "debitCard",
            HolderName: "Yazmin Vara",
            AccountNumber: "1234 5678 9012 3456",
            BankName: "Banorte",
            IsDefault: true), default);
        var secondDto = Assert.IsType<PayoutAccountDto>(
            Assert.IsType<CreatedResult>(second.Result).Value);

        var stored = await ctx.PayoutAccounts.AsNoTracking()
            .OrderBy(account => account.Id)
            .ToListAsync();

        Assert.False(stored.Single(account => account.Id == firstDto.Id).IsDefault);
        Assert.True(stored.Single(account => account.Id == secondDto.Id).IsDefault);
    }

    [Fact]
    public async Task Delete_DefaultAccount_PromotesAnotherActiveAccount()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = BuildController(ctx);

        var first = await controller.Create(new CreatePayoutAccountRequest(
            Kind: "clabe",
            HolderName: "Yazmin Vara",
            AccountNumber: ValidClabe,
            BankName: "BBVA"), default);
        var firstDto = Assert.IsType<PayoutAccountDto>(
            Assert.IsType<CreatedResult>(first.Result).Value);

        var second = await controller.Create(new CreatePayoutAccountRequest(
            Kind: "bankAccount",
            HolderName: "Yazmin Vara",
            AccountNumber: "1234567890",
            BankName: "Santander"), default);
        var secondDto = Assert.IsType<PayoutAccountDto>(
            Assert.IsType<CreatedResult>(second.Result).Value);

        var delete = await controller.Delete(firstDto.Id, default);
        Assert.IsType<NoContentResult>(delete);

        var promoted = await ctx.PayoutAccounts.AsNoTracking()
            .SingleAsync(account => account.Id == secondDto.Id);
        Assert.True(promoted.IsDefault);
        Assert.True(promoted.IsActive);
    }

    private static BusinessPayoutAccountsController BuildController(
        AppDbContext db,
        int businessId = 1)
    {
        var controller = new BusinessPayoutAccountsController(
            db,
            new TestCurrentTenant(businessId))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        return controller;
    }

    private sealed class TestCurrentTenant : ICurrentTenant
    {
        public TestCurrentTenant(int businessId) => ActiveBusinessId = businessId;
        public int ActiveBusinessId { get; private set; }
        public bool IsResolved => true;
        public void SetBusiness(int businessId) => ActiveBusinessId = businessId;
    }
}
