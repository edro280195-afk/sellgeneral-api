using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

/// <summary>Destino de negocio de una plantilla de etiquetas.</summary>
public enum LabelTemplateKind
{
    InventoryBox = 0,
    InventoryItem = 1,
    OrderPackage = 2
}

/// <summary>
/// Tamaño físico del medio. No representa una marca ni una configuración de impresora.
/// La aplicación entrega el documento al selector nativo del dispositivo.
/// </summary>
public enum LabelMediaSize
{
    Square50x50 = 0,
    Shipping4x6 = 1
}

public enum LabelTemplateVersionStatus
{
    Draft = 0,
    Published = 1,
    Archived = 2
}

public enum LabelPrintOutput
{
    SystemPrint = 0,
    PdfExport = 1,
    Share = 2
}

/// <summary>
/// El sistema operativo no confirma que el papel saliera físicamente. Por ello el
/// estado máximo verificable es que el trabajo se entregó al sistema de impresión.
/// </summary>
public enum LabelPrintJobStatus
{
    Prepared = 0,
    SentToSystem = 1,
    Canceled = 2,
    Failed = 3
}

/// <summary>
/// Familia de diseños de un negocio. Conserva una versión publicada inmutable y un
/// borrador editable para que una reimpresión siempre conserve su diseño original.
/// </summary>
public sealed class LabelTemplate : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int BusinessId { get; set; }

    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(400)]
    public string? Description { get; set; }

    public LabelTemplateKind Kind { get; set; }
    public LabelMediaSize MediaSize { get; set; }
    public bool IsDefault { get; set; }
    public bool IsArchived { get; set; }

    public Guid? PublishedVersionId { get; set; }
    public LabelTemplateVersion? PublishedVersion { get; set; }

    [Required, MaxLength(120)]
    public string CreatedBy { get; set; } = "Sistema";

    [MaxLength(120)]
    public string? UpdatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ArchivedAt { get; set; }

    public ICollection<LabelTemplateVersion> Versions { get; set; } = new List<LabelTemplateVersion>();
}

/// <summary>Instantánea editable o publicada del diseño expresado en milímetros.</summary>
public sealed class LabelTemplateVersion : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int BusinessId { get; set; }
    public Guid LabelTemplateId { get; set; }
    public LabelTemplate LabelTemplate { get; set; } = null!;

    public int VersionNumber { get; set; }
    public LabelTemplateVersionStatus Status { get; set; } = LabelTemplateVersionStatus.Draft;

    [Required]
    public string DesignJson { get; set; } = string.Empty;

    public int Revision { get; set; } = 1;

    [Required, MaxLength(120)]
    public string CreatedBy { get; set; } = "Sistema";

    [MaxLength(120)]
    public string? PublishedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAt { get; set; }
}

/// <summary>Imagen administrada por cada negocio para usarla dentro de sus etiquetas.</summary>
public sealed class LabelAsset : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int BusinessId { get; set; }

    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [Required, MaxLength(260)]
    public string OriginalFileName { get; set; } = string.Empty;

    [Required, MaxLength(120)]
    public string ContentType { get; set; } = string.Empty;

    [Required, MaxLength(1200)]
    public string Url { get; set; } = string.Empty;

    public long SizeBytes { get; set; }
    public bool IsArchived { get; set; }

    [Required, MaxLength(120)]
    public string UploadedBy { get; set; } = "Sistema";

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ArchivedAt { get; set; }
}

/// <summary>
/// Trabajo de impresión auditable. Guarda la versión exacta y el contenido de cada
/// etiqueta antes de entregarlo al sistema operativo.
/// </summary>
public sealed class LabelPrintJob : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int BusinessId { get; set; }
    public Guid LabelTemplateVersionId { get; set; }
    public LabelTemplateVersion LabelTemplateVersion { get; set; } = null!;
    public LabelMediaSize MediaSize { get; set; }
    public LabelPrintOutput Output { get; set; }
    public LabelPrintJobStatus Status { get; set; } = LabelPrintJobStatus.Prepared;

    [Range(1, 100)]
    public int Copies { get; set; } = 1;

    [Required, MaxLength(120)]
    public string RequestedBy { get; set; } = "Sistema";

    [MaxLength(800)]
    public string? FailureReason { get; set; }

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime? HandedOffAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public ICollection<LabelPrintJobItem> Items { get; set; } = new List<LabelPrintJobItem>();
}

/// <summary>Contenido inmutable de una bolsa dentro de un trabajo de impresión.</summary>
public sealed class LabelPrintJobItem : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int BusinessId { get; set; }
    public Guid LabelPrintJobId { get; set; }
    public LabelPrintJob LabelPrintJob { get; set; } = null!;
    public Guid OrderPackageId { get; set; }
    public int Sequence { get; set; }

    [Required, MaxLength(100)]
    public string PackageQrCodeValue { get; set; } = string.Empty;

    [Required]
    public string PayloadJson { get; set; } = string.Empty;
}
