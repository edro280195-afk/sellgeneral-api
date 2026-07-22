namespace EntregasApi.DTOs;

public sealed record CreateLabelPrintJobRequest(
    List<Guid> PackageIds,
    string MediaSize,
    int Copies = 1,
    string Output = "SystemPrint");

public sealed record UpdateLabelPrintJobStatusRequest(string Status, string? FailureReason = null);

public sealed record LabelTemplateVersionSnapshotDto(
    Guid Id,
    int VersionNumber,
    string DesignJson,
    DateTime PublishedAt);

public sealed record LabelAssetSnapshotDto(Guid Id, string Url);

public sealed record LabelOrderPayloadDto(
    int Id,
    string ClientName,
    string? Phone,
    string? Address,
    string ItemSummary,
    string? DeliveryInstructions);

public sealed record LabelPackagePayloadDto(
    Guid Id,
    int Number,
    int Total,
    string QrCodeValue);

public sealed record LabelPrintPayloadDto(
    string BusinessName,
    LabelOrderPayloadDto Order,
    LabelPackagePayloadDto Package);

public sealed record LabelPrintJobItemDto(
    Guid Id,
    Guid OrderPackageId,
    int Sequence,
    string PackageQrCodeValue,
    LabelPrintPayloadDto Payload);

public sealed record LabelPrintJobDto(
    Guid Id,
    string Status,
    string MediaSize,
    string Output,
    int Copies,
    DateTime RequestedAt,
    DateTime? HandedOffAt,
    string? FailureReason,
    LabelTemplateVersionSnapshotDto TemplateVersion,
    List<LabelAssetSnapshotDto> Assets,
    List<LabelPrintJobItemDto> Items);

/// <summary>
/// Bolsa disponible para el centro de impresión. La impresión se limita a
/// bolsas que aún están en preparación o ruta; las entregadas no deben volver
/// a presentarse como pendientes, pero se pueden reimprimir desde su pedido.
/// </summary>
public sealed record AvailableLabelPackageDto(
    Guid Id,
    int OrderId,
    string ClientName,
    int PackageNumber,
    int TotalPackages,
    string Status,
    DateTime CreatedAt);

public sealed record LabelTemplateVersionEditorDto(
    Guid Id,
    int VersionNumber,
    string Status,
    int Revision,
    string DesignJson,
    DateTime CreatedAt,
    DateTime? PublishedAt);

public sealed record LabelTemplateVersionHistoryDto(
    Guid Id,
    int VersionNumber,
    string Status,
    DateTime CreatedAt,
    DateTime? PublishedAt);

public sealed record LabelTemplateEditorDto(
    Guid Id,
    string Name,
    string? Description,
    string Kind,
    string MediaSize,
    bool IsDefault,
    LabelTemplateVersionEditorDto? PublishedVersion,
    LabelTemplateVersionEditorDto DraftVersion,
    List<LabelTemplateVersionHistoryDto> History);

public sealed record SaveLabelTemplateDraftRequest(string DesignJson, int ExpectedRevision);

public sealed record LabelAssetDto(
    Guid Id,
    string Name,
    string OriginalFileName,
    string ContentType,
    string Url,
    long SizeBytes,
    DateTime UploadedAt);
