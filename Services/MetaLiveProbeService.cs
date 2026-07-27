using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EntregasApi.Data;
using EntregasApi.DTOs;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public interface IMetaLiveProbeService
{
    Task<MetaLiveProbeDto> ProbeAsync(
        string accessToken,
        CancellationToken cancellationToken = default);
}

public enum MetaLiveProbeFailure
{
    ConfigurationUnavailable,
    IdentityNotLinked,
    IdentityMismatch,
    ProviderRejected,
    ProviderUnavailable
}

public sealed class MetaLiveProbeException : Exception
{
    public MetaLiveProbeException(MetaLiveProbeFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    public MetaLiveProbeFailure Failure { get; }
}

/// <summary>
/// Diagnóstico transitorio de Meta Live. Los access tokens solo viven durante
/// esta petición, viajan a Graph en el header Authorization y nunca se
/// persisten ni forman parte de la respuesta.
/// </summary>
public sealed class MetaLiveProbeService : IMetaLiveProbeService
{
    private const int MaxPages = 5;
    private const int MaxLivesPerSource = 3;
    private const int MaxCommentsPerLive = 10;

    private static readonly string[] RequiredPermissions =
    [
        "public_profile",
        "publish_video",
        "pages_show_list",
        "pages_read_engagement",
        "pages_read_user_content"
    ];

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly AppDbContext _db;
    private readonly ICurrentAccount _currentAccount;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _appId;
    private readonly string _appSecret;
    private readonly string _graphApiVersion;

    public MetaLiveProbeService(
        AppDbContext db,
        ICurrentAccount currentAccount,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _db = db;
        _currentAccount = currentAccount;
        _httpClientFactory = httpClientFactory;
        _appId = configuration["Facebook:AppId"]?.Trim() ?? string.Empty;
        _appSecret = configuration["Facebook:AppSecret"]?.Trim() ?? string.Empty;
        _graphApiVersion = NormalizeGraphApiVersion(
            configuration["Facebook:GraphApiVersion"]);
    }

    public async Task<MetaLiveProbeDto> ProbeAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        EnsureConfiguration();

        var accountId = _currentAccount.AccountId;
        if (accountId is null)
        {
            throw new MetaLiveProbeException(
                MetaLiveProbeFailure.IdentityNotLinked,
                "Tu sesión de Neni’s no contiene una cuenta válida.");
        }

        var linkedFacebookId = await _db.Accounts
            .AsNoTracking()
            .Where(account => account.Id == accountId.Value)
            .Select(account => account.FacebookUserId)
            .SingleOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(linkedFacebookId))
        {
            throw new MetaLiveProbeException(
                MetaLiveProbeFailure.IdentityNotLinked,
                "Primero inicia sesión en Neni’s con Facebook para vincular tu identidad.");
        }

        var profileResult = await GetAsync<GraphProfile>(
            "me",
            accessToken,
            new Dictionary<string, string> { ["fields"] = "id,name" },
            cancellationToken);

        if (!profileResult.IsSuccess || profileResult.Value is null)
        {
            throw new MetaLiveProbeException(
                profileResult.IsUnavailable
                    ? MetaLiveProbeFailure.ProviderUnavailable
                    : MetaLiveProbeFailure.ProviderRejected,
                profileResult.IsUnavailable
                    ? "Facebook no respondió. Intenta de nuevo en unos minutos."
                    : "Facebook rechazó el acceso. Revisa los permisos e intenta otra vez.");
        }

        var profile = profileResult.Value;
        if (!string.Equals(
                linkedFacebookId,
                profile.Id,
                StringComparison.Ordinal))
        {
            throw new MetaLiveProbeException(
                MetaLiveProbeFailure.IdentityMismatch,
                "El Facebook autorizado no coincide con el vinculado a tu cuenta de Neni’s.");
        }

        var permissionsTask = GetPermissionsAsync(accessToken, cancellationToken);
        var pagesTask = GetPagesAsync(accessToken, cancellationToken);
        await Task.WhenAll(permissionsTask, pagesTask);

        var permissions = await permissionsTask;
        var pages = await pagesTask;
        var granted = permissions.Value
            .Where(permission => permission.Status == "granted")
            .Select(permission => permission.Permission)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var declined = permissions.Value
            .Where(permission => permission.Status == "declined")
            .Select(permission => permission.Permission)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var missing = RequiredPermissions
            .Except(granted, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var sourceTasks = new List<Task<MetaLiveSourceDto>>
        {
            GetSourceAsync(
                "profile",
                profile.Id,
                profile.Name,
                [],
                accessToken,
                useMeAlias: true,
                cancellationToken)
        };
        sourceTasks.AddRange(
            pages.Value
                .Take(MaxPages)
                .Where(page => IsGraphId(page.Id) &&
                               !string.IsNullOrWhiteSpace(page.AccessToken))
                .Select(page => GetSourceAsync(
                    "page",
                    page.Id,
                    page.Name,
                    page.Tasks ?? [],
                    page.AccessToken!,
                    useMeAlias: false,
                    cancellationToken)));

        var sources = await Task.WhenAll(sourceTasks);

        return new MetaLiveProbeDto(
            new MetaLiveIdentityDto(profile.Id, profile.Name),
            granted,
            declined,
            missing,
            permissions.Error,
            pages.Error,
            pages.Value.Count > MaxPages,
            sources,
            DateTimeOffset.UtcNow);
    }

    private async Task<ProbeCollection<GraphPermission>> GetPermissionsAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var result = await GetAsync<GraphDataEnvelope<GraphPermission>>(
            "me/permissions",
            accessToken,
            new Dictionary<string, string> { ["limit"] = "100" },
            cancellationToken);

        return result.IsSuccess && result.Value is not null
            ? new ProbeCollection<GraphPermission>(result.Value.Data ?? [], null)
            : new ProbeCollection<GraphPermission>([], result.SafeError);
    }

    private async Task<ProbeCollection<GraphPage>> GetPagesAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var result = await GetAsync<GraphDataEnvelope<GraphPage>>(
            "me/accounts",
            accessToken,
            new Dictionary<string, string>
            {
                ["fields"] = "id,name,tasks,access_token",
                ["limit"] = "100"
            },
            cancellationToken);

        return result.IsSuccess && result.Value is not null
            ? new ProbeCollection<GraphPage>(result.Value.Data ?? [], null)
            : new ProbeCollection<GraphPage>([], result.SafeError);
    }

    private async Task<MetaLiveSourceDto> GetSourceAsync(
        string type,
        string id,
        string name,
        IReadOnlyList<string> tasks,
        string accessToken,
        bool useMeAlias,
        CancellationToken cancellationToken)
    {
        var sourcePath = useMeAlias ? "me" : id;
        var result = await GetAsync<GraphDataEnvelope<GraphLiveVideo>>(
            $"{sourcePath}/live_videos",
            accessToken,
            new Dictionary<string, string>
            {
                ["fields"] =
                    "id,status,title,description,permalink_url,creation_time",
                ["limit"] = MaxLivesPerSource.ToString(CultureInfo.InvariantCulture)
            },
            cancellationToken);

        if (!result.IsSuccess || result.Value is null)
        {
            return new MetaLiveSourceDto(
                type,
                id,
                name,
                tasks,
                result.SafeError,
                []);
        }

        var lives = new List<MetaLiveVideoDto>();
        foreach (var live in result.Value.Data ?? [])
        {
            if (!IsGraphId(live.Id))
            {
                continue;
            }

            var comments = await GetCommentsAsync(
                live.Id,
                accessToken,
                cancellationToken);
            lives.Add(new MetaLiveVideoDto(
                live.Id,
                live.Status ?? "UNKNOWN",
                live.Title,
                live.Description,
                live.PermalinkUrl,
                ParseMetaDate(live.CreationTime),
                comments.Error,
                comments.Value
                    .Select(comment => new MetaLiveCommentDto(
                        comment.Id,
                        comment.From?.Id,
                        comment.From?.Name,
                        comment.Message ?? string.Empty,
                        ParseMetaDate(comment.CreatedTime)))
                    .ToArray()));
        }

        return new MetaLiveSourceDto(type, id, name, tasks, null, lives);
    }

    private async Task<ProbeCollection<GraphComment>> GetCommentsAsync(
        string liveId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var result = await GetAsync<GraphDataEnvelope<GraphComment>>(
            $"{liveId}/comments",
            accessToken,
            new Dictionary<string, string>
            {
                ["fields"] = "id,from{id,name},message,created_time",
                ["filter"] = "stream",
                ["order"] = "reverse_chronological",
                ["limit"] = MaxCommentsPerLive.ToString(CultureInfo.InvariantCulture)
            },
            cancellationToken);

        return result.IsSuccess && result.Value is not null
            ? new ProbeCollection<GraphComment>(result.Value.Data ?? [], null)
            : new ProbeCollection<GraphComment>([], result.SafeError);
    }

    private async Task<GraphCall<T>> GetAsync<T>(
        string path,
        string accessToken,
        IReadOnlyDictionary<string, string> query,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildGraphUri(path, query, accessToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            var client = _httpClientFactory.CreateClient("facebook");
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return GraphCall<T>.Rejected(response.StatusCode);
            }

            await using var stream =
                await response.Content.ReadAsStreamAsync(cancellationToken);
            var value = await JsonSerializer.DeserializeAsync<T>(
                stream,
                JsonOptions,
                cancellationToken);
            return value is null
                ? GraphCall<T>.Rejected(HttpStatusCode.BadGateway)
                : GraphCall<T>.Success(value);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GraphCall<T>.Unavailable();
        }
        catch (HttpRequestException)
        {
            return GraphCall<T>.Unavailable();
        }
        catch (JsonException)
        {
            return GraphCall<T>.Rejected(HttpStatusCode.BadGateway);
        }
    }

    private Uri BuildGraphUri(
        string path,
        IReadOnlyDictionary<string, string> query,
        string accessToken)
    {
        var proof = Convert.ToHexString(
                HMACSHA256.HashData(
                    Encoding.UTF8.GetBytes(_appSecret),
                    Encoding.UTF8.GetBytes(accessToken)))
            .ToLowerInvariant();
        var values = query
            .Append(new KeyValuePair<string, string>("appsecret_proof", proof))
            .Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}");
        return new Uri(
            $"https://graph.facebook.com/{_graphApiVersion}/{path}?{string.Join("&", values)}");
    }

    private void EnsureConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_appId) ||
            string.IsNullOrWhiteSpace(_appSecret))
        {
            throw new MetaLiveProbeException(
                MetaLiveProbeFailure.ConfigurationUnavailable,
                "La conexión de Facebook todavía no está configurada en el servidor.");
        }
    }

    private static bool IsGraphId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.All(char.IsDigit);

    private static DateTimeOffset? ParseMetaDate(string? raw) =>
        DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;

    private static string NormalizeGraphApiVersion(string? version)
    {
        var trimmed = version?.Trim();
        return !string.IsNullOrWhiteSpace(trimmed) &&
               trimmed[0] == 'v' &&
               trimmed.Skip(1).All(character =>
                   char.IsDigit(character) || character == '.')
            ? trimmed
            : "v25.0";
    }

    private sealed record ProbeCollection<T>(
        IReadOnlyList<T> Value,
        string? Error);

    private sealed record GraphCall<T>(
        bool IsSuccess,
        bool IsUnavailable,
        T? Value,
        HttpStatusCode? StatusCode)
    {
        public string SafeError => IsUnavailable
            ? "Facebook no respondió."
            : $"Facebook rechazó esta consulta ({(int)(StatusCode ?? HttpStatusCode.BadGateway)}).";

        public static GraphCall<T> Success(T value) =>
            new(true, false, value, null);

        public static GraphCall<T> Rejected(HttpStatusCode statusCode) =>
            new(false, false, default, statusCode);

        public static GraphCall<T> Unavailable() =>
            new(false, true, default, null);
    }

    private sealed record GraphProfile(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name);

    private sealed record GraphDataEnvelope<T>(
        [property: JsonPropertyName("data")] IReadOnlyList<T>? Data);

    private sealed record GraphPermission(
        [property: JsonPropertyName("permission")] string Permission,
        [property: JsonPropertyName("status")] string Status);

    private sealed record GraphPage(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("tasks")] IReadOnlyList<string>? Tasks);

    private sealed record GraphLiveVideo(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("permalink_url")] string? PermalinkUrl,
        [property: JsonPropertyName("creation_time")] string? CreationTime);

    private sealed record GraphComment(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("from")] GraphIdentity? From,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("created_time")] string? CreatedTime);

    private sealed record GraphIdentity(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name);
}
