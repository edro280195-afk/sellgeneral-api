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
/// Prepara trabajos de etiquetas de bolsas. El servidor conserva el diseño y los
/// datos usados; la app entrega el documento al selector de impresión del sistema.
/// </summary>
[ApiController]
[Route("api/label-print-jobs")]
[Authorize(Policy = AuthorizationPolicies.Admin)]
[RequiresFeature(Feature.LabelPrinting)]
public sealed class LabelPrintJobsController(
    AppDbContext db,
    LabelTemplateCatalogService templateCatalog,
    ILabelTemplateDesignValidator designValidator,
    ICurrentTenant tenant) : ControllerBase
{
    private const int MaxPackagesPerJob = 100;
    private const int DefaultAvailablePackagesTake = 100;
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

    [HttpPost]
    public async Task<ActionResult<LabelPrintJobDto>> Create(
        [FromBody] CreateLabelPrintJobRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryParseMediaSize(request.MediaSize, out var mediaSize))
        {
            return BadRequest(new { message = "El formato de etiqueta no es válido." });
        }
        if (!TryParseOutput(request.Output, out var output))
        {
            return BadRequest(new { message = "La salida de impresión no es válida." });
        }
        if (request.Copies is < 1 or > 100)
        {
            return BadRequest(new { message = "Puedes imprimir entre 1 y 100 copias por etiqueta." });
        }

        var packageIds = request.PackageIds.Distinct().ToList();
        if (packageIds.Count == 0)
        {
            return BadRequest(new { message = "Selecciona al menos una bolsa para imprimir." });
        }
        if (packageIds.Count > MaxPackagesPerJob)
        {
            return BadRequest(new { message = $"Un trabajo admite hasta {MaxPackagesPerJob} bolsas." });
        }

        var packages = await db.OrderPackages
            .AsNoTracking()
            .Include(package => package.Order)
                .ThenInclude(order => order.Client)
            .Include(package => package.Order)
                .ThenInclude(order => order.Items)
            .Where(package => packageIds.Contains(package.Id))
            .OrderBy(package => package.OrderId)
            .ThenBy(package => package.PackageNumber)
            .ToListAsync(cancellationToken);
        if (packages.Count != packageIds.Count)
        {
            return NotFound(new { message = "Una o más bolsas ya no están disponibles en esta tienda." });
        }

        var business = await db.Businesses
            .AsNoTracking()
            .SingleOrDefaultAsync(current => current.Id == tenant.ActiveBusinessId, cancellationToken);
        if (business is null)
        {
            return NotFound(new { message = "No encontramos la tienda activa." });
        }

        var requestedBy = CurrentUserName();
        var template = await templateCatalog.GetOrCreateDefaultOrderPackageTemplateAsync(mediaSize, requestedBy, cancellationToken);
        var version = template.PublishedVersion;
        if (version is null)
        {
            return Conflict(new { message = "La plantilla predeterminada no tiene una versión publicada." });
        }
        var validation = designValidator.Validate(version.DesignJson, template.Kind, template.MediaSize);
        if (!validation.IsValid)
        {
            return Conflict(new { message = "La plantilla publicada necesita corregirse antes de imprimir.", errors = validation.Errors });
        }

        var assets = await db.LabelAssets
            .AsNoTracking()
            .Where(asset => validation.AssetIds.Contains(asset.Id) && !asset.IsArchived)
            .OrderBy(asset => asset.Name)
            .Select(asset => new LabelAssetSnapshotDto(asset.Id, asset.Url))
            .ToListAsync(cancellationToken);
        if (assets.Count != validation.AssetIds.Count)
        {
            return Conflict(new { message = "La plantilla usa una imagen que ya no está disponible en esta tienda." });
        }

        var job = new LabelPrintJob
        {
            LabelTemplateVersionId = version.Id,
            MediaSize = mediaSize,
            Output = output,
            Copies = request.Copies,
            RequestedBy = requestedBy,
            RequestedAt = DateTime.UtcNow
        };
        var totalByOrder = packages
            .GroupBy(package => package.OrderId)
            .ToDictionary(group => group.Key, group => group.Count());
        var sequence = 0;
        foreach (var package in packages)
        {
            var payload = BuildPayload(business.Name, package, totalByOrder[package.OrderId]);
            job.Items.Add(new LabelPrintJobItem
            {
                OrderPackageId = package.Id,
                Sequence = ++sequence,
                PackageQrCodeValue = package.QrCodeValue,
                PayloadJson = JsonSerializer.Serialize(payload, PayloadJsonOptions)
            });
        }

        db.LabelPrintJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = job.Id }, MapJob(job, version, assets));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<LabelPrintJobDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var job = await db.LabelPrintJobs
            .AsNoTracking()
            .Include(current => current.LabelTemplateVersion)
            .Include(current => current.Items)
            .SingleOrDefaultAsync(current => current.Id == id, cancellationToken);
        if (job is null)
        {
            return NotFound(new { message = "No encontramos este trabajo de impresión." });
        }
        var validation = designValidator.Validate(
            job.LabelTemplateVersion.DesignJson,
            LabelTemplateKind.OrderPackage,
            job.MediaSize);
        if (!validation.IsValid)
        {
            return Conflict(new { message = "El diseño histórico del trabajo ya no es compatible.", errors = validation.Errors });
        }
        var assets = await db.LabelAssets
            .AsNoTracking()
            .Where(asset => validation.AssetIds.Contains(asset.Id))
            .OrderBy(asset => asset.Name)
            .Select(asset => new LabelAssetSnapshotDto(asset.Id, asset.Url))
            .ToListAsync(cancellationToken);
        return Ok(MapJob(job, job.LabelTemplateVersion, assets));
    }

    [HttpGet]
    public async Task<ActionResult<List<LabelPrintJobDto>>> GetRecent(
        [FromQuery] int take = 30,
        CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 100);
        var jobs = await db.LabelPrintJobs
            .AsNoTracking()
            .Include(job => job.LabelTemplateVersion)
            .Include(job => job.Items)
            .OrderByDescending(job => job.RequestedAt)
            .Take(take)
            .ToListAsync(cancellationToken);
        var assetIds = jobs
            .SelectMany(job => designValidator.Validate(job.LabelTemplateVersion.DesignJson, LabelTemplateKind.OrderPackage, job.MediaSize).AssetIds)
            .Distinct()
            .ToList();
        var assetMap = await db.LabelAssets
            .AsNoTracking()
            .Where(asset => assetIds.Contains(asset.Id))
            .ToDictionaryAsync(asset => asset.Id, asset => new LabelAssetSnapshotDto(asset.Id, asset.Url), cancellationToken);
        return Ok(jobs.Select(job =>
        {
            var assetIdsForJob = designValidator.Validate(job.LabelTemplateVersion.DesignJson, LabelTemplateKind.OrderPackage, job.MediaSize).AssetIds;
            return MapJob(job, job.LabelTemplateVersion, assetIdsForJob.Where(assetMap.ContainsKey).Select(assetId => assetMap[assetId]).ToList());
        }).ToList());
    }

    /// <summary>
    /// Bolsas que la vendedora puede preparar en una sola corrida. No depende
    /// de una impresora ni de una etiqueta previamente impresa: el trabajo se
    /// crea solamente al confirmar en el dispositivo.
    /// </summary>
    [HttpGet("available-packages")]
    public async Task<ActionResult<List<AvailableLabelPackageDto>>> GetAvailablePackages(
        [FromQuery] int take = DefaultAvailablePackagesTake,
        CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, MaxPackagesPerJob);

        var packages = await db.OrderPackages
            .AsNoTracking()
            .Where(package => package.Status == PackageTrackingStatus.Packed || package.Status == PackageTrackingStatus.Loaded)
            .OrderByDescending(package => package.Order.CreatedAt)
            .ThenBy(package => package.OrderId)
            .ThenBy(package => package.PackageNumber)
            .Select(package => new AvailableLabelPackageDto(
                package.Id,
                package.OrderId,
                package.Order.Client.Name,
                package.PackageNumber,
                package.Order.Packages.Count,
                package.Status.ToString(),
                package.CreatedAt))
            .Take(take)
            .ToListAsync(cancellationToken);

        return Ok(packages);
    }

    [HttpPut("{id:guid}/status")]
    public async Task<ActionResult<LabelPrintJobDto>> UpdateStatus(
        Guid id,
        [FromBody] UpdateLabelPrintJobStatusRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryParseStatus(request.Status, out var status) || status == LabelPrintJobStatus.Prepared)
        {
            return BadRequest(new { message = "El estado de impresión no es válido." });
        }
        var job = await db.LabelPrintJobs
            .Include(current => current.LabelTemplateVersion)
            .Include(current => current.Items)
            .SingleOrDefaultAsync(current => current.Id == id, cancellationToken);
        if (job is null)
        {
            return NotFound(new { message = "No encontramos este trabajo de impresión." });
        }
        if (job.Status != LabelPrintJobStatus.Prepared)
        {
            return Conflict(new { message = "Este trabajo ya recibió un resultado y no puede cambiarse." });
        }
        if (status == LabelPrintJobStatus.Failed && string.IsNullOrWhiteSpace(request.FailureReason))
        {
            return BadRequest(new { message = "Indica qué ocurrió para poder mostrar una recuperación útil." });
        }

        job.Status = status;
        job.FailureReason = status == LabelPrintJobStatus.Failed ? request.FailureReason!.Trim()[..Math.Min(request.FailureReason.Trim().Length, 800)] : null;
        job.UpdatedAt = DateTime.UtcNow;
        if (status == LabelPrintJobStatus.SentToSystem)
        {
            job.HandedOffAt = job.UpdatedAt;
        }
        await db.SaveChangesAsync(cancellationToken);
        return Ok(MapJob(job, job.LabelTemplateVersion, []));
    }

    private static LabelPrintPayloadDto BuildPayload(string businessName, OrderPackage package, int totalPackages) => new(
        businessName,
        new LabelOrderPayloadDto(
            package.OrderId,
            package.Order.Client.Name,
            package.Order.Client.Phone,
            package.Order.AlternativeAddress ?? package.Order.Client.Address,
            BuildItemSummary(package.Order.Items),
            package.Order.DeliveryInstructions ?? package.Order.Client.DeliveryInstructions),
        new LabelPackagePayloadDto(package.Id, package.PackageNumber, totalPackages, package.QrCodeValue));

    private static string BuildItemSummary(IEnumerable<OrderItem> items) => string.Join("\n", items
        .OrderBy(item => item.Id)
        .Select(item => $"{item.Quantity} × {item.ProductName}")
        .Take(30));

    private static LabelPrintJobDto MapJob(
        LabelPrintJob job,
        LabelTemplateVersion version,
        List<LabelAssetSnapshotDto> assets) => new(
            job.Id,
            job.Status.ToString(),
            job.MediaSize.ToString(),
            job.Output.ToString(),
            job.Copies,
            job.RequestedAt,
            job.HandedOffAt,
            job.FailureReason,
            new LabelTemplateVersionSnapshotDto(version.Id, version.VersionNumber, version.DesignJson, version.PublishedAt ?? version.CreatedAt),
            assets,
            job.Items.OrderBy(item => item.Sequence).Select(item => new LabelPrintJobItemDto(
                item.Id,
                item.OrderPackageId,
                item.Sequence,
                item.PackageQrCodeValue,
                JsonSerializer.Deserialize<LabelPrintPayloadDto>(item.PayloadJson, PayloadJsonOptions)
                    ?? throw new InvalidOperationException("El trabajo contiene una etiqueta sin contenido.")
            )).ToList());

    private string CurrentUserName() => User.FindFirstValue(ClaimTypes.Name) ?? "Vendedora";

    private static bool TryParseMediaSize(string? value, out LabelMediaSize mediaSize) =>
        Enum.TryParse(value, ignoreCase: true, out mediaSize) && Enum.IsDefined(mediaSize);

    private static bool TryParseOutput(string? value, out LabelPrintOutput output) =>
        Enum.TryParse(value, ignoreCase: true, out output) && Enum.IsDefined(output);

    private static bool TryParseStatus(string? value, out LabelPrintJobStatus status) =>
        Enum.TryParse(value, ignoreCase: true, out status) && Enum.IsDefined(status);
}
