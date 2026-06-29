namespace EntregasApi.Migrator.Migration;

/// <summary>
/// Una "tabla a copiar" del origen al destino. El campo clave es <see cref="TableName"/>;
/// los corchetes/double-quotes alrededor del nombre se aplican al construir SQL.
/// </summary>
/// <param name="TableName">
/// Nombre real de la tabla en Postgres. Para tablas con case folding raro (e.g. snake_case
/// "tandas" vs PascalCase "Orders") se respeta el original del esquema fuente.
/// </param>
/// <param name="BusinessIdColumn">
/// Columna de BusinessId a estampar, o null si la tabla no lleva BusinessId
/// (por ejemplo "Businesses" misma, "Accounts", "Memberships").
/// </param>
/// <param name="SourceTable">
/// Nombre de la tabla en el ORIGEN si difiere del destino (e.g. "Users" -> tabla "Accounts" en destino).
/// Por defecto es igual a <paramref name="TableName"/>.
/// </param>
public sealed record TableSpec(
    string TableName,
    string? BusinessIdColumn,
    string? SourceTable = null,
    IReadOnlyList<string>? ExtraSourceColumnsToDrop = null)
{
    public string SourceTableName => SourceTable ?? TableName;
    public IReadOnlyList<string> DropColumns => ExtraSourceColumnsToDrop ?? Array.Empty<string>();
}

/// <summary>
/// Plan completo de la migracion: orden de insercion, transformaciones y metricas objetivo.
/// </summary>
public static class MigrationPlan
{
    public const int BusinessId = 1;

    /// <summary>Tablas que no se copian: o porque no existen en el origen, o porque son de "corte" (Users ya no existe).</summary>
    public static readonly IReadOnlyList<string> SkippedSourceTables = new[]
    {
        "Users",            // se transforma en Accounts + Memberships
        "AppSettings",      // se regenera (ver transformation F)
        "__EFMigrationsHistory",
    };

    /// <summary>Tablas vacias en el origen (se migran vacias, verificadas por conteo 0). Plan dice: Products(POS), SalesPeriods, ClientMergeAudits, LiveProducts, LiveSpokenOrders, LiveCommentOrders, LiveCandidates.</summary>
    public static readonly IReadOnlyList<string> EmptyTables = new[]
    {
        "Products",          // POS, 0 filas
        "SalesPeriods",      // 0 filas
        "ClientMergeAudits", // 0 filas
        "LiveProducts",      // 0 filas
        "LiveSpokenOrders",  // 0 filas
        "LiveCommentOrders", // 0 filas
        "LiveCandidates",    // 0 filas
    };

    /// <summary>
    /// Plan en orden topologico: padres antes que hijos.
    /// BusinessIdColumn es la columna destino a estampar con =1, o null si no aplica.
    /// SourceTable es la tabla en el origen (e.g. "Users") cuando difiere de la destino ("Accounts").
    /// </summary>
    public static readonly IReadOnlyList<TableSpec> TablesInOrder = new List<TableSpec>
    {
        // --- Fase 1: raices de identidad ---
        new("Businesses", BusinessIdColumn: null),
        new("Accounts",   BusinessIdColumn: null, SourceTable: "Users",
            ExtraSourceColumnsToDrop: new[] { "Rol" }),
        new("Memberships", BusinessIdColumn: null), // se insertan a mano desde Users, ver UsersToAccountsMapper

        // --- Fase 2: tablas tenant-ownadas de nivel 0 (sin FK a otras tenant-ownadas) ---
        new("AppSettings",     BusinessIdColumn: "BusinessId"),
        new("Clients",         BusinessIdColumn: "BusinessId"),
        new("DeliveryRoutes",  BusinessIdColumn: "BusinessId"),
        new("Suppliers",       BusinessIdColumn: "BusinessId"),
        new("LoyaltyRewards",  BusinessIdColumn: "BusinessId"),
        new("FcmTokens",       BusinessIdColumn: "BusinessId"),
        new("PushSubscriptions", BusinessIdColumn: "BusinessId"),
        // ChatMessages: el modelo NO implementa ITenantOwned y la migracion 0.1 no le
        // agrego BusinessId. Se copia tal cual (el aislamiento en runtime es por DeliveryRouteId).
        new("ChatMessages",    BusinessIdColumn: null),

        // --- Fase 3: productos (POS y Tandas son DISTINTOS en origen y destino) ---
        new("Products",        BusinessIdColumn: "BusinessId"), // POS, 0 filas en origen
        new("products",        BusinessIdColumn: "business_id"), // TandaProduct
        new("tandas",          BusinessIdColumn: "business_id"),
        new("tanda_participants", BusinessIdColumn: "business_id"),
        new("payments",        BusinessIdColumn: "business_id"),

        // --- Fase 4: sorteos sin FK a Orders (van ANTES de Orders) ---
        new("raffles",             BusinessIdColumn: "business_id"),
        new("raffle_participants", BusinessIdColumn: "business_id"),
        new("raffle_draws",        BusinessIdColumn: "business_id"),

        // --- Fase 5: lealtad / cash / sales periods / investments / driver expenses ---
        new("LoyaltyTransactions", BusinessIdColumn: "BusinessId"),
        new("CashRegisterSessions", BusinessIdColumn: "BusinessId"),
        new("SalesPeriods", BusinessIdColumn: "BusinessId"), // 0 filas en origen
        new("Investments",   BusinessIdColumn: "BusinessId"),
        new("DriverExpenses", BusinessIdColumn: "BusinessId"),

        // --- Fase 6: pedidos y dependencias ---
        new("Orders",         BusinessIdColumn: "BusinessId"),
        new("OrderItems",     BusinessIdColumn: "BusinessId"),
        new("OrderPayments",  BusinessIdColumn: "BusinessId"),
        new("OrderPackages",  BusinessIdColumn: "BusinessId"),
        new("Deliveries",     BusinessIdColumn: "BusinessId"),
        new("DeliveryEvidences", BusinessIdColumn: "BusinessId"),

        // --- Fase 7: raffle_entries (FK a Orders, va DESPUES de Orders) ---
        new("raffle_entries",      BusinessIdColumn: "business_id"),

        // --- Fase 8: live capture ---
        new("LiveSessions",       BusinessIdColumn: "BusinessId"),
        new("LiveProducts",       BusinessIdColumn: "BusinessId"), // 0 filas
        new("LiveSpokenOrders",   BusinessIdColumn: "BusinessId"), // 0 filas
        new("LiveCommentOrders",  BusinessIdColumn: "BusinessId"), // 0 filas
        new("LiveCandidates",     BusinessIdColumn: "BusinessId"), // 0 filas

        // --- Fase 9: client identity ---
        new("ClientAliases",     BusinessIdColumn: "BusinessId"),
        // ClientMergeAudits y ClientClaimAudits: el modelo no implementa ITenantOwned
        // y la migracion 0.1 no les agrego BusinessId. Se copian tal cual.
        new("ClientMergeAudits", BusinessIdColumn: null), // 0 filas
        new("ClientClaimAudits", BusinessIdColumn: null),
    };

    /// <summary>Tablas int-PK con su nombre de columna identity en el esquema destino (para setval).</summary>
    public static readonly IReadOnlyDictionary<string, string> IntPrimaryKeyTables = new Dictionary<string, string>
    {
        // PascalCase
        ["Accounts"] = "Id",
        ["AppSettings"] = "Id",
        ["Businesses"] = "Id",
        ["CashRegisterSessions"] = "Id",
        ["ChatMessages"] = "Id",
        ["ClientAliases"] = "Id",
        ["ClientClaimAudits"] = "Id",
        ["ClientMergeAudits"] = "Id",
        ["Clients"] = "Id",
        ["Deliveries"] = "Id",
        ["DeliveryEvidences"] = "Id",
        ["DeliveryRoutes"] = "Id",
        ["DriverExpenses"] = "Id",
        ["FcmTokens"] = "Id",
        ["Investments"] = "Id",
        ["LiveCandidates"] = "Id",
        ["LiveCommentOrders"] = "Id",
        ["LiveProducts"] = "Id",
        ["LiveSessions"] = "Id",
        ["LiveSpokenOrders"] = "Id",
        ["LoyaltyRewards"] = "Id",
        ["LoyaltyTransactions"] = "Id",
        ["Memberships"] = "Id",
        ["OrderItems"] = "Id",
        ["OrderPayments"] = "Id",
        ["Orders"] = "Id",
        ["Products"] = "Id",
        ["PushSubscriptions"] = "Id",
        ["SalesPeriods"] = "Id",
        ["Suppliers"] = "Id",
        // snake_case (Guid PK, pero las listamos para el reset de secuencias cuando aplique)
    };

    /// <summary>Tablas Guid-PK (no llevan secuencia int, se mencionan para registro).</summary>
    public static readonly IReadOnlyList<string> GuidPrimaryKeyTables = new[]
    {
        "products",
        "tandas",
        "tanda_participants",
        "payments",
        "raffles",
        "raffle_participants",
        "raffle_entries",
        "raffle_draws",
        // PascalCase con PK Guid
        "OrderPackages",
    };

    /// <summary>Conteo esperado de filas en cada tabla para verificacion post-copia.</summary>
    public static readonly IReadOnlyDictionary<string, int> ExpectedRowCounts = new Dictionary<string, int>
    {
        ["Businesses"] = 1,
        ["Accounts"] = 4,
        ["Memberships"] = 4,
        // El resto se compara contra el conteo de origen en --verify.
    };

    /// <summary>Distribucion esperada de roles en Memberships: Admin->Owner, Driver, Scaner (x2).</summary>
    public static readonly IReadOnlyDictionary<string, int> ExpectedMembershipRoles = new Dictionary<string, int>
    {
        ["Owner"] = 1,
        ["Driver"] = 1,
        ["Scaner"] = 2,
    };

    /// <summary>IDs de Orders a chequear como spot-check (deben existir con mismo AccessToken y BusinessId=1).</summary>
    public static readonly int[] SpotCheckOrderIds = { 118, 168, 190, 970 };

    /// <summary>Lista de chequeos de integridad referencial: nombre de tabla, columna FK, tabla padre.</summary>
    public static readonly IReadOnlyList<OrphanCheck> OrphanChecks = new List<OrphanCheck>
    {
        new("Clients",        "AccountId",     "Accounts",     Nullable: true),
        new("Orders",         "ClientId",      "Clients",      Nullable: false),
        new("Orders",         "DeliveryRouteId", "DeliveryRoutes", Nullable: true),
        new("OrderItems",     "OrderId",       "Orders",       Nullable: false),
        new("OrderPayments",  "OrderId",       "Orders",       Nullable: false),
        new("OrderPackages",  "OrderId",       "Orders",       Nullable: false),
        new("Deliveries",     "OrderId",       "Orders",       Nullable: true),
        new("Deliveries",     "DeliveryRouteId", "DeliveryRoutes", Nullable: false),
        new("DeliveryEvidences", "DeliveryId",  "Deliveries",   Nullable: false),
        new("CashRegisterSessions", "AccountId", "Accounts",    Nullable: false),
        new("tanda_participants",  "tanda_id",  "tandas",       Nullable: false),
        new("payments",            "participant_id", "tanda_participants", Nullable: false),
        new("Investments",    "SupplierId",    "Suppliers",    Nullable: false),
        new("raffle_entries","raffle_id",      "raffles",      Nullable: false),
        new("raffle_draws",  "raffle_id",      "raffles",      Nullable: false),
    };

    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}

public sealed record OrphanCheck(string Table, string FkColumn, string ParentTable, bool Nullable);
