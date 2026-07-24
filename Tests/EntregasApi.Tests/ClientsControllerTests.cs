using EntregasApi.Controllers;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace EntregasApi.Tests;

public class ClientsControllerTests
{
    [Fact]
    public async Task GetAll_And_GetById_ReturnAliasesAndFacebookProfileUrl()
    {
        using var ctx = TestDbContextFactory.Create();
        var client = new Client
        {
            BusinessId = 1,
            Name = "Sofia",
            NormalizedName = "sofia",
            FacebookProfileUrl = "https://facebook.com/sofia",
        };
        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();
        ctx.ClientAliases.AddRange(
            new ClientAlias
            {
                BusinessId = client.BusinessId,
                ClientId = client.Id,
                Alias = "Sofi",
                NormalizedAlias = "sofi",
                TimesSeen = 1,
            },
            new ClientAlias
            {
                BusinessId = client.BusinessId,
                ClientId = client.Id,
                Alias = "Sofia Live",
                NormalizedAlias = "sofia live",
                TimesSeen = 3,
            });
        await ctx.SaveChangesAsync();
        var controller = new ClientsController(ctx, null!, null!, null!);

        var allResult = await controller.GetAll();
        var allOk = Assert.IsType<OkObjectResult>(allResult.Result);
        var clients = Assert.IsAssignableFrom<List<ClientDto>>(allOk.Value);
        var listed = Assert.Single(clients);

        Assert.Equal(
            new List<string> { "Sofia Live", "Sofi" },
            listed.Aliases);
        Assert.Equal("https://facebook.com/sofia", listed.FacebookProfileUrl);

        var detailResult = await controller.GetById(client.Id);
        var detailOk = Assert.IsType<OkObjectResult>(detailResult.Result);
        var detail = Assert.IsType<ClientDto>(detailOk.Value);

        Assert.Equal(
            new List<string> { "Sofia Live", "Sofi" },
            detail.Aliases);
        Assert.Equal("https://facebook.com/sofia", detail.FacebookProfileUrl);
    }

    [Fact]
    public async Task Create_StandaloneClient_CreatesClientWithoutOrder()
    {
        using var ctx = TestDbContextFactory.Create();
        var controller = new ClientsController(ctx, null!, null!, null!);

        var req = new CreateClientRequest("Valeria Gomez", "8112345678", "Av. Constitución 100", "https://facebook.com/valeria.gomez");
        var result = await controller.Create(req);

        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<ClientDto>(createdResult.Value);

        Assert.Equal("Valeria Gomez", dto.Name);
        Assert.Equal("8112345678", dto.Phone);
        Assert.Equal("https://facebook.com/valeria.gomez", dto.FacebookProfileUrl);
        Assert.Equal(0, dto.OrdersCount);
        Assert.Equal(0, dto.TotalSpent);

        var inDb = await ctx.Clients.FindAsync(dto.Id);
        Assert.NotNull(inDb);
        Assert.Equal("valeria gomez", inDb!.NormalizedName);
    }
}

