using System.Security.Claims;
using System.Security.Cryptography;
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
/// Bodega física por tienda. Las operaciones se registran como movimientos;
/// ninguna tarjeta NFC contiene existencias ni concede acceso por sí misma.
/// </summary>
[ApiController]
[Route("api/inventory")]
[Authorize(Policy = AuthorizationPolicies.InventoryAccess)]
[RequiresFeature(Feature.LabelPrinting)]
public sealed class InventoryController(
    AppDbContext db,
    IConfiguration configuration,
    LabelTemplateCatalogService templateCatalog,
    ILabelTemplateDesignValidator designValidator) : ControllerBase
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _inventoryLinkBaseUrl =
        (configuration["App:InventoryLinkBaseUrl"] ?? "https://app.nenisapp.com").TrimEnd('/');

    [HttpGet("boxes")]
    public async Task<ActionResult<List<InventoryBoxSummaryDto>>> GetBoxes(
        [FromQuery] string? search,
        CancellationToken cancellationToken)
    {
        var normalizedSearch = search?.Trim().ToLowerInvariant();
        var query = db.InventoryBoxes.AsNoTracking().Where(box => !box.IsArchived);
        if (!string.IsNullOrWhiteSpace(normalizedSearch))
        {
            query = query.Where(box =>
                box.Code.ToLower().Contains(normalizedSearch) ||
                box.Name.ToLower().Contains(normalizedSearch) ||
                (box.Location ?? string.Empty).ToLower().Contains(normalizedSearch) ||
                box.Items.Any(item =>
                    item.Name.ToLower().Contains(normalizedSearch) ||
                    (item.Variant ?? string.Empty).ToLower().Contains(normalizedSearch) ||
                    (item.Barcode ?? string.Empty).ToLower().Contains(normalizedSearch) ||
                    item.LabelCode.ToLower().Contains(normalizedSearch)));
        }

        var boxes = await query.OrderBy(box => box.Code)
            .Select(box => new InventoryBoxSummaryDto(
                box.Id,
                box.Code,
                box.Name,
                box.Location,
                box.IsNfcBound,
                box.Items.Count(item => item.Quantity > 0),
                box.Items.Sum(item => (int?)item.Quantity) ?? 0,
                box.UpdatedAt))
            .ToListAsync(cancellationToken);
        return Ok(boxes);
    }

    [HttpGet("boxes/{id:guid}")]
    public async Task<ActionResult<InventoryBoxDto>> GetBox(Guid id, CancellationToken cancellationToken)
    {
        var box = await LoadBoxAsync(id, cancellationToken);
        return box is null ? NotFound(new { message = "Caja no encontrada." }) : Ok(MapBox(box));
    }

    /// <summary>El token se resuelve dentro del negocio activo, nunca de manera pública.</summary>
    [HttpGet("boxes/by-token/{token}")]
    public async Task<ActionResult<InventoryBoxDto>> GetBoxByToken(string token, CancellationToken cancellationToken)
    {
        var box = await db.InventoryBoxes
            .Include(current => current.Items)
            .Include(current => current.Movements).ThenInclude(movement => movement.InventoryItem)
            .AsSplitQuery()
            .FirstOrDefaultAsync(current => current.NfcToken == token && !current.IsArchived, cancellationToken);
        return box is null
            ? NotFound(new { message = "La etiqueta no corresponde a una caja activa de esta tienda." })
            : Ok(MapBox(box));
    }

    [HttpPost("boxes")]
    public async Task<ActionResult<InventoryBoxDto>> CreateBox(
        [FromBody] CreateInventoryBoxDto request,
        CancellationToken cancellationToken)
    {
        var code = request.Code?.Trim() ?? string.Empty;
        var name = request.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
            return BadRequest(new { message = "El código y el nombre de la caja son obligatorios." });

        var exists = await db.InventoryBoxes.AnyAsync(box =>
            !box.IsArchived && box.Code.ToLower() == code.ToLower(), cancellationToken);
        if (exists) return Conflict(new { message = "Ya existe una caja activa con ese código." });

        var now = DateTime.UtcNow;
        var box = new InventoryBox
        {
            Code = code,
            Name = name,
            Location = NormalizeNullable(request.Location),
            NfcToken = GenerateNfcToken(),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.InventoryBoxes.Add(box);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(GetBox), new { id = box.Id }, MapBox(box));
    }

    [HttpPut("boxes/{id:guid}")]
    public async Task<ActionResult<InventoryBoxDto>> UpdateBox(
        Guid id,
        [FromBody] UpdateInventoryBoxDto request,
        CancellationToken cancellationToken)
    {
        var box = await db.InventoryBoxes.FirstOrDefaultAsync(current => current.Id == id && !current.IsArchived, cancellationToken);
        if (box is null) return NotFound(new { message = "Caja no encontrada." });

        var code = request.Code?.Trim() ?? string.Empty;
        var name = request.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
            return BadRequest(new { message = "El código y el nombre de la caja son obligatorios." });
        var duplicate = await db.InventoryBoxes.AnyAsync(current =>
            current.Id != id && !current.IsArchived && current.Code.ToLower() == code.ToLower(), cancellationToken);
        if (duplicate) return Conflict(new { message = "Ya existe una caja activa con ese código." });

        box.Code = code;
        box.Name = name;
        box.Location = NormalizeNullable(request.Location);
        box.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(MapBox((await LoadBoxAsync(id, cancellationToken))!));
    }

    /// <summary>Confirma una escritura NDEF exitosa y evita reutilizar el mismo tag.</summary>
    [HttpPost("boxes/{id:guid}/bind-nfc")]
    public async Task<ActionResult<InventoryBoxDto>> BindNfc(
        Guid id,
        [FromBody] BindInventoryNfcDto request,
        CancellationToken cancellationToken)
    {
        var box = await db.InventoryBoxes.FirstOrDefaultAsync(current => current.Id == id && !current.IsArchived, cancellationToken);
        if (box is null) return NotFound(new { message = "Caja no encontrada." });
        var uid = request.TagUid?.Trim().ToUpperInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(uid)) return BadRequest(new { message = "No se pudo leer el identificador de la etiqueta." });

        var alreadyBound = await db.InventoryBoxes.IgnoreQueryFilters().AnyAsync(current =>
            current.Id != id && current.NfcTagUid == uid && !current.IsArchived, cancellationToken);
        if (alreadyBound) return Conflict(new { message = "Esta etiqueta NFC ya está vinculada a otra caja." });

        box.NfcTagUid = uid;
        box.IsNfcBound = true;
        box.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(MapBox((await LoadBoxAsync(id, cancellationToken))!));
    }

    [HttpPost("boxes/{id:guid}/items")]
    public async Task<ActionResult<InventoryBoxDto>> AddItem(
        Guid id,
        [FromBody] CreateInventoryItemDto request,
        CancellationToken cancellationToken)
    {
        if (request.Quantity <= 0) return BadRequest(new { message = "La cantidad debe ser mayor que cero." });
        var name = request.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { message = "El nombre del artículo es obligatorio." });

        var box = await db.InventoryBoxes.Include(current => current.Items)
            .FirstOrDefaultAsync(current => current.Id == id && !current.IsArchived, cancellationToken);
        if (box is null) return NotFound(new { message = "Caja no encontrada." });

        var variant = NormalizeNullable(request.Variant);
        var barcode = NormalizeBarcode(request.Barcode);
        var item = box.Items.FirstOrDefault(current =>
            current.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.Variant, variant, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.Barcode, barcode, StringComparison.OrdinalIgnoreCase));
        var isNew = item is null;
        if (item is null)
        {
            item = new InventoryItem
            {
                InventoryBoxId = box.Id,
                Name = name,
                Variant = variant,
                Barcode = barcode,
                LabelCode = await GenerateLabelCodeAsync(cancellationToken),
                CreatedAt = DateTime.UtcNow
            };
            db.InventoryItems.Add(item);
        }

        item.Quantity += request.Quantity;
        item.UpdatedAt = DateTime.UtcNow;
        box.UpdatedAt = DateTime.UtcNow;
        db.InventoryMovements.Add(new InventoryMovement
        {
            InventoryBoxId = box.Id,
            InventoryItemId = item.Id,
            Type = isNew ? InventoryMovementType.InitialCount : InventoryMovementType.Added,
            QuantityDelta = request.Quantity,
            QuantityAfter = item.Quantity,
            Note = NormalizeNullable(request.Note),
            PerformedBy = CurrentUserName(),
            OccurredAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        return Ok(MapBox((await LoadBoxAsync(id, cancellationToken))!));
    }

    [HttpPost("items/{id:guid}/adjust")]
    public async Task<ActionResult<InventoryBoxDto>> AdjustItem(
        Guid id,
        [FromBody] AdjustInventoryItemDto request,
        CancellationToken cancellationToken)
    {
        if (request.QuantityDelta == 0) return BadRequest(new { message = "El ajuste debe modificar la cantidad." });
        var item = await db.InventoryItems.Include(current => current.InventoryBox)
            .FirstOrDefaultAsync(current => current.Id == id && !current.InventoryBox.IsArchived, cancellationToken);
        if (item is null) return NotFound(new { message = "Artículo no encontrado." });
        if (item.Quantity + request.QuantityDelta < 0)
            return BadRequest(new { message = "No puedes sacar más artículos de los que hay en la caja." });

        item.Quantity += request.QuantityDelta;
        item.UpdatedAt = DateTime.UtcNow;
        item.InventoryBox.UpdatedAt = DateTime.UtcNow;
        db.InventoryMovements.Add(new InventoryMovement
        {
            InventoryBoxId = item.InventoryBoxId,
            InventoryItemId = item.Id,
            Type = request.QuantityDelta > 0 ? InventoryMovementType.Added : InventoryMovementType.Removed,
            QuantityDelta = request.QuantityDelta,
            QuantityAfter = item.Quantity,
            Note = NormalizeNullable(request.Note),
            PerformedBy = CurrentUserName(),
            OccurredAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        return Ok(MapBox((await LoadBoxAsync(item.InventoryBoxId, cancellationToken))!));
    }

    [HttpPost("boxes/{id:guid}/counts")]
    public async Task<ActionResult<InventoryBoxDto>> CompleteCount(
        Guid id,
        [FromBody] CompleteInventoryCountDto request,
        CancellationToken cancellationToken)
    {
        if (request.Items is null || request.Items.Count == 0)
            return BadRequest(new { message = "Captura todos los artículos antes de guardar el conteo." });
        if (request.Items.Any(item => item.ActualQuantity < 0) || request.Items.Select(item => item.InventoryItemId).Distinct().Count() != request.Items.Count)
            return BadRequest(new { message = "El conteo tiene cantidades o artículos inválidos." });

        var box = await db.InventoryBoxes.Include(current => current.Items)
            .FirstOrDefaultAsync(current => current.Id == id && !current.IsArchived, cancellationToken);
        if (box is null) return NotFound(new { message = "Caja no encontrada." });
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var actualByItem = request.Items.ToDictionary(item => item.InventoryItemId, item => item.ActualQuantity);
        if (box.Items.Count == 0 || actualByItem.Count != box.Items.Count || box.Items.Any(item => !actualByItem.ContainsKey(item.Id)))
            return BadRequest(new { message = "El conteo debe incluir exactamente todos los artículos de la caja." });

        var now = DateTime.UtcNow;
        var session = new InventoryCountSession { InventoryBoxId = box.Id, Note = NormalizeNullable(request.Note), PerformedBy = CurrentUserName(), CountedAt = now };
        db.InventoryCountSessions.Add(session);
        foreach (var item in box.Items)
        {
            var actual = actualByItem[item.Id];
            var difference = actual - item.Quantity;
            session.Entries.Add(new InventoryCountEntry { InventoryItemId = item.Id, ItemName = item.Name, Variant = item.Variant, ExpectedQuantity = item.Quantity, ActualQuantity = actual, Difference = difference });
            if (difference == 0) continue;
            item.Quantity = actual;
            item.UpdatedAt = now;
            db.InventoryMovements.Add(new InventoryMovement { InventoryBoxId = box.Id, InventoryItemId = item.Id, Type = InventoryMovementType.CountAdjustment, QuantityDelta = difference, QuantityAfter = actual, Note = session.Note ?? "Conteo físico", PerformedBy = session.PerformedBy, OccurredAt = now });
        }
        box.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return Ok(MapBox((await LoadBoxAsync(id, cancellationToken))!));
    }

    [HttpPost("transfers")]
    public async Task<ActionResult<InventoryBoxDto>> Transfer(
        [FromBody] TransferInventoryItemsDto request,
        CancellationToken cancellationToken)
    {
        if (request.SourceBoxId == request.DestinationBoxId || request.Quantity <= 0)
            return BadRequest(new { message = "Elige cajas distintas y una cantidad mayor que cero." });
        var source = await db.InventoryItems.Include(item => item.InventoryBox)
            .FirstOrDefaultAsync(item => item.Id == request.ItemId && item.InventoryBoxId == request.SourceBoxId && !item.InventoryBox.IsArchived, cancellationToken);
        if (source is null) return NotFound(new { message = "Artículo de origen no encontrado." });
        if (source.Quantity < request.Quantity) return BadRequest(new { message = "La caja de origen no tiene suficientes artículos." });
        var destination = await db.InventoryBoxes.Include(box => box.Items)
            .FirstOrDefaultAsync(box => box.Id == request.DestinationBoxId && !box.IsArchived, cancellationToken);
        if (destination is null) return NotFound(new { message = "Caja destino no encontrada." });
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var target = destination.Items.FirstOrDefault(item =>
            item.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Variant, source.Variant, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Barcode, source.Barcode, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            target = new InventoryItem { InventoryBoxId = destination.Id, Name = source.Name, Variant = source.Variant, Barcode = source.Barcode, LabelCode = await GenerateLabelCodeAsync(cancellationToken), CreatedAt = DateTime.UtcNow };
            db.InventoryItems.Add(target);
        }
        var now = DateTime.UtcNow;
        source.Quantity -= request.Quantity;
        target.Quantity += request.Quantity;
        source.UpdatedAt = now;
        target.UpdatedAt = now;
        source.InventoryBox.UpdatedAt = now;
        destination.UpdatedAt = now;
        var group = Guid.NewGuid();
        var note = NormalizeNullable(request.Note);
        db.InventoryMovements.AddRange(
            new InventoryMovement { InventoryBoxId = source.InventoryBoxId, InventoryItemId = source.Id, TransferGroupId = group, Type = InventoryMovementType.TransferOut, QuantityDelta = -request.Quantity, QuantityAfter = source.Quantity, Note = note, PerformedBy = CurrentUserName(), OccurredAt = now },
            new InventoryMovement { InventoryBoxId = destination.Id, InventoryItemId = target.Id, TransferGroupId = group, Type = InventoryMovementType.TransferIn, QuantityDelta = request.Quantity, QuantityAfter = target.Quantity, Note = note, PerformedBy = CurrentUserName(), OccurredAt = now });
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return Ok(MapBox((await LoadBoxAsync(destination.Id, cancellationToken))!));
    }

    [HttpGet("items/by-barcode/{barcode}")]
    public async Task<ActionResult<List<InventoryBarcodeMatchDto>>> GetItemsByBarcode(string barcode, CancellationToken cancellationToken)
    {
        var value = NormalizeBarcode(barcode);
        if (value is null) return BadRequest(new { message = "El código escaneado no es válido." });
        var matches = await db.InventoryItems.AsNoTracking()
            .Where(item => (item.Barcode == value || item.LabelCode == value) && item.Quantity > 0 && !item.InventoryBox.IsArchived)
            .OrderBy(item => item.InventoryBox.Code)
            .Select(item => new InventoryBarcodeMatchDto(item.InventoryBoxId, item.InventoryBox.Code, item.InventoryBox.Name, item.InventoryBox.Location, item.Id, item.Name, item.Variant, item.Barcode, item.Barcode ?? item.LabelCode, item.Quantity))
            .ToListAsync(cancellationToken);
        return Ok(matches);
    }

    /// <summary>
    /// Congela una etiqueta de caja o artículo antes de abrir el selector de
    /// impresión nativo. No toca ni configura una impresora específica.
    /// </summary>
    [HttpPost("label-prints")]
    public async Task<ActionResult<InventoryLabelPrintDto>> CreateLabelPrint(
        [FromBody] CreateInventoryLabelPrintDto request,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<LabelTemplateKind>(request.Kind, true, out var kind) ||
            kind is not (LabelTemplateKind.InventoryBox or LabelTemplateKind.InventoryItem))
        {
            return BadRequest(new { message = "El tipo de etiqueta de bodega no es válido." });
        }
        if (!Enum.TryParse<LabelMediaSize>(request.MediaSize, true, out var mediaSize) || !Enum.IsDefined(mediaSize))
        {
            return BadRequest(new { message = "El formato de etiqueta no es válido." });
        }
        if (!Enum.TryParse<LabelPrintOutput>(request.Output, true, out var output) || !Enum.IsDefined(output))
        {
            return BadRequest(new { message = "La salida de impresión no es válida." });
        }
        if (request.Copies is < 1 or > 100)
        {
            return BadRequest(new { message = "Puedes imprimir entre 1 y 100 copias por etiqueta." });
        }

        var data = await BuildLabelDataAsync(kind, request.TargetId, cancellationToken);
        if (data is null)
        {
            return NotFound(new { message = "No encontramos el elemento de bodega que quieres etiquetar." });
        }
        var requestedBy = CurrentUserName();
        var template = await templateCatalog.GetOrCreateDefaultTemplateAsync(kind, mediaSize, requestedBy, cancellationToken);
        if (template.PublishedVersionId is not Guid versionId)
        {
            return Conflict(new { message = "La plantilla predeterminada no tiene una versión publicada." });
        }
        var version = await db.LabelTemplateVersions.AsNoTracking()
            .SingleOrDefaultAsync(current => current.Id == versionId, cancellationToken);
        if (version is null)
        {
            return Conflict(new { message = "No pudimos cargar la versión publicada de la etiqueta." });
        }
        var validation = designValidator.Validate(version.DesignJson, kind, mediaSize);
        if (!validation.IsValid)
        {
            return Conflict(new { message = "La plantilla publicada necesita corregirse antes de imprimir.", errors = validation.Errors });
        }
        var assets = await ResolveAssetsAsync(validation.AssetIds, cancellationToken);
        if (assets.Count != validation.AssetIds.Count)
        {
            return Conflict(new { message = "La plantilla usa una imagen que ya no está disponible en esta tienda." });
        }

        var print = new InventoryLabelPrint
        {
            Kind = kind,
            TargetId = request.TargetId,
            LabelTemplateVersionId = version.Id,
            MediaSize = mediaSize,
            Output = output,
            Copies = request.Copies,
            PayloadJson = JsonSerializer.Serialize(data, PayloadJsonOptions),
            RequestedBy = requestedBy,
            RequestedAt = DateTime.UtcNow
        };
        db.InventoryLabelPrints.Add(print);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(GetLabelPrint), new { id = print.Id }, MapLabelPrint(print, version, assets));
    }

    [HttpGet("label-prints/{id:guid}")]
    public async Task<ActionResult<InventoryLabelPrintDto>> GetLabelPrint(Guid id, CancellationToken cancellationToken)
    {
        var print = await db.InventoryLabelPrints.AsNoTracking()
            .Include(current => current.LabelTemplateVersion)
            .SingleOrDefaultAsync(current => current.Id == id, cancellationToken);
        if (print is null) return NotFound(new { message = "No encontramos este trabajo de etiqueta." });
        var validation = designValidator.Validate(print.LabelTemplateVersion.DesignJson, print.Kind, print.MediaSize);
        if (!validation.IsValid)
        {
            return Conflict(new { message = "El diseño histórico de esta etiqueta ya no es compatible.", errors = validation.Errors });
        }
        return Ok(MapLabelPrint(print, print.LabelTemplateVersion, await ResolveAssetsAsync(validation.AssetIds, cancellationToken)));
    }

    [HttpPut("label-prints/{id:guid}/status")]
    public async Task<ActionResult<InventoryLabelPrintDto>> UpdateLabelPrintStatus(
        Guid id,
        [FromBody] UpdateInventoryLabelPrintStatusDto request,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<LabelPrintJobStatus>(request.Status, true, out var status) ||
            status == LabelPrintJobStatus.Prepared || !Enum.IsDefined(status))
        {
            return BadRequest(new { message = "El estado de impresión no es válido." });
        }
        var print = await db.InventoryLabelPrints.Include(current => current.LabelTemplateVersion)
            .SingleOrDefaultAsync(current => current.Id == id, cancellationToken);
        if (print is null) return NotFound(new { message = "No encontramos este trabajo de etiqueta." });
        if (print.Status != LabelPrintJobStatus.Prepared)
        {
            return Conflict(new { message = "Este trabajo ya recibió un resultado y no puede cambiarse." });
        }
        if (status == LabelPrintJobStatus.Failed && string.IsNullOrWhiteSpace(request.FailureReason))
        {
            return BadRequest(new { message = "Indica qué ocurrió para poder recuperarlo." });
        }
        print.Status = status;
        print.FailureReason = status == LabelPrintJobStatus.Failed
            ? request.FailureReason!.Trim()[..Math.Min(request.FailureReason.Trim().Length, 800)]
            : null;
        print.UpdatedAt = DateTime.UtcNow;
        if (status == LabelPrintJobStatus.SentToSystem) print.HandedOffAt = print.UpdatedAt;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(MapLabelPrint(print, print.LabelTemplateVersion, []));
    }

    private async Task<InventoryBox?> LoadBoxAsync(Guid id, CancellationToken cancellationToken) => await db.InventoryBoxes
        .Include(box => box.Items)
        .Include(box => box.Movements).ThenInclude(movement => movement.InventoryItem)
        .AsSplitQuery()
        .FirstOrDefaultAsync(box => box.Id == id && !box.IsArchived, cancellationToken);

    private async Task<Dictionary<string, string>?> BuildLabelDataAsync(
        LabelTemplateKind kind,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        var businessName = await db.Businesses.AsNoTracking()
            .Where(current => current.Id == db.ActiveBusinessId)
            .Select(current => current.Name)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(businessName)) return null;
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["business.name"] = businessName
        };
        if (kind == LabelTemplateKind.InventoryBox)
        {
            var box = await db.InventoryBoxes.AsNoTracking()
                .SingleOrDefaultAsync(current => current.Id == targetId && !current.IsArchived, cancellationToken);
            if (box is null) return null;
            data["box.code"] = box.Code;
            data["box.name"] = box.Name;
            data["box.location"] = box.Location ?? string.Empty;
            data["box.nfcUrl"] = $"{_inventoryLinkBaseUrl}/caja/{box.BusinessId}/{box.NfcToken}";
            return data;
        }
        var item = await db.InventoryItems.AsNoTracking().Include(current => current.InventoryBox)
            .SingleOrDefaultAsync(current => current.Id == targetId && !current.InventoryBox.IsArchived, cancellationToken);
        if (item is null) return null;
        var scannableCode = item.Barcode ?? item.LabelCode;
        data["item.name"] = item.Name;
        data["item.variant"] = item.Variant ?? string.Empty;
        data["item.scannableCode"] = scannableCode;
        data["item.barcode"] = scannableCode;
        return data;
    }

    private async Task<List<LabelAssetSnapshotDto>> ResolveAssetsAsync(
        IReadOnlySet<Guid> assetIds,
        CancellationToken cancellationToken) => await db.LabelAssets.AsNoTracking()
        .Where(asset => assetIds.Contains(asset.Id) && !asset.IsArchived)
        .OrderBy(asset => asset.Name)
        .Select(asset => new LabelAssetSnapshotDto(asset.Id, asset.Url))
        .ToListAsync(cancellationToken);

    private static InventoryLabelPrintDto MapLabelPrint(
        InventoryLabelPrint print,
        LabelTemplateVersion version,
        List<LabelAssetSnapshotDto> assets)
    {
        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(print.PayloadJson, PayloadJsonOptions) ?? [];
        return new InventoryLabelPrintDto(
            print.Id,
            print.Kind.ToString(),
            print.TargetId,
            print.Status.ToString(),
            print.MediaSize.ToString(),
            print.Output.ToString(),
            print.Copies,
            print.RequestedAt,
            print.HandedOffAt,
            print.FailureReason,
            new LabelTemplateVersionSnapshotDto(version.Id, version.VersionNumber, version.DesignJson, version.PublishedAt ?? version.CreatedAt),
            assets,
            data);
    }

    private InventoryBoxDto MapBox(InventoryBox box) => new(
        box.Id, box.Code, box.Name, box.Location, box.IsNfcBound,
        $"{_inventoryLinkBaseUrl}/caja/{box.BusinessId}/{box.NfcToken}",
        box.Items.OrderBy(item => item.Name).ThenBy(item => item.Variant)
            .Select(item => new InventoryItemDto(item.Id, item.Name, item.Variant, item.Barcode, item.LabelCode, item.Quantity, item.UpdatedAt)).ToList(),
        box.Movements.OrderByDescending(movement => movement.OccurredAt).Take(30)
            .Select(movement => new InventoryMovementDto(movement.Id, movement.InventoryItemId, movement.InventoryItem?.Name, movement.Type.ToString(), movement.QuantityDelta, movement.QuantityAfter, movement.Note, movement.PerformedBy, movement.OccurredAt)).ToList(),
        box.CreatedAt, box.UpdatedAt);

    private string CurrentUserName() => User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "Administración";
    private static string? NormalizeNullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? NormalizeBarcode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return normalized.Length is > 0 and <= 100 ? normalized : null;
    }
    private static string GenerateNfcToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private async Task<string> GenerateLabelCodeAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidate = $"NNI{Convert.ToHexString(RandomNumberGenerator.GetBytes(7))}";
            if (!await db.InventoryItems.AnyAsync(item => item.LabelCode == candidate, cancellationToken)) return candidate;
        }
        throw new InvalidOperationException("No fue posible generar un código de etiqueta único.");
    }
}
