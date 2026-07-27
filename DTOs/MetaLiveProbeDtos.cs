using System.ComponentModel.DataAnnotations;

namespace EntregasApi.DTOs;

public sealed record MetaLiveProbeRequest(
    [property: Required, StringLength(16_384)] string AccessToken);

public sealed record MetaLiveProbeDto(
    MetaLiveIdentityDto Profile,
    IReadOnlyList<string> GrantedPermissions,
    IReadOnlyList<string> DeclinedPermissions,
    IReadOnlyList<string> MissingPermissions,
    string? PermissionsError,
    string? PageDiscoveryError,
    bool PagesTruncated,
    IReadOnlyList<MetaLiveSourceDto> Sources,
    DateTimeOffset CheckedAtUtc);

public sealed record MetaLiveIdentityDto(
    string Id,
    string Name);

public sealed record MetaLiveSourceDto(
    string Type,
    string Id,
    string Name,
    IReadOnlyList<string> Tasks,
    string? Error,
    IReadOnlyList<MetaLiveVideoDto> Lives);

public sealed record MetaLiveVideoDto(
    string Id,
    string Status,
    string? Title,
    string? Description,
    string? PermalinkUrl,
    DateTimeOffset? CreatedAt,
    string? CommentsError,
    IReadOnlyList<MetaLiveCommentDto> Comments);

public sealed record MetaLiveCommentDto(
    string Id,
    string? AuthorId,
    string? AuthorName,
    string Message,
    DateTimeOffset? CreatedAt);
