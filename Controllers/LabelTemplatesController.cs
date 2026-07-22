using System.Security.Claims;
using System.Text.Json;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers;

/// <summary>
/// Editor de plantillas de etiquetas. Cada publicación permanece inmutable;
/// el borrador siempre es una versión aparte para no alterar reimpresiones.
/// </summary>
[ApiController]
[Route("api/label-templates")]
[Authorize(Policy = AuthorizationPolicies.Admin)]
[RequiresFeature(Feature.LabelPrinting)]
public sealed class LabelTemplatesController(
    AppDbContext db,
    LabelTemplateCatalogService templateCatalog,
    ILabelTemplateDesignValidator designValidator,
    ICloudinaryService cloudinary) : ControllerBase
{
    private const long MaxAssetBytes = 3L * 1024 * 1024;
    private static readonly HashSet<string> AllowedImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/jpg", "image/webp"
    };

    [HttpGet("default")]
    public async Task<ActionResult<LabelTemplateEditorDto>> GetDefault(
        [FromQuery] string kind,
        [FromQuery] string mediaSize,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<LabelTemplateKind>(kind, ignoreCase: true, out var parsedKind) || !Enum.IsDefined(parsedKind))
        {
            return BadRequest(new { message = "El tipo de etiqueta no es válido." });
        }
        if (!TryParseMediaSize(mediaSize, out var parsedMediaSize))
        {
            return BadRequest(new { message = "El formato de etiqueta no es válido." });
        }

        var template = await templateCatalog.GetOrCreateDefaultTemplateAsync(
            parsedKind,
            parsedMediaSize,
            CurrentUserName(),
            cancellationToken);
        var loaded = await LoadTemplateAsync(template.Id, cancellationToken);
        return Ok(MapTemplate(loaded!));
    }

    [HttpGet("order-package/default")]
    public async Task<ActionResult<LabelTemplateEditorDto>> GetOrderPackageDefault(
        [FromQuery] string mediaSize,
        CancellationToken cancellationToken)
    {
        if (!TryParseMediaSize(mediaSize, out var parsedMediaSize))
        {
            return BadRequest(new { message = "El formato de etiqueta no es válido." });
        }

        var template = await templateCatalog.GetOrCreateDefaultOrderPackageTemplateAsync(
            parsedMediaSize,
            CurrentUserName(),
            cancellationToken);
        var loaded = await LoadTemplateAsync(template.Id, cancellationToken);
        return Ok(MapTemplate(loaded!));
    }

    [HttpPut("{id:guid}/draft")]
    public async Task<ActionResult<LabelTemplateEditorDto>> SaveDraft(
        Guid id,
        [FromBody] SaveLabelTemplateDraftRequest request,
        CancellationToken cancellationToken)
    {
        var template = await LoadTemplateAsync(id, cancellationToken);
        if (template is null)
        {
            return NotFound(new { message = "No encontramos esta plantilla." });
        }

        var draft = GetDraft(template);
        if (draft is null)
        {
            return Conflict(new { message = "La plantilla no tiene un borrador editable." });
        }
        if (request.ExpectedRevision != draft.Revision)
        {
            return Conflict(new
            {
                message = "La plantilla cambió en otro dispositivo. Recarga antes de guardar para no perder cambios.",
                revision = draft.Revision
            });
        }

        var validation = designValidator.Validate(request.DesignJson, template.Kind, template.MediaSize);
        if (!validation.IsValid)
        {
            return BadRequest(new { message = "Revisa los elementos de la etiqueta.", errors = validation.Errors, warnings = validation.Warnings });
        }
        var missingAssets = await GetMissingAssetIdsAsync(validation.AssetIds, cancellationToken);
        if (missingAssets.Count > 0)
        {
            return BadRequest(new { message = "La plantilla usa una imagen que ya no está disponible.", assetIds = missingAssets });
        }

        draft.DesignJson = request.DesignJson;
        draft.Revision++;
        template.UpdatedAt = DateTime.UtcNow;
        template.UpdatedBy = CurrentUserName();
        await db.SaveChangesAsync(cancellationToken);

        return Ok(MapTemplate(template));
    }

    [HttpPost("{id:guid}/publish")]
    public async Task<ActionResult<LabelTemplateEditorDto>> Publish(Guid id, CancellationToken cancellationToken)
    {
        var template = await LoadTemplateAsync(id, cancellationToken);
        if (template is null)
        {
            return NotFound(new { message = "No encontramos esta plantilla." });
        }
        var draft = GetDraft(template);
        if (draft is null)
        {
            return Conflict(new { message = "No hay cambios pendientes para publicar." });
        }

        var validation = designValidator.Validate(draft.DesignJson, template.Kind, template.MediaSize);
        if (!validation.IsValid)
        {
            return BadRequest(new { message = "La etiqueta no se puede publicar todavía.", errors = validation.Errors, warnings = validation.Warnings });
        }
        var missingAssets = await GetMissingAssetIdsAsync(validation.AssetIds, cancellationToken);
        if (missingAssets.Count > 0)
        {
            return BadRequest(new { message = "La etiqueta usa una imagen que ya no está disponible.", assetIds = missingAssets });
        }

        var now = DateTime.UtcNow;
        var currentPublished = template.Versions.SingleOrDefault(version => version.Id == template.PublishedVersionId);
        if (currentPublished is not null)
        {
            currentPublished.Status = LabelTemplateVersionStatus.Archived;
        }
        draft.Status = LabelTemplateVersionStatus.Published;
        draft.PublishedAt = now;
        draft.PublishedBy = CurrentUserName();
        template.PublishedVersionId = draft.Id;
        template.UpdatedAt = now;
        template.UpdatedBy = CurrentUserName();

        var nextDraft = new LabelTemplateVersion
        {
            LabelTemplateId = template.Id,
            VersionNumber = template.Versions.Max(version => version.VersionNumber) + 1,
            Status = LabelTemplateVersionStatus.Draft,
            DesignJson = draft.DesignJson,
            Revision = 1,
            CreatedBy = CurrentUserName(),
            CreatedAt = now
        };
        db.LabelTemplateVersions.Add(nextDraft);
        await db.SaveChangesAsync(cancellationToken);

        var updated = await LoadTemplateAsync(template.Id, cancellationToken);
        return Ok(MapTemplate(updated!));
    }

    [HttpPost("{id:guid}/draft/reset")]
    public async Task<ActionResult<LabelTemplateEditorDto>> ResetDraft(Guid id, CancellationToken cancellationToken)
    {
        var template = await LoadTemplateAsync(id, cancellationToken);
        if (template is null)
        {
            return NotFound(new { message = "No encontramos esta plantilla." });
        }
        var draft = GetDraft(template);
        var published = template.Versions.SingleOrDefault(version => version.Id == template.PublishedVersionId);
        if (draft is null || published is null)
        {
            return Conflict(new { message = "No podemos recuperar esta plantilla porque le falta una versión publicada o un borrador." });
        }

        draft.DesignJson = published.DesignJson;
        draft.Revision++;
        template.UpdatedAt = DateTime.UtcNow;
        template.UpdatedBy = CurrentUserName();
        await db.SaveChangesAsync(cancellationToken);
        return Ok(MapTemplate(template));
    }

    [HttpGet("assets")]
    public async Task<ActionResult<List<LabelAssetDto>>> GetAssets(CancellationToken cancellationToken)
    {
        var assets = await db.LabelAssets
            .AsNoTracking()
            .Where(asset => !asset.IsArchived)
            .OrderByDescending(asset => asset.UploadedAt)
            .Select(asset => new LabelAssetDto(asset.Id, asset.Name, asset.OriginalFileName, asset.ContentType, asset.Url, asset.SizeBytes, asset.UploadedAt))
            .ToListAsync(cancellationToken);
        return Ok(assets);
    }

    [HttpPost("assets")]
    [RequestSizeLimit(MaxAssetBytes)]
    public async Task<ActionResult<LabelAssetDto>> UploadAsset(
        IFormFile? file,
        [FromForm] string? name,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { message = "Elige una imagen para usar en la etiqueta." });
        }
        if (file.Length > MaxAssetBytes)
        {
            return BadRequest(new { message = "La imagen puede pesar hasta 3 MB." });
        }
        if (!AllowedImageContentTypes.Contains(file.ContentType))
        {
            return BadRequest(new { message = "Usa una imagen PNG, JPG o WEBP." });
        }
        var displayName = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(file.FileName) : name.Trim();
        if (displayName.Length is < 1 or > 120)
        {
            return BadRequest(new { message = "El nombre de la imagen debe tener entre 1 y 120 caracteres." });
        }
        if (file.FileName.Length > 260)
        {
            return BadRequest(new { message = "El nombre del archivo es demasiado largo." });
        }

        await using var stream = file.OpenReadStream();
        var url = await cloudinary.UploadAsync(stream, file.FileName, "labels");
        var asset = new LabelAsset
        {
            Name = displayName,
            OriginalFileName = file.FileName,
            ContentType = file.ContentType,
            Url = url,
            SizeBytes = file.Length,
            UploadedBy = CurrentUserName(),
            UploadedAt = DateTime.UtcNow
        };
        db.LabelAssets.Add(asset);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(GetAssets), new LabelAssetDto(asset.Id, asset.Name, asset.OriginalFileName, asset.ContentType, asset.Url, asset.SizeBytes, asset.UploadedAt));
    }

    [HttpDelete("assets/{id:guid}")]
    public async Task<IActionResult> ArchiveAsset(Guid id, CancellationToken cancellationToken)
    {
        var asset = await db.LabelAssets.SingleOrDefaultAsync(current => current.Id == id, cancellationToken);
        if (asset is null)
        {
            return NotFound(new { message = "No encontramos esta imagen." });
        }
        var usedByTemplate = await db.LabelTemplateVersions
            .AsNoTracking()
            .Where(version => version.Status != LabelTemplateVersionStatus.Archived)
            .AnyAsync(version => version.DesignJson.Contains(id.ToString()), cancellationToken);
        if (usedByTemplate)
        {
            return Conflict(new { message = "Esta imagen está en una plantilla activa. Retírala de la etiqueta antes de archivarla." });
        }

        asset.IsArchived = true;
        asset.ArchivedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<LabelTemplate?> LoadTemplateAsync(Guid id, CancellationToken cancellationToken) => await db.LabelTemplates
        .Include(template => template.Versions)
        .SingleOrDefaultAsync(template => template.Id == id && !template.IsArchived, cancellationToken);

    private async Task<List<Guid>> GetMissingAssetIdsAsync(IEnumerable<Guid> assetIds, CancellationToken cancellationToken)
    {
        var requested = assetIds.Distinct().ToList();
        if (requested.Count == 0)
        {
            return [];
        }
        var existing = await db.LabelAssets
            .AsNoTracking()
            .Where(asset => requested.Contains(asset.Id) && !asset.IsArchived)
            .Select(asset => asset.Id)
            .ToListAsync(cancellationToken);
        return requested.Except(existing).ToList();
    }

    private static LabelTemplateVersion? GetDraft(LabelTemplate template) => template.Versions
        .Where(version => version.Status == LabelTemplateVersionStatus.Draft)
        .OrderByDescending(version => version.VersionNumber)
        .FirstOrDefault();

    private static LabelTemplateEditorDto MapTemplate(LabelTemplate template)
    {
        var draft = GetDraft(template) ?? throw new InvalidOperationException("La plantilla no tiene borrador.");
        var published = template.Versions.SingleOrDefault(version => version.Id == template.PublishedVersionId);
        return new(
            template.Id,
            template.Name,
            template.Description,
            template.Kind.ToString(),
            template.MediaSize.ToString(),
            template.IsDefault,
            published is null ? null : MapVersion(published),
            MapVersion(draft),
            template.Versions
                .OrderByDescending(version => version.VersionNumber)
                .Select(version => new LabelTemplateVersionHistoryDto(version.Id, version.VersionNumber, version.Status.ToString(), version.CreatedAt, version.PublishedAt))
                .ToList());
    }

    private static LabelTemplateVersionEditorDto MapVersion(LabelTemplateVersion version) => new(
        version.Id,
        version.VersionNumber,
        version.Status.ToString(),
        version.Revision,
        version.DesignJson,
        version.CreatedAt,
        version.PublishedAt);

    private string CurrentUserName() => User.FindFirstValue(ClaimTypes.Name) ?? "Vendedora";

    private static bool TryParseMediaSize(string? value, out LabelMediaSize mediaSize) =>
        Enum.TryParse(value, ignoreCase: true, out mediaSize) && Enum.IsDefined(mediaSize);
}
