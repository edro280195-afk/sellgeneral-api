using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EntregasApi.Data;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EntregasApi.Tests;

public sealed class MetaLiveProbeServiceTests
{
    private const string UserToken = "USER_TOKEN_SHOULD_NEVER_APPEAR_IN_URL";
    private const string PageToken = "PAGE_TOKEN_SHOULD_NEVER_LEAVE_SERVICE";

    [Fact]
    public async Task Probe_ReadsProfilePageLivesAndComments_WithoutLeakingTokens()
    {
        await using var db = NewContext();
        var account = new Account
        {
            DisplayName = "Eduardo",
            FacebookUserId = "100"
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var handler = new MetaGraphHandler();
        var service = NewService(db, account.Id, handler);

        var result = await service.ProbeAsync(UserToken);

        Assert.Equal("100", result.Profile.Id);
        Assert.Contains("pages_show_list", result.GrantedPermissions);
        Assert.Contains("publish_video", result.MissingPermissions);
        Assert.Null(result.PermissionsError);
        Assert.Null(result.PageDiscoveryError);
        Assert.False(result.PagesTruncated);

        var profile = Assert.Single(
            result.Sources,
            source => source.Type == "profile");
        var profileLive = Assert.Single(profile.Lives);
        Assert.Equal("LIVE", profileLive.Status);
        Assert.NotNull(profileLive.CreatedAt);
        var profileComment = Assert.Single(profileLive.Comments);
        Assert.Equal("Cliente Uno", profileComment.AuthorName);
        Assert.NotNull(profileComment.CreatedAt);

        var page = Assert.Single(
            result.Sources,
            source => source.Type == "page");
        Assert.Equal("Regi Bazar", page.Name);
        Assert.Contains("CREATE_CONTENT", page.Tasks);
        Assert.Single(page.Lives);

        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, request =>
        {
            Assert.DoesNotContain(UserToken, request.Uri, StringComparison.Ordinal);
            Assert.DoesNotContain(PageToken, request.Uri, StringComparison.Ordinal);
            Assert.True(
                request.Authorization == UserToken ||
                request.Authorization == PageToken);
        });

        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(UserToken, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(PageToken, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_WhenFacebookIsNotLinked_StopsBeforeCallingMeta()
    {
        await using var db = NewContext();
        var account = new Account
        {
            DisplayName = "Eduardo",
            Phone = "8680000000"
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var handler = new MetaGraphHandler();
        var service = NewService(db, account.Id, handler);

        var exception = await Assert.ThrowsAsync<MetaLiveProbeException>(
            () => service.ProbeAsync(UserToken));

        Assert.Equal(MetaLiveProbeFailure.IdentityNotLinked, exception.Failure);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Probe_WhenFacebookIdentityDoesNotMatch_RejectsConnection()
    {
        await using var db = NewContext();
        var account = new Account
        {
            DisplayName = "Eduardo",
            FacebookUserId = "999"
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var handler = new MetaGraphHandler();
        var service = NewService(db, account.Id, handler);

        var exception = await Assert.ThrowsAsync<MetaLiveProbeException>(
            () => service.ProbeAsync(UserToken));

        Assert.Equal(MetaLiveProbeFailure.IdentityMismatch, exception.Failure);
        Assert.Single(handler.Requests);
    }

    private static AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"MetaLiveProbe_{Guid.NewGuid():N}")
            .Options;
        return new AppDbContext(options);
    }

    private static MetaLiveProbeService NewService(
        AppDbContext db,
        int accountId,
        HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Facebook:AppId"] = "test-app",
                ["Facebook:AppSecret"] = "test-secret",
                ["Facebook:GraphApiVersion"] = "v25.0"
            })
            .Build();
        return new MetaLiveProbeService(
            db,
            new FakeCurrentAccount(accountId),
            new FakeHttpClientFactory(handler),
            configuration);
    }

    private sealed class FakeCurrentAccount(int accountId) : ICurrentAccount
    {
        public int? AccountId { get; } = accountId;
        public bool IsAuthenticated => true;
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler)
        : IHttpClientFactory
    {
        private readonly HttpClient _client = new(handler);

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class MetaGraphHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri
                ?? throw new InvalidOperationException("Request sin URI.");
            var authorization = ReadBearer(request.Headers.Authorization);
            Requests.Add(new CapturedRequest(uri.ToString(), authorization));

            var path = uri.AbsolutePath;
            var json = path switch
            {
                "/v25.0/me" => """
                    {"id":"100","name":"Eduardo"}
                    """,
                "/v25.0/me/permissions" => """
                    {
                      "data": [
                        {"permission":"public_profile","status":"granted"},
                        {"permission":"pages_show_list","status":"granted"},
                        {"permission":"pages_read_engagement","status":"granted"},
                        {"permission":"pages_read_user_content","status":"declined"}
                      ]
                    }
                    """,
                "/v25.0/me/accounts" => $$"""
                    {
                      "data": [
                        {
                          "id":"200",
                          "name":"Regi Bazar",
                          "access_token":"{{PageToken}}",
                          "tasks":["CREATE_CONTENT","MODERATE"]
                        }
                      ]
                    }
                    """,
                "/v25.0/me/live_videos" => """
                    {
                      "data": [
                        {
                          "id":"300",
                          "status":"LIVE",
                          "title":"Live de perfil",
                          "permalink_url":"/videos/300",
                          "creation_time":"2026-07-27T12:00:00+0000"
                        }
                      ]
                    }
                    """,
                "/v25.0/200/live_videos" => """
                    {
                      "data": [
                        {
                          "id":"301",
                          "status":"VOD",
                          "title":"Live de página",
                          "permalink_url":"/videos/301",
                          "creation_time":"2026-07-26T12:00:00+0000"
                        }
                      ]
                    }
                    """,
                "/v25.0/300/comments" => """
                    {
                      "data": [
                        {
                          "id":"400",
                          "from":{"id":"500","name":"Cliente Uno"},
                          "message":"Mío 12",
                          "created_time":"2026-07-27T12:01:00+0000"
                        }
                      ]
                    }
                    """,
                "/v25.0/301/comments" => """
                    {"data":[]}
                    """,
                _ => throw new InvalidOperationException(
                    $"Ruta de Meta no preparada en la prueba: {path}")
            };

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
            return Task.FromResult(response);
        }

        private static string? ReadBearer(AuthenticationHeaderValue? header) =>
            string.Equals(header?.Scheme, "Bearer", StringComparison.Ordinal)
                ? header?.Parameter
                : null;
    }

    private sealed record CapturedRequest(string Uri, string? Authorization);
}
