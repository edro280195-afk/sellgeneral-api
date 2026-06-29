using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EntregasApi.Migrator.Migration;

/// <summary>
/// Modo --preflight: valida accesibilidad y estado de las 3 bases (origen, ensayo, prod)
/// SIN escribir nada. Lee el connection string desde un archivo con el formato:
///
///   ORIGEN -> nombre: postgresql://...
///   DESTINO_PROD -> nombre: postgresql://...
///   DESTINO_ENSAYO -> nombre: postgresql://...
///
/// El archivo default es `connectionStrings.txt` en el cwd. Se puede sobreescribir con --conn-file.
///
/// Para ORIGEN: confirma que la base es la real de Regi Bazar (conteos esperados).
/// Para DESTINOS: confirma que existen, son accesibles y que NO tienen todavia el
/// esquema multi-tenant (o lo tienen vacio, listo para recibir la migracion).
/// </summary>
public sealed class PreflightChecker
{
    private readonly ILogger<PreflightChecker> _logger;

    // Conteos esperados en ORIGEN segun MIGRACION-DIAGNOSTICO 2026-06-23.
    private static readonly IReadOnlyDictionary<string, long> OrigenEsperado = new Dictionary<string, long>
    {
        ["Orders"] = 669,
        ["Clients"] = 319,
        ["Users"] = 4,
        ["OrderPayments"] = 651,
        ["OrderItems"] = 2539,
        ["DeliveryRoutes"] = 41,
        ["Deliveries"] = 398,
        ["AppSettings"] = 1,
        ["payments"] = 255,        // TandaPayment
        ["tandas"] = 6,
        ["tanda_participants"] = 60,
        ["products"] = 4,          // TandaProduct
        ["raffles"] = 1,
        ["raffle_entries"] = 235,
        ["raffle_participants"] = 78,
        ["raffle_draws"] = 3,
        ["LoyaltyTransactions"] = 592,
        ["LoyaltyRewards"] = 4,
        ["CashRegisterSessions"] = 1,
        ["Investments"] = 29,
        ["Suppliers"] = 12,
        ["FcmTokens"] = 1,
        ["PushSubscriptions"] = 25,
        ["ClientAliases"] = 13,
        ["ClientClaimAudits"] = 0,
        ["ClientMergeAudits"] = 0,
        ["LiveSessions"] = 13,
        ["LiveProducts"] = 0,
        ["LiveSpokenOrders"] = 0,
        ["LiveCommentOrders"] = 0,
        ["LiveCandidates"] = 0,
        ["Products"] = 0,          // POS vacio
        ["SalesPeriods"] = 0,
        ["OrderPackages"] = 7,
        ["ChatMessages"] = 45,
        ["DriverExpenses"] = 7,
        ["DeliveryEvidences"] = 17,
    };

    public PreflightChecker(ILogger<PreflightChecker> logger)
    {
        _logger = logger;
    }

    public async Task<int> RunAsync(MigratorOptions opts, CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("==============================================================");
        Console.WriteLine("  PRE-FLIGHT  (modo solo lectura, no escribe nada)");
        Console.WriteLine("==============================================================");
        Console.WriteLine();

        // 1) Parsear el archivo de connection strings.
        IReadOnlyDictionary<string, string> connections;
        try
        {
            connections = ParseConnFile(opts.ConnFile!);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] No se pudo leer el archivo de conexiones: {opts.ConnFile}");
            Console.WriteLine($"        error: {ex.Message}");
            return 2;
        }

        foreach (var kv in connections)
        {
            var tag = ConnectionStringParser.HostTag(ConnectionStringParser.ToNpgsql(kv.Value));
            Console.WriteLine($"   leido: {kv.Key,-14}  host=...{tag}");
        }
        Console.WriteLine();

        // 2) Conectividad a cada una.
        var origOk = connections.TryGetValue("ORIGEN", out var origRaw) &&
            await CheckConexionAsync("ORIGEN       ", origRaw, cancellationToken);
        var ensOk = connections.TryGetValue("DESTINO_ENSAYO", out var ensRaw) &&
            await CheckConexionAsync("DEST. ENSAYO ", ensRaw, cancellationToken);
        var prodOk = connections.TryGetValue("DESTINO_PROD", out var prodRaw) &&
            await CheckConexionAsync("DEST. PROD   ", prodRaw, cancellationToken);

        // 3) Conteos del origen.
        if (origOk)
        {
            await CheckOrigenConteosAsync(origRaw!, cancellationToken);
        }

        // 4) Estado de cada destino.
        if (ensOk)
        {
            await CheckDestinoEstadoAsync("DEST. ENSAYO ", ensRaw!, cancellationToken);
        }
        if (prodOk)
        {
            await CheckDestinoEstadoAsync("DEST. PROD   ", prodRaw!, cancellationToken);
        }

        Console.WriteLine();
        Console.WriteLine("==============================================================");
        Console.WriteLine("  FIN PRE-FLIGHT");
        Console.WriteLine("==============================================================");
        return 0;
    }

    private async Task<bool> CheckConexionAsync(string label, string rawConnStr, CancellationToken ct)
    {
        var npgsql = ConnectionStringParser.ToNpgsql(rawConnStr);
        var tag = ConnectionStringParser.HostTag(npgsql);
        try
        {
            await using var conn = new NpgsqlConnection(npgsql);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT version()", conn);
            var version = (string)(await cmd.ExecuteScalarAsync(ct) ?? "?");
            var db = conn.Database;
            var serverShort = version.Length > 70 ? version.Substring(0, 70) + "..." : version;
            var state = conn.State;
            Console.WriteLine($"[ OK ] {label} host=...{tag}  db={db}  estado={state}");
            Console.WriteLine($"        version: {serverShort}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] {label} host=...{tag}");
            Console.WriteLine($"        error: {ex.Message}");
            return false;
        }
    }

    private async Task CheckOrigenConteosAsync(string rawConnStr, CancellationToken ct)
    {
        var npgsql = ConnectionStringParser.ToNpgsql(rawConnStr);
        Console.WriteLine();
        Console.WriteLine("--- Conteos de ORIGEN (esperado segun diagnostico 2026-06-23) ---");
        Console.WriteLine();
        Console.WriteLine($"    {"Tabla",-30} {"Actual",10}  {"Esperado",10}  {"Estado"}");
        Console.WriteLine($"    {new string('-', 30)} {new string('-', 10)}  {new string('-', 10)}  {new string('-', 10)}");

        await using var conn = new NpgsqlConnection(npgsql);
        await conn.OpenAsync(ct);

        int totalTablas = 0, okCount = 0, failCount = 0;
        foreach (var (tabla, esperado) in OrigenEsperado.OrderBy(kv => kv.Key))
        {
            totalTablas++;
            long actual;
            // Entrecomillar para preservar case: "Orders" (PascalCase) != orders (snake_case en Postgres).
            var tablaQuoted = "\"" + tabla.Replace("\"", "\"\"") + "\"";
            try
            {
                await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {tablaQuoted}", conn);
                actual = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
            }
            catch
            {
                Console.WriteLine($"    {tabla,-30} {"?",10}  {esperado,10}  [TABLA NO EXISTE]");
                failCount++;
                continue;
            }
            var estado = actual == esperado ? "[OK]" : "[DIFIERE]";
            if (actual == esperado) okCount++; else failCount++;
            Console.WriteLine($"    {tabla,-30} {actual,10}  {esperado,10}  {estado}");
        }
        Console.WriteLine();
        Console.WriteLine($"    Resumen: {okCount}/{totalTablas} tablas con conteo exacto; {failCount} difieren.");
        if (failCount > 0)
        {
            Console.WriteLine("    >> Si las diferencias son pequenas (1-5 filas), puede ser data nueva");
            Console.WriteLine("       del dia a dia. Si son grandes, valida que estes conectado a la base correcta.");
        }
    }

    private async Task CheckDestinoEstadoAsync(string label, string rawConnStr, CancellationToken ct)
    {
        var npgsql = ConnectionStringParser.ToNpgsql(rawConnStr);
        Console.WriteLine();
        Console.WriteLine($"--- {label}: estado del esquema multi-tenant ---");
        Console.WriteLine();

        await using var conn = new NpgsqlConnection(npgsql);
        await conn.OpenAsync(ct);

        bool businessesExists = await TableExistsAsync(conn, "Businesses", ct);
        bool accountsExists = await TableExistsAsync(conn, "Accounts", ct);
        bool membershipsExists = await TableExistsAsync(conn, "Memberships", ct);

        if (!businessesExists || !accountsExists || !membershipsExists)
        {
            Console.WriteLine("    Estado: ESQUEMA MULTI-TENANT NO CREADO");
            Console.WriteLine();
            Console.WriteLine($"    Businesses existe:        {businessesExists}");
            Console.WriteLine($"    Accounts existe:          {accountsExists}");
            Console.WriteLine($"    Memberships existe:       {membershipsExists}");
            Console.WriteLine();
            Console.WriteLine("    >> Accion: hay que correr `dotnet ef database update` para crear el esquema.");
            Console.WriteLine("       La base esta lista para recibir las migraciones (vacia o con esquema viejo).");
            return;
        }

        long ScalarCount(string sql)
        {
            using var cmd = new NpgsqlCommand(sql, conn);
            return (long)(cmd.ExecuteScalarAsync(ct).GetAwaiter().GetResult() ?? 0L);
        }
        long businesses = ScalarCount("SELECT COUNT(*) FROM \"Businesses\"");
        long accounts = ScalarCount("SELECT COUNT(*) FROM \"Accounts\"");
        long memberships = ScalarCount("SELECT COUNT(*) FROM \"Memberships\"");
        long orders = ScalarCount("SELECT COUNT(*) FROM \"Orders\"");
        long clients = ScalarCount("SELECT COUNT(*) FROM \"Clients\"");

        Console.WriteLine($"    Businesses: {businesses}   (esperado post-migracion: 1)");
        Console.WriteLine($"    Accounts:   {accounts}   (esperado post-migracion: 4)");
        Console.WriteLine($"    Memberships:{memberships}  (esperado post-migracion: 4)");
        Console.WriteLine($"    Orders:     {orders}  (esperado post-migracion: 669)");
        Console.WriteLine($"    Clients:    {clients}  (esperado post-migracion: 319)");
        Console.WriteLine();

        if (businesses == 0 && accounts == 0 && memberships == 0)
        {
            Console.WriteLine("    Estado: ESQUEMA MULTI-TENANT CREADO, VACIO");
            Console.WriteLine("    >> OK para correr la migracion. El migrador poblara las 4 cuentas + 4 memberships.");
        }
        else if (businesses == 1 && accounts == 4)
        {
            Console.WriteLine("    Estado: YA MIGRADO");
            Console.WriteLine("    >> El migrador ABORTARA (pre-check: destino no vacio). Si queres re-migrar,");
            Console.WriteLine("       primero limpia el destino (DROP SCHEMA + dotnet ef database update).");
        }
        else
        {
            Console.WriteLine($"    Estado: INCONSISTENTE (Businesses={businesses}, Accounts={accounts})");
            Console.WriteLine("    >> NO se reconoce este estado. Investigar antes de continuar.");
        }
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = @t)",
            conn);
        cmd.Parameters.AddWithValue("t", table);
        return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
    }

    /// <summary>
    /// Parsea un archivo con el formato:
    ///
    ///   ORIGEN -> produccion: postgresql://...
    ///   DESTINO_PROD -> prod-cutover: postgresql://...
    ///   DESTINO_ENSAYO -> staging-cutover: postgresql://...
    ///
    /// Comentarios con # y lineas vacias se ignoran.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseConnFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No existe el archivo de conexiones: {path}", path);
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

            // Formato esperado: "KEY -> descripcion: value"
            var arrow = line.IndexOf("->", StringComparison.Ordinal);
            if (arrow < 0)
            {
                throw new FormatException($"Linea {i + 1}: falta '->'. Formato esperado 'KEY -> descripcion: value'.");
            }
            var key = line.Substring(0, arrow).Trim();
            var rest = line.Substring(arrow + 2).Trim();
            var colon = rest.IndexOf(':');
            if (colon < 0)
            {
                throw new FormatException($"Linea {i + 1}: falta ':' despues de la descripcion.");
            }
            // rest[..colon] = descripcion (ignoramos), rest[colon+1..] = value
            var value = rest.Substring(colon + 1).Trim();
            result[key] = value;
        }
        // Validar que esten las 3 keys.
        foreach (var required in new[] { "ORIGEN", "DESTINO_PROD", "DESTINO_ENSAYO" })
        {
            if (!result.ContainsKey(required))
            {
                throw new FormatException($"Falta la clave '{required}' en {path}.");
            }
        }
        return result;
    }
}
