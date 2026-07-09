using System.ComponentModel.DataAnnotations;
using EntregasApi.Services;

namespace EntregasApi.DTOs;

// ── Auth ──
public record LoginRequest(string Email, string Password);
public record LoginResponse(
    string Token,
    string Name,
    string Role,
    DateTime ExpiresAt,
    int AccountId,
    List<AuthMembershipDto> Memberships,
    // Refresh token opaco para re-autenticar en silencio al expirar el JWT.
    string? RefreshToken = null);
public record RegisterRequest(
    string Name,
    string Email,
    string Password,
    bool AcceptedLegal = false,
    string? LegalVersion = null);
public record AuthMembershipDto(int BusinessId, string BusinessName, string Role);
public record PhoneLoginRequest(string Phone);

/// <summary>Canje de un refresh token por una sesión nueva (o logout).</summary>
public record RefreshRequest(string RefreshToken);

public record VerifyPhoneLoginRequest(
    string Phone,
    string Code,
    string? AccountType = null,
    string? BusinessName = null,
    string? City = null,
    // Nombre opcional para el alta passwordless pre-llenada desde el pedido.
    string? FirstName = null,
    string? LastName = null,
    bool AcceptedLegal = false,
    string? LegalVersion = null);

// ── Registro/acceso de la compradora por teléfono + contraseña (confirmación WhatsApp) ──

/// <summary>
/// Alta de una compradora con teléfono + contraseña. Tras registrarse se envía
/// un código por WhatsApp que debe confirmar en <c>phone/confirm</c>.
/// </summary>
public record PhoneRegisterRequest(
    string FirstName,
    string LastName,
    string Phone,
    string Email,
    string Password,
    bool AcceptedLegal = false,
    string? LegalVersion = null,
    string AccountType = "client",
    string? BusinessName = null,
    string? City = null);

/// <summary>Acceso de la compradora ya registrada: teléfono + contraseña.</summary>
public record PhonePasswordLoginRequest(string Phone, string Password);

/// <summary>Solicita un código para restablecer la contraseña.</summary>
public record PasswordResetRequest(string Phone);

/// <summary>Confirma el código y establece una contraseña nueva.</summary>
public record ConfirmPasswordResetRequest(
    string Phone,
    string Code,
    string NewPassword);

/// <summary>
/// Inicia Facebook Login. Si el Facebook ya está vinculado a una cuenta
/// verificada devuelve sesión; de lo contrario responde con los datos que
/// todavía deben completarse.
/// </summary>
public record FacebookLoginRequest(
    string AccessToken,
    string AccountType = "client",
    string TokenType = "classic");

/// <summary>
/// Completa un alta o enlace iniciado con Facebook. Una cuenta existente exige
/// su contraseña actual antes de permitir que se agregue Facebook como método
/// de acceso.
/// </summary>
public record FacebookCompleteProfileRequest(
    string AccessToken,
    string AccountType,
    string FirstName,
    string LastName,
    string Email,
    string Phone,
    string TokenType = "classic",
    string? BusinessName = null,
    string? City = null,
    string? ExistingPassword = null,
    bool AcceptedLegal = false,
    string? LegalVersion = null);

public record FacebookContinuationResponse(
    string Error,
    string Message,
    string AccountType,
    bool NeedsProfile,
    bool NeedsPhoneVerification,
    bool RequiresExistingPassword,
    string? FirstName,
    string? LastName,
    string? Email,
    string? Phone,
    List<string> MissingFields,
    bool ProviderConfigured = false,
    bool DevMode = false);

// Suscripcion / onboarding de negocio
public record CreateBusinessRequest(
    string Name,
    string? Slug = null,
    string? City = null,
    string? FrontendUrl = null,
    double? DepotLat = null,
    double? DepotLng = null,
    string? GeocodingRegion = null);

public record BusinessOnboardingResponse(
    int BusinessId,
    string Name,
    string Slug,
    string Role,
    SubscriptionAccountStateDto Subscription);

public record SubscriptionAccountStateDto(
    string EffectivePlan,
    string PlanTier,
    string SubscriptionStatus,
    DateTime? TrialEndsAt,
    DateTime? CurrentPeriodEndsAt,
    string? PendingPlanTier,
    DateTime? PendingPlanEffectiveAt,
    bool IsLocked,
    int DaysLeft,
    int PastDueGraceDays);

public record ChangePlanRequest(string PlanTier);

// ── General ──
public record PagedResult<T>(
    List<T> Items,
    int TotalCount,
    int CurrentPage,
    int PageSize
);

// ── Excel Upload ──
public record ExcelUploadResult(
    int OrdersCreated,
    int ClientsCreated,
    List<OrderSummaryDto> Orders,
    List<string> Warnings
);

public record OrderSummaryDto(
    int Id,
    string ClientName,
    string Status,
    decimal Total,
    string Link,
    int ItemsCount,
    string OrderType,
    DateTime CreatedAt,
    string Type,

    string? ClientPhone,
    string? ClientAddress,
    DateTime? PostponedAt,
    string? PostponedNote,
    decimal Subtotal,
    decimal ShippingCost,
    string AccessToken,
    DateTime ExpiresAt,
    List<OrderItemDto> Items,
    // Nuevo: Libro de Pagos
    List<OrderPaymentDto> Payments = null!,
    decimal AmountPaid = 0m,
    decimal BalanceDue = 0m,
    // Legacy (retrocompatibilidad)
    decimal AdvancePayment = 0m,
    string? PaymentMethod = null,
    // SalesPeriod (Corte)
    int? SalesPeriodId = null,
    string? SalesPeriodName = null,
    // Cliente y Tags
    int? ClientId = null,
    List<string>? Tags = null,
    // Loyalty
    int ClientPoints = 0,
    string? DeliveryInstructions = null,
    decimal DiscountAmount = 0m,
    string? AlternativeAddress = null,
    int? DeliveryRouteId = null,
    DateTime? ScheduledDeliveryDate = null,
    string? ClientFacebookProfileUrl = null,
    DateTime? NotifiedAt = null,
    double? ClientLatitude = null,
    double? ClientLongitude = null,
    // Enlace corto compartible (dominio compartido) que abre el muro de
    // instalación o, si la app está instalada, directamente el pedido.
    string? ShareUrl = null
);


public record ClientDto(
    int Id,
    string Name,
    string? Phone,
    string? Address,
    string Tag,
    int OrdersCount,
    decimal TotalSpent,
    string Type,
    string? DeliveryInstructions = null,
    double? Latitude = null,
    double? Longitude = null,
    List<string>? Aliases = null,
    string? FacebookProfileUrl = null
);

public record OrderTrackingDto(
    int Id,
    string ClientName,
    string Status,
    decimal Total,
    int ItemsCount,
    string OrderType,
    List<OrderItemDto> Items,
    DateTime CreatedAt,
    string Type
);

public record OrderItemDto(
    int Id,
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal
);

public record ManualOrderRequest(
    string ClientName,
    string? ClientPhone,
    string? ClientAddress,
    string? Type,
    string OrderType,
    List<ManualOrderItem> Items,
    DateTime? PostponedAt = null,
    string? PostponedNote = null,
    string Status = "Pending",
    string? DeliveryInstructions = null,
    string? AlternativeAddress = null,
    DateTime? ScheduledDeliveryDate = null,
    // Si viene un ClientId resuelto desde el frontend, se usa directo y se salta el
    // lookup por nombre. Útil cuando el resolver multi-señal ya identificó a la
    // clienta y el `ClientName` tecleado debe quedar como alias.
    int? ClientId = null
);
public record ManualOrderItem(
    string ProductName,
    int Quantity,
    decimal UnitPrice
);

public record ManualOrderItemRequest(
    string ProductName,
    int Quantity,
    decimal UnitPrice
);

public record ParseLiveRequest(string Text, List<AiParsedOrder>? CurrentState);

// ── Delivery Route ──
public record CreateRouteRequest(
    List<int> OrderIds,
    bool Force = false,
    List<Guid>? TandaParticipantIds = null,
    /// <summary>
    /// Si es true, OrderIds y TandaParticipantIds vienen en el orden óptimo deseado
    /// (típicamente desde un preview del frontend) y se respeta sin re-optimizar.
    /// Si es false (default), el backend re-optimiza usando Google Routes API.
    /// </summary>
    bool PreOptimized = false
);

public record SkippedStopDto(
    string Kind,         // "Order" | "Tanda"
    string Id,           // int como string o Guid como string
    string Name,         // nombre del cliente
    string Reason
);

public record CreateRouteResponse(
    RouteDto Route,
    List<SkippedStopDto> Skipped
);

public record PreviewRouteRequest(
    List<int>? OrderIds,
    List<Guid>? TandaParticipantIds,
    double? StartLat,
    double? StartLng
);

public record PreviewStopDto(
    string Kind,                 // "Order" | "Tanda"
    int? OrderId,
    Guid? TandaParticipantId,
    int SortOrder,
    string ClientName,
    string? ClientAddress,
    double? Latitude,
    double? Longitude,
    decimal Total,
    bool HasCoords,
    string? TandaName,
    int? TandaWeek
);

public record PreviewRouteResponse(
    List<PreviewStopDto> Stops,
    int TotalDistanceMeters,
    int TotalDurationSeconds,
    string OptimizerSource,
    List<SkippedStopDto> Skipped,
    int StopsWithoutCoords,
    string? PolylineEncoded = null,
    double? DepotLatitude = null,
    double? DepotLongitude = null
);

public record RecomposeRouteRequest(
    List<int> OrderIds,
    List<Guid>? TandaParticipantIds = null
);

public record RecomposeRouteResponse(
    RouteDto Route,
    List<SkippedStopDto> Skipped
);

public record BulkGeocodeRequest(List<int> ClientIds);
public record SetClientCoordinatesRequest(
    double Latitude,
    double Longitude,
    string? Address,
    string? DeliveryInstructions = null
);
public record BulkGeocodeResultDto(
    int ClientId,
    bool Success,
    double? Latitude,
    double? Longitude,
    string? FormattedAddress,
    string? Error
);

public record RouteDto(
    int Id,
    string DriverToken,
    string DriverLink,
    string Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    List<RouteDeliveryDto> Deliveries,
    List<DriverExpenseDto>? Expenses = null
);

public record RouteDeliveryDto(
    int DeliveryId,
    int? OrderId,
    int SortOrder,
    string ClientName,
    string? ClientAddress,
    double? Latitude,
    double? Longitude,
    string Status,
    decimal Total,
    DateTime? DeliveredAt,
    string? Notes,
    string? FailureReason,
    List<string> EvidenceUrls,
    string? ClientPhone,
    string? PaymentMethod,
    List<OrderPaymentDto>? Payments = null,
    List<OrderItemDto>? Items = null,
    decimal AmountPaid = 0m,
    decimal BalanceDue = 0m,
    string? DeliveryInstructions = null,
    DateTime? ArrivedAt = null,
    List<OrderPackageDto>? Packages = null,
    string? AlternativeAddress = null,
    // Feature #5 — Etiqueta y tipo de cliente para el chofer
    string? ClientTag = null,
    string? ClientType = null,
    // ── Tanda fields (cuando Kind == "Tanda") ──
    string Kind = "Order",
    Guid? TandaParticipantId = null,
    Guid? TandaId = null,
    string? TandaName = null,
    string? TandaProductName = null,
    int? TandaWeek = null,
    int? TandaTotalWeeks = null,
    string? TandaVariant = null
)
{
    public int Id => DeliveryId;
    public string? Address => ClientAddress;
}

// Tandas listas para incluirse en una ruta dominical
public record AvailableTandaDto(
    Guid TandaParticipantId,
    Guid TandaId,
    string TandaName,
    string? TandaProductName,
    int Week,
    int TotalWeeks,
    string? Variant,
    int ClientId,
    string ClientName,
    string? ClientAddress,
    string? ClientPhone,
    double? ClientLatitude,
    double? ClientLongitude,
    string? DeliveryInstructions
);

// ── AI Voice Routes ──
public record AiRouteSelectionRequest(
    string VoiceCommand,
    List<OrderSummaryDto> AvailableOrders
);

public record AiRouteSelectionResponse(
    List<int> SelectedOrderIds,
    string AiConfirmationMessage,
    string? AudioBase64 = null
);

public record CreateAdminExpenseRequest(decimal Amount, string ExpenseType, DateTime Date, string? Notes, int? DeliveryRouteId);
public record UpdateAdminExpenseRequest(decimal Amount, string ExpenseType, DateTime Date, string? Notes, int? DeliveryRouteId);

// ── Driver ──
public record UpdateLocationRequest(double Latitude, double Longitude);
public record CompleteDeliveryRequest(string? Notes, string? PaymentsJson, string? SignatureSvg = null, string? SignedByName = null);
public record PaymentInputDto(decimal Amount, string Method, string? Notes);
public record FailDeliveryRequest(string Reason, string? Notes);

// ── Client View ──
public record ClientOrderView(
    int ClientId,
    string ClientName,
    List<OrderItemDto> Items,
    decimal Subtotal,
    decimal ShippingCost,
    decimal Total,
    string Status,
    DateTime? EstimatedArrival,
    DriverLocationDto? DriverLocation,
    int? QueuePosition = null,
    int? TotalDeliveries = null,
    bool IsCurrentDelivery = false,
    int? DeliveriesAhead = null,
    double? ClientLatitude = null,
    double? ClientLongitude = null,
    DateTime? CreatedAt = null,
    string? Type = null,
    string? ClientAddress = null,
    decimal AdvancePayment = 0m,
    List<OrderPaymentDto>? Payments = null,
    decimal AmountPaid = 0m,
    decimal BalanceDue = 0m,
    int ClientPoints = 0,
    string? DeliveryInstructions = null,
    DateTime? ExpiresAt = null,
    DateTime? ScheduledDeliveryDate = null,
    /// <summary>Fotos de evidencia de la entrega (solo si fue entregada).</summary>
    List<string>? EvidenceUrls = null,
    /// <summary>Firma digital capturada al momento de la entrega (SVG).</summary>
    string? SignatureSvg = null,
    /// <summary>Nombre de quien firmó el pedido.</summary>
    string? SignedByName = null,
    /// <summary>Fecha/hora en que se firmó.</summary>
    DateTime? SignedAt = null,
    /// <summary>Motivo de no entrega (solo si Status = NotDelivered).</summary>
    string? FailureReason = null,
    /// <summary>Fecha/hora real de la entrega o del intento fallido.</summary>
    DateTime? DeliveredAt = null,
    /// <summary>Fotos del intento de no entrega (cuando Status = NotDelivered).</summary>
    List<string>? NonDeliveryEvidenceUrls = null,
    string? MercadoPagoPublicKey = null,
    // ── Campos V3: Experiencia de seguimiento Nenis ──
    /// <summary>Nombre del negocio (tienda). Protagonista de la experiencia.</summary>
    string? BusinessName = null,
    /// <summary>URL del logo del negocio (Cloudinary). Null si la tienda no lo ha subido.</summary>
    string? BusinessLogoUrl = null,
    /// <summary>URL de Messenger del negocio para contacto de la clienta.</summary>
    string? BusinessMessengerUrl = null,
    /// <summary>URL de Facebook del negocio.</summary>
    string? BusinessFacebookUrl = null,
    /// <summary>Nombre del repartidor asignado a la ruta. Null si aún no hay ruta activa.</summary>
    string? CourierName = null,
    /// <summary>Teléfono del repartidor (para el botón de llamar). Null si no hay ruta/chofer.</summary>
    string? CourierPhone = null,
    /// <summary>Evaluación previa si la clienta ya calificó este pedido. Null si aún no.</summary>
    OrderRatingDto? Rating = null
);

// ── OrderPayment ──
public record OrderPaymentDto(
    int Id,
    int OrderId,
    decimal Amount,
    string Method,
    DateTime Date,
    string RegisteredBy,
    string? Notes
);

// ── Order Rating (Experiencia Nenis V3) ──
/// <summary>DTO de respuesta con la evaluación ya guardada.</summary>
public record OrderRatingDto(
    int Id,
    int Stars,
    List<string>? Reasons,
    string? Comment,
    DateTime CreatedAt
);

/// <summary>Request de la clienta al enviar su evaluación del pedido.</summary>
public record SubmitOrderRatingRequest
{
    /// <summary>Calificación 1–5 estrellas.</summary>
    [Required, Range(1, 5)]
    public int Stars { get; init; }

    /// <summary>Motivos/stickers seleccionados (ej. ["🎀 El producto", "⚡ La entrega"]).</summary>
    public List<string>? Reasons { get; init; }

    /// <summary>Comentario libre opcional.</summary>
    [MaxLength(1000)]
    public string? Comment { get; init; }
}


public record AddPaymentRequest
{
    [Required]
    public decimal Amount { get; init; }

    [Required, MaxLength(50)]
    public string Method { get; init; } = "Efectivo";

    public string? RegisteredBy { get; init; }  // ✅ nullable

    [MaxLength(500)]
    public string? Notes { get; init; }

    /// <summary>
    /// Fecha real del pago. Si no se manda, se usa DateTime.UtcNow.
    /// Permite registrar pagos con fecha retroactiva para cuadrar reportes.
    /// </summary>
    public DateTime? PaymentDate { get; init; }
};

public record DriverLocationDto(
    double Latitude,
    double Longitude,
    DateTime LastUpdate
);

// ── Dashboard ──
public record DashboardDto(
    int TotalClients,
    int TotalOrders,
    int PendingOrders,
    int DeliveredOrders,
    int NotDeliveredOrders,
    int ActiveRoutes,
    decimal TotalRevenue,
    decimal RevenueMonth,
    decimal RevenueToday,
    decimal TotalInvestment,
    int TotalCashOrders,
    decimal TotalCashAmount,
    int TotalTransferOrders,
    decimal TotalTransferAmount,
    int TotalDepositOrders,
    decimal TotalDepositAmount,
    // Chart data — eliminates N+1 calls from frontend
    List<MonthlySalesDto> SalesByMonth,
    int ClientsNueva,
    int ClientsFrecuente,
    int OrdersDelivery,
    int OrdersPickUp,
    ActivePeriodSummaryDto? ActivePeriod = null,
    decimal PendingAmount = 0m,
    List<OrderSummaryDto>? RecentOrders = null
);

public record ActivePeriodSummaryDto(
    int Id,
    string Name,
    decimal TotalSales,
    decimal TotalInvested,
    decimal NetProfit,
    decimal CollectedAmount = 0m
);

public record MonthlySalesDto(string Month, decimal Sales);
public record CommonProductDto(string Name, int Count, decimal TypicalPrice);

public record OrderCaptureSettingsDto(decimal DefaultShippingCost);

// ── AI Insights ──
public record AiInsightDto(
    string Category, // 'Finanzas', 'Ventas', 'Clientas', 'Riesgo', 'Operación'
    string Title,
    string Description,
    string ActionableAdvice,
    string Icon
);

// ── Reports ──
public record ReportDto(
    // Financiero
    decimal TotalRevenue,      // Billed (Delivered total)
    decimal TotalCollected,    // Actually paid (OrderPayments)
    decimal TotalInvestment,
    decimal TotalExpenses,     // DriverExpenses
    decimal NetProfit,         // TotalRevenue - TotalInvestment - TotalExpenses
    decimal CashBalance,       // TotalCollected - TotalInvestment - TotalExpenses
                               // Pedidos
    int TotalOrders,
    int PendingOrders,
    int InRouteOrders,
    int DeliveredOrders,
    int NotDeliveredOrders,
    int CanceledOrders,
    int DeliveryOrders,
    int PickUpOrders,
    decimal AvgTicket,
    List<TopProductDto> TopProducts,
    List<DailyCountDto> OrdersByDay,
    // Rutas
    int TotalRoutes,
    int CompletedRoutes,
    decimal SuccessRate,
    decimal TotalDriverExpenses,
    // Clientas
    int NewClients,
    int FrequentClients,
    int ActiveClients,
    List<TopClientDto> TopClients,
    // Cobros
    int CashOrders,
    decimal CashAmount,
    int TransferOrders,
    decimal TransferAmount,
    int DepositOrders,
    decimal DepositAmount,
    int UnassignedPaymentOrders,
    decimal UnassignedPaymentAmount,
    // Proveedores
    List<SupplierSummaryDto> SupplierSummaries,
    // Rendimiento
    double AvgDeliveryTimeMinutes = 0,
    double AvgRouteTimeMinutes = 0,
    double AvgDoorTimeMinutes = 0,
    // Comparativa
    decimal PrevPeriodRevenue = 0,
    int PrevPeriodOrders = 0
);

public record TopProductDto(string Name, int Quantity, decimal Revenue);
public record DailyCountDto(string Date, int Count, decimal Amount);
public record TopClientDto(string Name, int Orders, decimal TotalSpent);
public record SupplierSummaryDto(string Name, decimal TotalInvested, int InvestmentCount);

// ── Glow Up (IG Story) ──
public record GlowUpReportDto(
    string MonthName,
    int TotalDeliveries,
    string TopProduct,
    int NewClients
);

public record OrderStatsDto(
    int Total,
    int Pending,
    decimal PendingAmount,
    decimal CollectedToday
);



public record UpdateOrderStatusRequest(
    string? Status,              // ✅ nullable
    string? OrderType,           // ✅ nullable — Angular no lo manda aquí
    DateTime? PostponedAt,
    string? PostponedNote
);

public enum ClientTag
{
    None = 0,         // Normal
    RisingStar = 1,   // En Ascenso 🚀
    Vip = 2,          // Consentida 👑
    Blacklist = 3     // Lista Negra 🚫
}

public record SupplierDto(
    int Id,
    string Name,
    string? ContactName,
    string? Phone,
    string? Notes,
    DateTime CreatedAt,
    decimal TotalInvested = 0m
);
public record UpdateClientRequest(string Name, string? Phone, string? Address, ClientTag Tag, string Type, string? DeliveryInstructions, string? FacebookProfileUrl = null);

// ── Importación masiva de Facebook de clientas ──

/// <summary>Una fila cruda del archivo/pegado: nombre tal como viene + enlace de FB.</summary>
public record FacebookImportRow(string Name, string FacebookUrl);

public record FacebookImportPreviewRequest(List<FacebookImportRow> Rows);

/// <summary>
/// Resultado del matching difuso de una fila contra las clientas existentes.
/// Status: "matched" (match claro, premarcado) | "review" (ambiguo, revisar) | "notfound" (sin match confiable).
/// </summary>
public record FacebookImportPreviewItem(
    int RowIndex,
    string InputName,
    string InputUrl,
    bool UrlValid,
    string Status,
    int? SuggestedClientId,
    double TopScore,
    bool TopAlreadyHasFacebook,
    bool DuplicateUrlInBatch,
    List<ResolveCandidateDto> Candidates);

public record FacebookImportPreviewResponse(List<FacebookImportPreviewItem> Items);

/// <summary>Una asignación confirmada por el usuario: a esta clienta, este enlace.</summary>
public record FacebookImportApplyRow(int ClientId, string FacebookUrl);

public record FacebookImportApplyRequest(List<FacebookImportApplyRow> Rows);

public record FacebookImportApplyResponse(int Applied, int Skipped, List<string> Errors);

public record CreateSupplierRequest
{
    [Required, MaxLength(200)]
    public string Name { get; init; } = string.Empty;

    [MaxLength(200)]
    public string? ContactName { get; init; }

    [MaxLength(50)]
    public string? Phone { get; init; }

    [MaxLength(500)]
    public string? Notes { get; init; }
}

public record UpdateSupplierRequest
{
    [Required, MaxLength(200)]
    public string Name { get; init; } = string.Empty;

    [MaxLength(200)]
    public string? ContactName { get; init; }

    [MaxLength(50)]
    public string? Phone { get; init; }

    [MaxLength(500)]
    public string? Notes { get; init; }
}

// ── Investment DTOs ──

public record InvestmentDto(
    int Id,
    int SupplierId,
    decimal Amount,
    DateTime Date,
    string? Notes,
    DateTime CreatedAt,
    string Currency,
    decimal ExchangeRate,
    decimal TotalMXN,
    int? SalesPeriodId = null,
    string? SalesPeriodName = null
);

public record CreateInvestmentRequest
{
    [Required]
    public decimal Amount { get; init; }

    [Required]
    public DateTime Date { get; init; }

    [MaxLength(500)]
    public string? Notes { get; init; }

    public string Currency { get; set; } = "MXN";
    public decimal? ExchangeRate { get; set; }

    public int? SalesPeriodId { get; init; }
}

public record DriverExpenseDto(
    int Id,
    int? DriverRouteId,
    string? DriverName,
    decimal Amount,
    string ExpenseType,
    DateTime Date,
    string? Notes,
    string? EvidenceUrl,
    DateTime CreatedAt
);

public record CreateDriverExpenseRequest
{
    [Required]
    public decimal Amount { get; init; }

    [Required, MaxLength(50)]
    public string ExpenseType { get; init; } = "Gasolina";

    [MaxLength(500)]
    public string? Notes { get; init; }
}

public record FinancialReportDto
{
    public string Period { get; init; } = string.Empty;
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
    public decimal TotalBilled { get; init; }      // Lo que se facturó (Ordenes Entregadas)
    public decimal TotalCollected { get; init; }   // Lo que entró realmente a caja (Pagos)
    public decimal TotalPending { get; init; }     // Diferencia (Billed - Collected)
    public decimal TotalInvestment { get; init; }
    public decimal TotalExpenses { get; init; }
    public decimal NetProfit { get; init; }        // Utilidad teórica (Billed - Inv - Exp)
    public decimal CashBalance { get; init; }      // Dinero real en mano (Collected - Inv - Exp)
    public FinancialDetailsDto Details { get; init; } = new();
}

public record FinancialDetailsDto
{
    public List<InvestmentLineDto> Investments { get; init; } = new();
    public List<IncomeLineDto> Incomes { get; init; } = new();
    public List<ExpenseLineDto> Expenses { get; init; } = new();
}

public record InvestmentLineDto(
    int Id,
    string SupplierName,
    decimal Amount,
    DateTime Date,
    string? Notes
);

public record IncomeLineDto(
    int Id,
    string ClientName,
    decimal Total,
    string OrderType,
    DateTime CreatedAt
);

public record ExpenseLineDto(
    int Id,
    int? DriverRouteId,
    string? RouteName,
    string? DriverName,
    decimal Amount,
    string ExpenseType,
    DateTime Date,
    string? Notes,
    string? EvidenceUrl
);

// DTO para marcar/desmarcar que el enlace ya fue enviado a la clienta
public record SetNotifiedRequest(bool Notified);

// DTO para actualizar la orden completa
public record UpdateOrderDetailsRequest(
    string? Status,
    string? OrderType,
    DateTime? PostponedAt,
    string? PostponedNote,
    string ClientName,
    string? ClientAddress,
    string? ClientPhone,
    string? Type, // Added
    List<string>? Tags,
    string? DeliveryTime,
    string? PickupDate,
    decimal? ShippingCost = null,
    decimal? AdvancePayment = null,
    int? SalesPeriodId = null,
    string? DeliveryInstructions = null,
    string? AlternativeAddress = null,
    DateTime? ScheduledDeliveryDate = null,
    string? ClientFacebookProfileUrl = null
);

// DTO para actualizar un producto individual
public record UpdateOrderItemRequest(
    string ProductName,
    int Quantity,
    decimal UnitPrice
);

public record SendMessageRequest(string Text);
public record UpdateInstructionsRequest(string Instructions);


// ── SalesPeriods (Cortes de Venta) ──
public record SalesPeriodDto(
    int Id,
    string Name,
    DateTime StartDate,
    DateTime EndDate,
    bool IsActive,
    DateTime CreatedAt
);

public record CreateSalesPeriodRequest
{
    [Required, MaxLength(200)]
    public string Name { get; init; } = string.Empty;

    [Required]
    public DateTime StartDate { get; init; }

    [Required]
    public DateTime EndDate { get; init; }
}

public record PeriodReportDto(
    int PeriodId,
    string PeriodName,
    decimal TotalSales,          // Billed
    decimal TotalCollected,      // Actually paid
    decimal TotalInvestments,
    decimal TotalExpenses,       // Driver expenses in this period
    decimal NetProfit,           // Billed - Inv - Exp
    decimal CashBalance,         // Collected - Inv - Exp
    List<PeriodInvestmentBySupplierDto> InvestmentsBySupplier
);

public record PeriodInvestmentBySupplierDto(
    string SupplierName,
    decimal TotalInvested,
    int InvestmentCount
);

public record SyncSalesPeriodRequest(
    DateTime InvStartDate,
    DateTime InvEndDate,
    DateTime OrderStartDate,
    DateTime OrderEndDate
);

// ── Paquetes y Logística ──
public record GeneratePackagesRequest(int Count);

public record OrderPackageDto(
    Guid Id,
    int PackageNumber,
    string QrCodeValue,
    string Status,
    DateTime CreatedAt,
    DateTime? LoadedAt,
    DateTime? DeliveredAt,
    DateTime? ReturnedAt = null
);

public record ScanPackageRequest(
    string QrCodeValue,
    string Action // "Load" | "Deliver" | "Return"
);

// ── C.A.M.I. ──
public record CamiMessageDto(string Role, string Text); // Role: "user" | "model"
public record CamiChatRequest(List<CamiMessageDto> History, string NewMessage);
public record CamiChatResponse(string Text, string? AudioBase64 = null);

// ── C.A.M.I. DRIVER COPILOT ──
public record DriverCamiRequest(string CommandText);
public record DriverCamiResponse(string RespuestaCami);

// ── C.A.M.I. PROACTIVE GREETING ──
public record CamiGreetingResponse(string Message, string? AudioBase64 = null);

// ── CAMI Insights ──
public record CamiAlert(string Type, string Message, string Icon, int? RelatedId = null);
public record RouteBriefingResponse(string Text, string? AudioBase64 = null);
public record DashboardInsightRequest(decimal RevenueToday, decimal RevenueMonth, int PendingOrders, int DeliveredOrders, int ActiveRoutes, decimal PendingAmount, int TotalClients);

// ── C.A.M.I. Proactive Suggestions ──
public record CamiProactiveSuggestionDto(
    string Kind,        // "live-pending", "live-stuck", "duplicates"
    string Icon,
    string Title,
    string Detail,
    string ActionLabel,
    string ActionRoute,
    int Priority);

// ── POS ──
public record OpenSessionRequest(decimal InitialCash);
public record CloseSessionRequest(int SessionId, decimal ActualCash);
public record PaymentRequest(int OrderId, int SessionId, decimal Amount, string Method);
public record ScanItemRequest(int OrderId, string Sku);
public record CreatePosOrderRequest(string ClientName);

// ── POS VOICE ASSISTANT (CAMI) ──
public record PosVoiceRequest(string Text, int? OrderId = null);
public record PosVoiceResponse(string Message, string? AudioBase64, List<PosVoiceAction> Actions);
public record PosVoiceAction(string Type, string? ClientName = null, string? ProductName = null, decimal? Price = null, int? Quantity = null);

// ── Pago con Tarjeta (Mercado Pago) ──
public record CardPaymentRequest(
    string CardToken,
    string PaymentMethodId,
    string? IssuerId,
    int Installments);

public record CardPaymentResultDto(
    string Status,
    string StatusDetail,
    decimal Amount,
    string Message,
    long? PaymentId = null);

// ── Identidad multi-señal de clientas ──

/// <summary>
/// Request al resolver: pide al backend que identifique a qué clienta corresponde
/// el nombre tecleado/dictado, opcionalmente con teléfono y dirección.
/// </summary>
public record ResolveClientRequest(
    string Name,
    string? Phone = null,
    string? Address = null);

/// <summary>
/// Una candidata propuesta por el resolver con su score y por qué señal hizo match.
/// </summary>
public record ResolveCandidateDto(
    int ClientId,
    string Name,
    string? Phone,
    string? Address,
    string Tag,
    string Type,
    int OrdersCount,
    decimal TotalSpent,
    List<string> Aliases,
    decimal BalanceDue,
    double Score,
    string MatchedBy); // "alias" | "phone" | "name-fuzzy" | "address-fuzzy"

/// <summary>
/// Respuesta del resolver con los top-N candidatos y una acción sugerida para la UI.
/// </summary>
public record ResolveClientResponse(
    List<ResolveCandidateDto> Candidates,
    string SuggestedAction); // "use" (top muy claro), "choose" (ambiguo), "create" (no hay match)

public record AddAliasRequest(
    string Alias,
    string? Source = null); // ClientAliasSource serializado o null para ManualConfirm

public record MergeClientsRequest(
    int SourceId,
    int TargetId);

public record DuplicateSuggestionDto(
    int LeftClientId,
    string LeftName,
    int LeftOrdersCount,
    int RightClientId,
    string RightName,
    int RightOrdersCount,
    string Reason, // "same-phone", "similar-name", "similar-address"
    double Confidence);

public record ClientAliasDto(
    int Id,
    string Alias,
    string Source,
    int TimesSeen,
    DateTime CreatedAt);

// ── Live Capture ──

public record ImportLiveRequest(string FacebookUrl, string? Title = null);

public record LiveSessionDto(
    int Id,
    string FacebookUrl,
    string? Title,
    string Status,
    string? StatusDetail,
    DateTime ImportedAt,
    DateTime? ProcessedAt,
    double? DurationSeconds,
    int ProductCount,
    int CandidateCount,
    int PendingCount,
    string? Transcript = null);

public record LiveProductDto(
    int Id,
    string Keyword,
    string? Description,
    decimal Price,
    double? AnnouncedAtSeconds,
    int CandidateCount);

public record LiveCandidateDto(
    int Id,
    string Keyword,
    int? LiveProductId,
    string? ClientNameSpoken,
    string? CommentDisplayName,
    int? ResolvedClientId,
    string? ResolvedClientName,
    string? ProposedAliasPairJson,
    string Source,   // "Spoken" | "Comment" | "SpokenAndComment"
    string Status,   // "Pending" | "Confirmed" | "Ignored"
    double? SpokenAtSeconds = null);

public record LiveReviewDto(
    LiveSessionDto Session,
    List<LiveProductDto> Products,
    Dictionary<int, List<LiveCandidateDto>> CandidatesByProduct,
    List<LiveCandidateDto> UnmatchedCandidates);

public record ConfirmCandidateRequest(
    int? ClientId = null,          // null = create new client
    string? ClientName = null,     // used when creating new client
    string? ProductOverride = null,
    decimal? PriceOverride = null,
    bool AcceptAlias = false);

public record ClientMergeAuditDto(
    int Id,
    int SourceClientId,
    string SourceName,
    int TargetClientId,
    string TargetName,
    string Mode,        // "Manual" | "Auto"
    string? Reason,
    double Confidence,
    int OrdersMoved,
    int AliasesMoved,
    DateTime MergedAt);

// ── Reclamar perfil (plan 0.3) ──

/// <summary>
/// Candidato a reclamar para una Account autenticada. Datos no sensibles del Client
/// + nombre del negocio para que la clienta decida a qué vendedora pertenece.
/// NO expone teléfono, dirección ni pedidos: eso se ve solo después de reclamar.
/// </summary>
public record ClientClaimCandidateDto(
    int ClientId,
    int BusinessId,
    string BusinessName,
    string ClientName,
    string? City,
    int OrdersCount,
    DateTime? LastOrderAt,
    string MatchedBy);   // "phone" | "order-token"

/// <summary>
/// Resultado de un enlace Account ↔ Client.
/// </summary>
public record ClientClaimResultDto(
    int ClientId,
    int BusinessId,
    string BusinessName,
    string ClientName,
    string LinkedBy,         // "order-token" | "phone-match" | "idempotent"
    DateTime ClaimedAt);

/// <summary>
/// Detalle del Client que la Account YA tiene reclamado (camino cross-tenant).
/// Se omite teléfono y dirección: la clienta ya sabe los suyos; el panel admin
/// del OTRO negocio NO debe filtrarse aquí.
/// </summary>
public record ClaimedClientSummaryDto(
    int ClientId,
    int BusinessId,
    string BusinessName,
    string ClientName,
    string LinkedBy,
    DateTime ClaimedAt);

// ── Feed de la compradora (app Flutter, Fase 2) ──
public record BuyerHomeDto(
    string DisplayName,
    int TotalPoints,
    BuyerActiveOrderDto? ActiveOrder,
    List<BuyerStoreDto> Stores,
    List<BuyerRecentOrderDto> RecentOrders,
    int LiveCount);

public record BuyerStoreDto(
    int BusinessId,
    string Name,
    string? Slug,
    string BrandPrimaryColor,
    string? LogoUrl,
    int Points,
    bool IsLive);

public record BuyerActiveOrderDto(
    int OrderId,
    int BusinessId,
    string BusinessName,
    string BrandPrimaryColor,
    string Status,
    string? AccessToken,
    DateTime? ScheduledDeliveryDate,
    decimal Total);

public record BuyerRecentOrderDto(
    int OrderId,
    int BusinessId,
    string BusinessName,
    string BrandPrimaryColor,
    string Status,
    int ItemsCount,
    decimal Total,
    DateTime CreatedAt,
    string? AccessToken);

/// <summary>
/// Filtro aceptado por GET /api/me/orders. "open" agrupa los estados en curso
/// (Pending/Confirmed/Shipped/InRoute); "closed" agrupa los terminales
/// (Delivered/Canceled/NotDelivered/Postponed). "all" no filtra.
/// </summary>
public static class BuyerOrderFilter
{
    public const string All = "all";
    public const string Open = "open";
    public const string Closed = "closed";

    public static bool IsValid(string? value) =>
        value is null
        || string.Equals(value, All, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Open, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Closed, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Pedido cross-tenant para la pantalla "Mis pedidos" de la compradora.
/// Es la versión "completa" de BuyerRecentOrderDto (incluye AccessToken para
/// poder abrir el rastreo y logo de la tienda).
/// </summary>
public record BuyerOrderDto(
    int OrderId,
    int BusinessId,
    string BusinessName,
    string BrandPrimaryColor,
    string? LogoUrl,
    string Status,
    int ItemsCount,
    decimal Total,
    string? AccessToken,
    DateTime CreatedAt,
    DateTime? ScheduledDeliveryDate);

/// <summary>
/// Respuesta paginada de GET /api/me/orders. Page es 1-based; Total es el
/// conteo total antes de paginar (útil para que la app muestre "Página X de Y").
/// </summary>
public record BuyerOrdersResponse(
    List<BuyerOrderDto> Orders,
    int Total,
    int Page,
    int PageSize,
    string Filter,
    int? BusinessId);

/// <summary>
/// Premio canjeable visto por la compradora. Es una copia del LoyaltyRewardDto
/// del lado vendedora (Controllers/LoyaltyController.cs) para no acoplar la
/// app del cliente al modelo interno.
/// </summary>
public record BuyerRewardDto(
    int Id,
    string Name,
    string? Description,
    int PointsCost,
    string Type,
    decimal Value,
    string? Icon);

/// <summary>
/// Catálogo de premios activos de UNA tienda de la compradora, junto con los
/// puntos que ella tiene acumulados en esa tienda. Si la tienda no tiene
/// premios configurados, igual aparece con `rewards` vacío (para mostrar
/// "Pronto habrá premios" en la app).
/// </summary>
public record RewardsByBusinessDto(
    int BusinessId,
    string BusinessName,
    string BrandPrimaryColor,
    string? LogoUrl,
    int StorePoints,
    List<BuyerRewardDto> Rewards);

/// <summary>
/// Tanda vista por la compradora (cross-tenant por AccountId). Si la
/// compradora está inscrita, `IsMine = true` y los campos opcionales traen
/// su turno, semanas pagadas y si es la ganadora de la semana actual.
/// Si NO está inscrita, la tanda aparece igual para que la app pueda
/// listarla en "Disponibles" — esos campos quedan null.
/// </summary>
public record MyTandaDto(
    Guid TandaId,
    int BusinessId,
    string BusinessName,
    string BrandPrimaryColor,
    int ClientId,
    string Name,
    string ProductName,
    int TotalWeeks,
    decimal WeeklyAmount,
    DateTime StartDate,
    string Status,
    int CurrentWeek,
    bool IsMine,
    int? MyTurn,
    bool? HasPaidThisWeek,
    List<int> PaidWeeks,
    bool? AmIThisWeekWinner);

/// <summary>
/// Sorteo visto por la compradora (cross-tenant por AccountId). Si tiene
/// al menos una entrada (RaffleEntry) o aparece como RaffleParticipant,
/// `IsMineEntered = true` y trae `MyEntryCount` (boletos) y/o `AmIWinner`.
/// Si no, aparece igual para mostrar sorteos activos de sus tiendas.
/// </summary>
public record MyRaffleDto(
    Guid RaffleId,
    int BusinessId,
    string BusinessName,
    string BrandPrimaryColor,
    int ClientId,
    string Name,
    string? ImageUrl,
    string PrizeType,
    decimal? PrizeValue,
    string? PrizeDescription,
    DateTime RaffleDate,
    string Status,
    string? TandaName,
    int MyEntryCount,
    bool IsMineEntered,
    bool AmIWinner,
    DateTime? AnnouncedAt);

/// <summary>
/// Producto del catálogo público de una tienda (sin imágenes — el modelo
/// `Product` no las tiene todavía). Se devuelve una selección acotada de
/// los productos activos más recientes para alimentar la grid del tab
/// "Productos" de la pantalla de tienda.
/// </summary>
public record BuyerProductDto(
    int Id,
    string Sku,
    string Name,
    decimal Price,
    int Stock);

/// <summary>
/// Resumen del live activo de una tienda (Status = Ready). Se incluye
/// solo si hay una sesión de live "publicada" en el momento del GET.
/// </summary>
public record BuyerLiveSummaryDto(
    int SessionId,
    string Title,
    int ViewerCount,
    string? Topics,
    DateTime? ProcessedAt);

/// <summary>
/// Puntos de la compradora en una tienda y el costo de la próxima reward
/// que podría canjear (o null si la tienda no tiene rewards configuradas).
/// </summary>
public record BuyerStorePointsDto(
    int CurrentPoints,
    int? NextRewardAt);

/// <summary>
/// Vista de tienda para la pantalla `/store/{businessId}`. Single endpoint
/// que devuelve header, puntos, live (si hay), productos y counts para
/// que la app pinte la pantalla sin encadenar requests.
/// </summary>
public record BuyerStoreDetailDto(
    int BusinessId,
    string Name,
    string? Slug,
    string? City,
    string? LogoUrl,
    string BrandPrimaryColor,
    string? BrandAccentColor,
    int ClientCount,
    bool IsVerified,
    BuyerStorePointsDto Points,
    BuyerLiveSummaryDto? Live,
    List<BuyerProductDto> Products,
    int ActiveTandasCount,
    int ActiveRafflesCount,
    int FollowerCount,
    bool IsFollowing,
    bool IsVip,
    bool IsLiveNow,
    string? LiveAnnouncementTitle,
    double? AverageRating,
    int RatingsCount);

/// <summary>Estado de "seguir" de la compradora sobre una tienda.</summary>
public record FollowStateDto(
    int BusinessId,
    bool IsFollowing,
    bool NotifyOnPost,
    bool NotifyOnLive,
    bool IsVip);

public record FollowPreferencesRequest(bool NotifyOnPost, bool NotifyOnLive);

/// <summary>Fila de la lista de seguidoras que ve la vendedora (gestión VIP).</summary>
public record StoreFollowerAdminDto(
    int AccountId,
    string DisplayName,
    DateTime FollowedAt,
    bool IsVip,
    DateTime? VipSince);

public record RegisterDeviceRequest(string Token, string Platform);

// ── Comunidad de tienda: en vivo ahora + novedades + VIP ──

public record StartLiveAnnouncementRequest(string? Title);

public record LiveAnnouncementDto(int Id, string? Title, DateTime StartedAt, bool IsActive);

public record CreateStorePostRequest(string Body, string? ImageUrl, bool IsVipOnly);

public record StorePostDto(
    int Id,
    string Body,
    string? ImageUrl,
    bool IsVipOnly,
    DateTime CreatedAt);

public record StorePostFeedItemDto(
    int Id,
    int BusinessId,
    string BusinessName,
    string BrandPrimaryColor,
    string? LogoUrl,
    string Body,
    string? ImageUrl,
    bool IsVipOnly,
    bool IsLocked,
    DateTime CreatedAt);

public record SetFollowerVipRequest(bool IsVip);

/// <summary>
/// Request para que la compradora aparte un producto de una tienda.
/// Crea un `Order` con `Status = Pending` y `OrderType = PickUp`. La
/// respuesta es el mismo `BuyerOrderDto` que `GET /api/me/orders` para
/// que la app pueda navegar a `/tracking/{id}?token=…` de una vez.
/// </summary>
public record ReserveProductRequest(
    int BusinessId,
    int ProductId,
    int Quantity = 1);

/// <summary>
/// Pago visto por la compradora (cross-tenant por AccountId). Es la
/// unión de `OrderPayment` con info mínima del `Order` y la tienda para
/// que la app pueda agrupar/mostrar sin hacer N+1 requests.
/// </summary>
public record BuyerPaymentDto(
    int PaymentId,
    int OrderId,
    int BusinessId,
    string BusinessName,
    string BrandPrimaryColor,
    string? LogoUrl,
    decimal Amount,
    string Method,
    DateTime Date,
    string RegisteredBy,
    string? Notes,
    string OrderStatus,
    decimal OrderTotal);

/// <summary>
/// Dirección de la compradora en una tienda (cross-tenant por AccountId).
/// Es la "dirección de entrega" que la tienda tiene guardada para esta
/// clienta. El modelo actual asume 1 dirección por Client.
/// </summary>
public record BuyerAddressDto(
    int ClientId,
    int BusinessId,
    string BusinessName,
    string BrandPrimaryColor,
    string? LogoUrl,
    string? Address,
    double? Latitude,
    double? Longitude,
    string? DeliveryInstructions);

/// <summary>
/// Body para que la compradora actualice su dirección en una tienda.
/// Solo se modifican los campos no-null; los null se dejan sin tocar.
/// </summary>
public record UpdateBuyerAddressRequest(
    string? Address = null,
    double? Latitude = null,
    double? Longitude = null,
    string? DeliveryInstructions = null);

/// <summary>
/// Notificación persistida vista por la compradora. Se devuelve
/// ordenada por fecha desc y se filtra por los Client de la Account
/// (cross-tenant). El `Tag` lo usa la app para decidir icono/cta.
/// </summary>
public record BuyerNotificationDto(
    Guid Id,
    int BusinessId,
    string BusinessName,
    string BrandPrimaryColor,
    string Title,
    string Message,
    string Tag,
    string? Url,
    int? OrderId,
    DateTime CreatedAt,
    DateTime? ReadAt);

// ── Suscripcion de plataforma (Fase 1.3) ──

/// <summary>
/// Configuracion de precios que el frontend de pago (FE-4) consume para
/// mostrar el resumen del plan y la periodicidad elegida.
/// El backend es la fuente de verdad: el front NO inventa precios.
/// </summary>
public record SubscriptionPlanPriceDto(
    string PlanTier,
    decimal MonthlyPrice,
    decimal QuarterlyPrice,
    decimal AnnualPrice,
    int QuarterlyDiscountPct,
    int AnnualDiscountPct,
    string Currency);

public record SubscriptionPricingDto(
    List<SubscriptionPlanPriceDto> Plans,
    string Currency);

/// <summary>
/// Llave publica de MP de la PLATAFORMA (no del tenant). El brick de tarjeta
/// de FE-4 la consume para tokenizar la tarjeta del owner.
/// </summary>
public record PlatformMpPublicKeyDto(string PublicKey);

/// <summary>
/// Request para crear el preapproval. El backend completa auto_recurring con
/// los precios de la periodicidad elegida; el cardTokenId es opcional
/// (cuando el brick lo entrega listo para cobros recurrentes).
/// </summary>
public record CreatePreapprovalRequest(
    string PlanTier,
    string Periodicity,
    string PayerEmail,
    string? CardTokenId = null);

/// <summary>
/// Request para ajustar un preapproval existente (upgrade de plan o cambio
/// de periodicidad). Se aplica de inmediato porque es upgrade; downgrade usa
/// PendingPlanTier de 1.2.
/// </summary>
public record UpdatePreapprovalRequest(
    string PlanTier,
    string Periodicity);

public record PreapprovalSummaryDto(
    string PreapprovalId,
    string PlanTier,
    string Periodicity,
    decimal Amount,
    string Currency,
    string Status,
    DateTime? NextPaymentDate,
    DateTime? CurrentPeriodEndsAt,
    DateTime? CancellationEffectiveAt,
    string? InitPoint);

// ── Kit de marca por tenant (Fase FE-0) ──

public record BrandDto(
    string? LogoUrl,
    string? BannerUrl,
    string BrandPrimaryColor,
    string? BrandAccentColor,
    string? MessengerUrl = null,
    string? FacebookUrl = null);

public record UpdateBrandRequest(
    string? Name = null,
    string? BrandPrimaryColor = null,
    string? BrandAccentColor = null,
    string? MessengerUrl = null,
    string? FacebookUrl = null);

public record BrandAssetDto(string Kind, string Url);

public record MercadoPagoPaymentSettingsDto(
    string? PublicKey,
    bool HasAccessToken,
    bool IsConfigured);

public record UpdateMercadoPagoPaymentSettingsRequest(
    string? PublicKey,
    string? AccessToken = null,
    bool ClearAccessToken = false);

public record PayoutAccountDto(
    int Id,
    string Kind,
    string KindLabel,
    string HolderName,
    string? BankName,
    string? Alias,
    string MaskedNumber,
    int NumberLength,
    string? Notes,
    bool IsDefault,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record CreatePayoutAccountRequest(
    string Kind,
    string HolderName,
    string AccountNumber,
    string? BankName = null,
    string? Alias = null,
    string? Notes = null,
    bool IsDefault = false);

public record UpdatePayoutAccountRequest(
    string? Kind = null,
    string? HolderName = null,
    string? AccountNumber = null,
    string? BankName = null,
    string? Alias = null,
    string? Notes = null,
    bool? IsDefault = null);

public record SubscriptionSummaryDto(
    string EffectivePlan,
    string SubscriptionStatus,
    DateTime? TrialEndsAt,
    DateTime? CurrentPeriodEndsAt,
    bool IsLocked,
    int DaysLeft,
    int PastDueGraceDays);

public record BusinessMeDto(
    int Id,
    string Name,
    string Slug,
    string? City,
    BrandDto Brand,
    SubscriptionSummaryDto Subscription,
    List<string> Features);
