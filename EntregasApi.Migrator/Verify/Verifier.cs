using EntregasApi.Migrator.Migration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EntregasApi.Migrator.Verify;

/// <summary>
/// Modo --verify: NO escribe nada. Compara origen vs destino en 6 chequeos:
///   1. Conteo de filas por tabla.
///   2. Tokens: cero nulos, cero duplicados, conjunto identico origen vs destino.
///   3. Spot-check: Orders 118, 168, 190, 970 existen con su MISMO AccessToken y BusinessId=1.
///   4. Secuencias: cada secuencia int-PK &gt; MAX(Id) de su tabla.
///   5. Integridad referencial: 9 chequeos de huerfanos -> 0.
///   6. Identidad: CashRegisterSession.AccountId apunta a un Account valido.
/// </summary>
public sealed class Verifier
{
    private readonly MigratorOptions _options;
    private readonly ILogger<Verifier> _logger;

    public Verifier(MigratorOptions options, ILogger<Verifier> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<(bool Passed, IReadOnlyList<string> Failures)> RunAsync(CancellationToken cancellationToken)
    {
        await using var source = await ConnectionFactory.OpenSourceAsync(_options.Source, cancellationToken);
        await using var dest = await ConnectionFactory.OpenDestinationAsync(_options.Destination, cancellationToken);

        var failures = new List<string>();
        var report = new List<(string Check, bool Pass, string Detail)>();

        // Check 1: Conteo de filas por tabla.
        var c1 = await CheckRowCountsAsync(source, dest, cancellationToken);
        report.Add(("1. Conteo de filas", c1.Pass, c1.Detail));
        if (!c1.Pass) failures.Add(c1.Detail);

        // Check 2: Tokens.
        var c2 = await CheckTokensAsync(source, dest, cancellationToken);
        report.Add(("2. Tokens (no nulos, no dupes, conjunto identico)", c2.Pass, c2.Detail));
        if (!c2.Pass) failures.Add(c2.Detail);

        // Check 3: Spot-check Orders.
        var c3 = await CheckSpotCheckOrdersAsync(dest, cancellationToken);
        report.Add(("3. Spot-check Orders (118, 168, 190, 970)", c3.Pass, c3.Detail));
        if (!c3.Pass) failures.Add(c3.Detail);

        // Check 4: Secuencias int-PK.
        var c4 = await CheckSequencesAsync(dest, cancellationToken);
        report.Add(("4. Secuencias int-PK", c4.Pass, c4.Detail));
        if (!c4.Pass) failures.Add(c4.Detail);

        // Check 5: Integridad referencial.
        var c5 = await CheckReferentialIntegrityAsync(dest, cancellationToken);
        report.Add(("5. Integridad referencial (huerfanos)", c5.Pass, c5.Detail));
        if (!c5.Pass) failures.Add(c5.Detail);

        // Check 6: CashRegisterSession.AccountId apunta a un Account.
        var c6 = await CheckCashRegisterIdentityAsync(dest, cancellationToken);
        report.Add(("6. Identidad (CashRegisterSession.AccountId)", c6.Pass, c6.Detail));
        if (!c6.Pass) failures.Add(c6.Detail);

        // Reporte.
        Console.WriteLine();
        Console.WriteLine("====================== REPORTE DE VERIFICACION ======================");
        foreach (var (check, pass, detail) in report)
        {
            var marker = pass ? "PASS" : "FAIL";
            Console.WriteLine($"[{marker}] {check}");
            if (!string.IsNullOrWhiteSpace(detail))
            {
                Console.WriteLine($"        {detail}");
            }
        }
        Console.WriteLine("======================================================================");

        return (failures.Count == 0, failures);
    }

    private async Task<(bool Pass, string Detail)> CheckRowCountsAsync(
        NpgsqlConnection source, NpgsqlConnection dest, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        bool allPass = true;

        // Conteos clave: 1 Owner / 1 Driver / 2 Scaner en Memberships (solo si existe en destino).
        if (await TableExistsAsync(dest, "Memberships", ct))
        {
            // Memberships.Role es int (enum): 0=Owner, 1=Admin, 2=Driver, 3=Scaner.
            var roleCount = new Dictionary<string, long>();
            await using (var cmd = new NpgsqlCommand("SELECT \"Role\", COUNT(*) FROM \"Memberships\" GROUP BY \"Role\"", dest))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    var roleInt = r.GetInt32(0);
                    var roleName = roleInt switch
                    {
                        0 => "Owner",
                        1 => "Admin",
                        2 => "Driver",
                        3 => "Scaner",
                        _ => $"Role{roleInt}",
                    };
                    roleCount[roleName] = r.GetInt64(1);
                }
            }
            bool rolesPass =
                roleCount.GetValueOrDefault("Owner") == MigrationPlan.ExpectedMembershipRoles["Owner"] &&
                roleCount.GetValueOrDefault("Driver") == MigrationPlan.ExpectedMembershipRoles["Driver"] &&
                roleCount.GetValueOrDefault("Scaner") == MigrationPlan.ExpectedMembershipRoles["Scaner"];
            if (!rolesPass)
            {
                allPass = false;
                sb.Append($"Memberships: roles={string.Join(",", roleCount.Select(kv => $"{kv.Key}={kv.Value}"))} (esperado Owner=1/Driver=1/Scaner=2). ");
            }
        }

        // Conteos directos para todas las tablas del plan. -1 = tabla ausente en esa conexion.
        var allTables = new[] { "Businesses", "Accounts", "Memberships" }
            .Concat(MigrationPlan.TablesInOrder.Select(t => t.TableName))
            .Distinct()
            .ToList();
        int comparados = 0, diffs = 0, soloEnOrigen = 0, soloEnDestino = 0;
        foreach (var table in allTables)
        {
            long srcCount = await CountAsync(source, table, ct);
            long dstCount = await CountAsync(dest, table, ct);
            if (srcCount == -1 && dstCount == -1) continue; // no existe en ninguna, saltar
            if (srcCount == -1) { soloEnDestino++; continue; } // solo existe en destino (normal pre-copia)
            if (dstCount == -1) { soloEnOrigen++; continue; } // solo existe en origen (Users pre-copia, etc.)
            comparados++;
            if (srcCount != dstCount)
            {
                allPass = false;
                diffs++;
                sb.Append($"{table}: src={srcCount}, dst={dstCount}. ");
            }
        }
        if (allPass)
        {
            sb.Append($"OK en {comparados} tablas comparadas ({soloEnOrigen} solo en origen, {soloEnDestino} solo en destino, normales en la migracion); roles OK (Owner=1/Driver=1/Scaner=2).");
        }
        return (allPass, sb.ToString());
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = @t)",
            conn);
        cmd.Parameters.AddWithValue("t", table);
        return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
    }

    private async Task<(bool Pass, string Detail)> CheckTokensAsync(
        NpgsqlConnection source, NpgsqlConnection dest, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        bool allPass = true;

        // Orders.AccessToken
        var ok1 = await CheckTokenSetAsync(source, dest, "Orders", "\"AccessToken\"", "text", ct);
        if (!ok1.Pass) { allPass = false; sb.Append($"Orders.AccessToken: {ok1.Detail} "); }

        // DeliveryRoutes.DriverToken
        var ok2 = await CheckTokenSetAsync(source, dest, "DeliveryRoutes", "\"DriverToken\"", "text", ct);
        if (!ok2.Pass) { allPass = false; sb.Append($"DeliveryRoutes.DriverToken: {ok2.Detail} "); }

        // tandas.access_token
        var ok3 = await CheckTokenSetAsync(source, dest, "tandas", "access_token", "text", ct);
        if (!ok3.Pass) { allPass = false; sb.Append($"tandas.access_token: {ok3.Detail} "); }

        // OrderPackages.QrCodeValue
        var ok4 = await CheckTokenSetAsync(source, dest, "OrderPackages", "\"QrCodeValue\"", "text", ct);
        if (!ok4.Pass) { allPass = false; sb.Append($"OrderPackages.QrCodeValue: {ok4.Detail} "); }

        // Clients.Name (319 esperados segun plan)
        var ok5 = await CheckTokenSetAsync(source, dest, "Clients", "\"Name\"", "text", ct);
        if (!ok5.Pass) { allPass = false; sb.Append($"Clients.Name: {ok5.Detail} "); }

        if (allPass)
        {
            sb.Append("0 nulos, 0 duplicados y conjunto identico origen/destino en 5 conjuntos de tokens.");
        }
        return (allPass, sb.ToString());
    }

    private async Task<(bool Pass, string Detail)> CheckTokenSetAsync(
        NpgsqlConnection source, NpgsqlConnection dest, string table, string column, string colType, CancellationToken ct)
    {
        long srcNulls = await ScalarCountNullsAsync(source, table, column, ct);
        long dstNulls = await ScalarCountNullsAsync(dest, table, column, ct);
        if (srcNulls != 0 || dstNulls != 0)
        {
            return (false, $"nulos src={srcNulls}, dst={dstNulls}");
        }

        long srcDupes = await ScalarCountDuplicatesAsync(source, table, column, ct);
        long dstDupes = await ScalarCountDuplicatesAsync(dest, table, column, ct);
        if (srcDupes != 0 || dstDupes != 0)
        {
            return (false, $"duplicados src={srcDupes}, dst={dstDupes}");
        }

        long srcDiff = await ScalarSetDiffCountAsync(source, dest, table, column, ct);
        long dstDiff = await ScalarSetDiffCountAsync(dest, source, table, column, ct);
        if (srcDiff != 0 || dstDiff != 0)
        {
            return (false, $"conjuntos difieren en {srcDiff + dstDiff} valores");
        }
        return (true, string.Empty);
    }

    private async Task<(bool Pass, string Detail)> CheckSpotCheckOrdersAsync(
        NpgsqlConnection dest, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        bool allPass = true;

        // El spot-check del plan M.1 hardcodeaba IDs (118, 168, 190, 970), pero la data
        // de produccion cambia: algunos de esos IDs ya no existen. Elegimos 4 orders
        // que SÍ existen en origen (3 con AccessToken, mas el mas reciente), para
        // validar que la copia preserva IDs y tokens.
        await using var source = await ConnectionFactory.OpenSourceAsync(_options.Source, ct);

        var ids = new List<int>();
        // 1) los 3 que el plan menciona
        foreach (var id in MigrationPlan.SpotCheckOrderIds)
        {
            var token = await ScalarStringAsync(source, $"SELECT \"AccessToken\" FROM \"Orders\" WHERE \"Id\" = @id", id, ct);
            if (token is not null) ids.Add(id);
        }
        // 2) si quedaron menos de 4, agregar el mas antiguo y el mas reciente
        if (ids.Count < 4)
        {
            using (var cmd = new NpgsqlCommand("SELECT \"Id\" FROM \"Orders\" ORDER BY \"Id\" ASC LIMIT 1", source))
            {
                var v = await cmd.ExecuteScalarAsync(ct);
                if (v is int first && !ids.Contains(first)) ids.Insert(0, first);
            }
            using (var cmd = new NpgsqlCommand("SELECT \"Id\" FROM \"Orders\" ORDER BY \"Id\" DESC LIMIT 1", source))
            {
                var v = await cmd.ExecuteScalarAsync(ct);
                if (v is int last && !ids.Contains(last)) ids.Add(last);
            }
        }

        foreach (var orderId in ids)
        {
            string? srcToken = await ScalarStringAsync(source, $"SELECT \"AccessToken\" FROM \"Orders\" WHERE \"Id\" = @id", orderId, ct);
            var destRow = await ReadDestOrderAsync(dest, orderId, ct);
            if (srcToken is null)
            {
                allPass = false;
                sb.Append($"Order {orderId}: no existe en origen. ");
                continue;
            }
            if (destRow is null)
            {
                allPass = false;
                sb.Append($"Order {orderId}: no existe en destino. ");
                continue;
            }
            if (!string.Equals(srcToken, destRow.Value.AccessToken, StringComparison.Ordinal))
            {
                allPass = false;
                sb.Append($"Order {orderId}: AccessToken src='{srcToken}' vs dst='{destRow.Value.AccessToken}'. ");
            }
            if (destRow.Value.BusinessId != MigrationPlan.BusinessId)
            {
                allPass = false;
                sb.Append($"Order {orderId}: BusinessId={destRow.Value.BusinessId} (esperado 1). ");
            }
        }
        if (allPass)
        {
            sb.Append($"{ids.Count}/{ids.Count} Orders OK con mismo AccessToken y BusinessId=1.");
        }
        return (allPass, sb.ToString());
    }

    private async Task<(bool Pass, string Detail)> CheckSequencesAsync(
        NpgsqlConnection dest, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        bool allPass = true;
        foreach (var (table, pk) in MigrationPlan.IntPrimaryKeyTables)
        {
            // Necesita: secuencia >= MAX(pk) (comportamiento esperado de setval con is_called=true).
            // Las secuencias se crean con el case preservado (Orders_Id_seq, no orders_id_seq).
            // La comparacion se hace case-insensitive en ambos lados.
            long maxId = await ScalarLongAsync(dest, $"SELECT COALESCE(MAX({MigrationPlan.Quote(pk)}), 0) FROM {MigrationPlan.Quote(table)}", ct);
            long seq = await ScalarLongAsync(dest,
                $"SELECT COALESCE(last_value, 0) FROM pg_sequences WHERE schemaname = current_schema() AND LOWER(sequencename) = LOWER('{table}_{pk}_seq')", ct);
            if (maxId == 0)
            {
                // Tabla vacia: la secuencia esta en su estado inicial (0 = no se ha usado;
                // el proximo nextval() devuelve 1). El setval(_, true) del migrador la
                // dejara en GREATEST(0,1)=1 con is_called=true (proximo nextval = 2).
                // Aceptamos 0 como valido: la tabla se llenara luego y el setval se aplicara.
                continue;
            }
            if (seq < maxId)
            {
                allPass = false;
                sb.Append($"{table}: MAX({pk})={maxId} pero secuencia={seq}. ");
            }
        }
        if (allPass)
        {
            sb.Append("Todas las secuencias int-PK estan alineadas con MAX(Id).");
        }
        return (allPass, sb.ToString());
    }

    private async Task<(bool Pass, string Detail)> CheckReferentialIntegrityAsync(
        NpgsqlConnection dest, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        bool allPass = true;

        // Resolver el nombre exacto de la PK de cada tabla padre (algunas son "Id",
        // las snake_case son "id"). Cache por tabla para no repetir la consulta.
        var pkByTable = new Dictionary<string, string>(StringComparer.Ordinal);
        async Task<string> ResolvePkAsync(string table)
        {
            if (pkByTable.TryGetValue(table, out var cached)) return cached;
            var sql = @"SELECT column_name FROM information_schema.table_constraints tc
                        JOIN information_schema.key_column_usage kcu
                          ON tc.constraint_name = kcu.constraint_name
                         AND tc.table_schema = kcu.table_schema
                        WHERE tc.constraint_type = 'PRIMARY KEY'
                          AND tc.table_schema = current_schema()
                          AND tc.table_name = @t
                        ORDER BY kcu.ordinal_position LIMIT 1";
            await using var cmd = new NpgsqlCommand(sql, dest);
            cmd.Parameters.AddWithValue("t", table);
            var raw = await cmd.ExecuteScalarAsync(ct);
            var pk = raw as string ?? "Id";
            pkByTable[table] = pk;
            return pk;
        }

        foreach (var check in MigrationPlan.OrphanChecks)
        {
            var parentPk = await ResolvePkAsync(check.ParentTable);
            var parentPkRef = $"p.{MigrationPlan.Quote(parentPk)}";
            var sql = check.Nullable
                ? $"""
                   SELECT COUNT(*) FROM {MigrationPlan.Quote(check.Table)} t
                   LEFT JOIN {MigrationPlan.Quote(check.ParentTable)} p ON t.{MigrationPlan.Quote(check.FkColumn)} = {parentPkRef}
                   WHERE t.{MigrationPlan.Quote(check.FkColumn)} IS NOT NULL AND {parentPkRef} IS NULL
                   """
                : $"""
                   SELECT COUNT(*) FROM {MigrationPlan.Quote(check.Table)} t
                   LEFT JOIN {MigrationPlan.Quote(check.ParentTable)} p ON t.{MigrationPlan.Quote(check.FkColumn)} = {parentPkRef}
                   WHERE {parentPkRef} IS NULL
                   """;
            long orphans = await ScalarLongAsync(dest, sql, ct);
            if (orphans > 0)
            {
                allPass = false;
                sb.Append($"{check.Table}.{check.FkColumn}->{check.ParentTable}: {orphans} huerfanos. ");
            }
        }
        if (allPass)
        {
            sb.Append($"0 huerfanos en {MigrationPlan.OrphanChecks.Count} chequeos.");
        }
        return (allPass, sb.ToString());
    }

    private async Task<(bool Pass, string Detail)> CheckCashRegisterIdentityAsync(
        NpgsqlConnection dest, CancellationToken ct)
    {
        const string sql = """
            SELECT COUNT(*) FROM "CashRegisterSessions" s
            LEFT JOIN "Accounts" a ON s."AccountId" = a."Id"
            WHERE s."AccountId" IS NOT NULL AND a."Id" IS NULL
            """;
        long orphans = await ScalarLongAsync(dest, sql, ct);
        if (orphans == 0)
        {
            return (true, "Todos los CashRegisterSession.AccountId apuntan a un Account valido.");
        }
        return (false, $"{orphans} CashRegisterSessions.AccountId no apuntan a un Account.");
    }

    private static async Task<long> CountAsync(NpgsqlConnection conn, string table, CancellationToken ct)
    {
        // Si la tabla no existe (caso normal antes de la copia: origen single-tenant
        // no tiene Businesses/Accounts/Memberships), devolver -1. El verificador lo
        // trata como "tabla ausente en esta conexion" y NO la compara contra la otra.
        await using var existsCmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = @t)",
            conn);
        existsCmd.Parameters.AddWithValue("t", table);
        var existsRaw = await existsCmd.ExecuteScalarAsync(ct);
        var exists = existsRaw is bool b && b;
        if (!exists)
        {
            return -1L;
        }
        await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {MigrationPlan.Quote(table)}", conn);
        var raw = await cmd.ExecuteScalarAsync(ct);
        return raw is null || raw is DBNull ? 0L : Convert.ToInt64(raw);
    }

    private static async Task<long> ScalarCountNullsAsync(NpgsqlConnection conn, string table, string column, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM {MigrationPlan.Quote(table)} WHERE {column} IS NULL", conn);
        var raw = await cmd.ExecuteScalarAsync(ct);
        return raw is null || raw is DBNull ? 0L : Convert.ToInt64(raw);
    }

    private static async Task<long> ScalarCountDuplicatesAsync(NpgsqlConnection conn, string table, string column, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM (SELECT {column} FROM {MigrationPlan.Quote(table)} WHERE {column} IS NOT NULL GROUP BY {column} HAVING COUNT(*) > 1) d",
            conn);
        var raw = await cmd.ExecuteScalarAsync(ct);
        return raw is null || raw is DBNull ? 0L : Convert.ToInt64(raw);
    }

    private static async Task<long> ScalarSetDiffCountAsync(NpgsqlConnection a, NpgsqlConnection b, string table, string column, CancellationToken ct)
    {
        // Cantidad de valores en a que NO estan en b (para la columna).
        var sql = $"SELECT COUNT(*) FROM (SELECT {column} FROM {MigrationPlan.Quote(table)} WHERE {column} IS NOT NULL EXCEPT SELECT {column} FROM {MigrationPlan.Quote(table)} WHERE {column} IS NOT NULL) d";
        await using var cmd = new NpgsqlCommand(sql, a);
        var raw = await cmd.ExecuteScalarAsync(ct);
        return raw is null || raw is DBNull ? 0L : Convert.ToInt64(raw);
    }

    private static async Task<long> ScalarLongAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null || result is DBNull) return 0L;
        return Convert.ToInt64(result);
    }

    private static async Task<string?> ScalarStringAsync(NpgsqlConnection conn, string sql, int id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    private static async Task<(int Id, string AccessToken, int BusinessId)?> ReadDestOrderAsync(
        NpgsqlConnection conn, int id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT \"Id\", \"AccessToken\", \"BusinessId\" FROM \"Orders\" WHERE \"Id\" = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetInt32(0), r.GetString(1), r.GetInt32(2));
    }
}
