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
}
