using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

/// <summary>
/// Caja física de mercancía de una tienda. El tag NFC sólo contiene su enlace
/// aleatorio: el inventario nunca vive dentro de la tarjeta.
/// </summary>
public sealed class InventoryBox : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int BusinessId { get; set; }

    [Required, MaxLength(30)]
    public string Code { get; set; } = string.Empty;

    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Location { get; set; }

    [Required, MaxLength(64)]
    public string NfcToken { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? NfcTagUid { get; set; }

    public bool IsNfcBound { get; set; }
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<InventoryItem> Items { get; set; } = new List<InventoryItem>();
    public ICollection<InventoryMovement> Movements { get; set; } = new List<InventoryMovement>();
    public ICollection<InventoryCountSession> CountSessions { get; set; } = new List<InventoryCountSession>();
}

/// <summary>Existencia actual de un artículo dentro de una caja.</summary>
public sealed class InventoryItem : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int BusinessId { get; set; }
    public Guid InventoryBoxId { get; set; }
    public InventoryBox InventoryBox { get; set; } = null!;

    [Required, MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(120)]
    public string? Variant { get; set; }

    [MaxLength(100)]
    public string? Barcode { get; set; }

    /// <summary>Código interno imprimible cuando no existe un código comercial.</summary>
    [Required, MaxLength(40)]
    public string LabelCode { get; set; } = string.Empty;

    public int Quantity { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<InventoryMovement> Movements { get; set; } = new List<InventoryMovement>();
}

public enum InventoryMovementType
{
    InitialCount = 0,
    Added = 1,
    Removed = 2,
    Adjusted = 3,
    TransferOut = 4,
    TransferIn = 5,
    CountAdjustment = 6
}

/// <summary>Bitácora inmutable de cada cambio de existencias.</summary>
public sealed class InventoryMovement : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int BusinessId { get; set; }
    public Guid InventoryBoxId { get; set; }
    public InventoryBox InventoryBox { get; set; } = null!;
    public Guid? InventoryItemId { get; set; }
    public InventoryItem? InventoryItem { get; set; }
    public Guid? TransferGroupId { get; set; }
    public InventoryMovementType Type { get; set; }
    public int QuantityDelta { get; set; }
    public int QuantityAfter { get; set; }

    [MaxLength(300)]
    public string? Note { get; set; }

    [Required, MaxLength(120)]
    public string PerformedBy { get; set; } = "Sistema";

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Foto auditable de un conteo físico.</summary>
public sealed class InventoryCountSession : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int BusinessId { get; set; }
    public Guid InventoryBoxId { get; set; }
    public InventoryBox InventoryBox { get; set; } = null!;

    [MaxLength(300)]
    public string? Note { get; set; }

    [Required, MaxLength(120)]
    public string PerformedBy { get; set; } = "Sistema";

    public DateTime CountedAt { get; set; } = DateTime.UtcNow;
    public ICollection<InventoryCountEntry> Entries { get; set; } = new List<InventoryCountEntry>();
}

public sealed class InventoryCountEntry : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int BusinessId { get; set; }
    public Guid InventoryCountSessionId { get; set; }
    public InventoryCountSession InventoryCountSession { get; set; } = null!;
    public Guid InventoryItemId { get; set; }

    [Required, MaxLength(150)]
    public string ItemName { get; set; } = string.Empty;

    [MaxLength(120)]
    public string? Variant { get; set; }

    public int ExpectedQuantity { get; set; }
    public int ActualQuantity { get; set; }
    public int Difference { get; set; }
}

/// <summary>
/// Bitácora inmutable de una etiqueta de bodega preparada para el selector de
/// impresión del sistema. El diseño publicado y los datos se congelan antes
/// del handoff; no afirma que una impresora física haya terminado el trabajo.
/// </summary>
public sealed class InventoryLabelPrint : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int BusinessId { get; set; }
    public LabelTemplateKind Kind { get; set; }
    public Guid TargetId { get; set; }
    public Guid LabelTemplateVersionId { get; set; }
    public LabelTemplateVersion LabelTemplateVersion { get; set; } = null!;
    public LabelMediaSize MediaSize { get; set; }
    public LabelPrintOutput Output { get; set; }
    public LabelPrintJobStatus Status { get; set; } = LabelPrintJobStatus.Prepared;

    [Range(1, 100)]
    public int Copies { get; set; } = 1;

    [Required]
    public string PayloadJson { get; set; } = string.Empty;

    [Required, MaxLength(120)]
    public string RequestedBy { get; set; } = "Sistema";

    [MaxLength(800)]
    public string? FailureReason { get; set; }

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime? HandedOffAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
