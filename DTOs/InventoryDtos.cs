using System.ComponentModel.DataAnnotations;

namespace EntregasApi.DTOs;

public sealed record InventoryBoxSummaryDto(Guid Id, string Code, string Name, string? Location, bool IsNfcBound, int ArticleTypesCount, int TotalUnits, DateTime UpdatedAt);
public sealed record InventoryItemDto(Guid Id, string Name, string? Variant, string? Barcode, string LabelCode, int Quantity, DateTime UpdatedAt);
public sealed record InventoryMovementDto(Guid Id, Guid? InventoryItemId, string? ItemName, string Type, int QuantityDelta, int QuantityAfter, string? Note, string PerformedBy, DateTime OccurredAt);
public sealed record InventoryBoxDto(Guid Id, string Code, string Name, string? Location, bool IsNfcBound, string NfcUrl, List<InventoryItemDto> Items, List<InventoryMovementDto> Movements, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record CreateInventoryBoxDto(
    [Required, MaxLength(30)] string Code,
    [Required, MaxLength(120)] string Name,
    [MaxLength(200)] string? Location);
public sealed record UpdateInventoryBoxDto(
    [Required, MaxLength(30)] string Code,
    [Required, MaxLength(120)] string Name,
    [MaxLength(200)] string? Location);
public sealed record BindInventoryNfcDto([Required, MaxLength(64)] string TagUid);
public sealed record CreateInventoryItemDto(
    [Required, MaxLength(150)] string Name,
    [MaxLength(120)] string? Variant,
    [MaxLength(100)] string? Barcode,
    [Range(1, int.MaxValue)] int Quantity,
    [MaxLength(300)] string? Note);
public sealed record AdjustInventoryItemDto([Range(-100000, 100000)] int QuantityDelta, [MaxLength(300)] string? Note);
public sealed record TransferInventoryItemsDto(Guid SourceBoxId, Guid DestinationBoxId, Guid ItemId, [Range(1, int.MaxValue)] int Quantity, [MaxLength(300)] string? Note);
public sealed record InventoryBarcodeMatchDto(Guid BoxId, string BoxCode, string BoxName, string? Location, Guid ItemId, string ItemName, string? Variant, string? Barcode, string ScannableCode, int Quantity);
public sealed record InventoryCountItemDto(Guid InventoryItemId, [Range(0, int.MaxValue)] int ActualQuantity);
public sealed record CompleteInventoryCountDto([Required, MinLength(1)] List<InventoryCountItemDto> Items, [MaxLength(300)] string? Note);
public sealed record CreateInventoryLabelPrintDto(string Kind, Guid TargetId, string MediaSize, int Copies = 1, string Output = "SystemPrint");
public sealed record UpdateInventoryLabelPrintStatusDto(string Status, string? FailureReason = null);
public sealed record InventoryLabelPrintDto(
    Guid Id,
    string Kind,
    Guid TargetId,
    string Status,
    string MediaSize,
    string Output,
    int Copies,
    DateTime RequestedAt,
    DateTime? HandedOffAt,
    string? FailureReason,
    LabelTemplateVersionSnapshotDto TemplateVersion,
    List<LabelAssetSnapshotDto> Assets,
    Dictionary<string, string> Data);
