using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EntregasApi.Migrator.Migration;

/// <summary>
/// Orquestador del modo COPIA. Realiza, en una sola transaccion sobre el destino:
///   1) Pre-chequeo: destino vacio (sin Business Id=1, sin Memberships, sin Orders, etc.).
///   2) BusinessSeeder: crear Business Id=1 (transformacion A).
///   3) UsersToAccountsMapper: Users -> Accounts + Memberships (transformacion B).
///   4) TableCopier: copia tabla por tabla segun <see cref="MigrationPlan.TablesInOrder"/>,
///      excluyendo "Businesses" y "Memberships" (ya pobladas) y "Users"/"AppSettings" (casos especiales).
///   5) Clients.AccountId = null explícito (transformacion E).
///   6) Remapeo de CashRegisterSession.UserId -> AccountId (transformacion C).
///   7) Reescritura de ImagePath en DeliveryEvidences con el evidence-map.
///   8) SequenceResetter: setval de cada int-PK al MAX.
///   9) Commit.
///
/// Si algo falla: ROLLBACK y el destino queda limpio.
/// </summary>
public sealed class Copier
{
    private readonly MigratorOptions _options;
    private readonly ILogger<Copier> _logger;
    private readonly TableCopier _tableCopier;
    private readonly BusinessSeeder _businessSeeder;
    private readonly UsersToAccountsMapper _usersMapper;
    private readonly SequenceResetter _sequenceResetter;

    public Copier(MigratorOptions options, ILogger<Copier> logger, ILoggerFactory loggerFactory)
    {
        _options = options;
        _logger = logger;
        _tableCopier = new TableCopier(loggerFactory.CreateLogger<TableCopier>());
        _businessSeeder = new BusinessSeeder();
        _usersMapper = new UsersToAccountsMapper(loggerFactory.CreateLogger<UsersToAccountsMapper>());
        _sequenceResetter = new SequenceResetter(loggerFactory.CreateLogger<SequenceResetter>());
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var tokenEncryptor = new TokenEncryptor();
        var encryptedToken = tokenEncryptor.Protect(_options.RbMpToken);
        if (string.IsNullOrEmpty(_options.RbMpToken))
        {
            _logger.LogWarning(
                "No se proporciono --rb-mp-token. Business.MercadoPagoAccessToken quedara NULL. " +
                "Si la vendedora cobra por MP a sus clientas, esto debe corregirse despues de migrar.");
        }

        var evidenceMap = LoadEvidenceMap(_options.EvidenceMapPath, _logger);

        await using var source = await ConnectionFactory.OpenSourceAsync(_options.Source, cancellationToken);
        await using var dest = await ConnectionFactory.OpenDestinationAsync(_options.Destination, cancellationToken);

        // Pre-chequeo: el destino NO debe tener un Business con Id=1.
        await AssertDestinationIsEmptyAsync(dest, cancellationToken);

        // Una sola transaccion: todo o nada.
        await using var destTx = await dest.BeginTransactionAsync(cancellationToken);

        try
        {
            // A) Business Id=1
            _logger.LogInformation("Creando Business Id={Id} (Regi Bazar)", MigrationPlan.BusinessId);
            await _businessSeeder.SeedAsync(
                dest, destTx, encryptedToken,
                tokenWasProvided: !string.IsNullOrEmpty(_options.RbMpToken),
                cancellationToken);

            // B) Users -> Accounts + Memberships
            _logger.LogInformation("Transformando Users (origen) -> Accounts + Memberships (destino)");
            var userIdToAccountId = await _usersMapper.MigrateAsync(source, dest, destTx, cancellationToken);

            // C) Remapear CashRegisterSession.UserId -> AccountId
            //     (despues de copiar la tabla, ver paso "PostCopy"). Aqui recalculamos el remapeo.

            // D) Copia directa tabla por tabla
            var skipDest = new HashSet<string>(StringComparer.Ordinal)
            {
                "Businesses",
                "Accounts",
                "Memberships",
                "AppSettings", // se regenera con la fila unica copiada literal a continuacion
            };
            // AppSettings se regenera especialmente: copiamos la fila unica de origen tal cual,
            // estamapando BusinessId=1. Si no existe, la creamos con defaults.
            await CopyAppSettingsAsync(source, dest, destTx, cancellationToken);

            foreach (var spec in MigrationPlan.TablesInOrder)
            {
                if (skipDest.Contains(spec.TableName))
                {
                    continue;
                }
                // Caso especial: CashRegisterSessions requiere transformar UserId (origen) -> AccountId (destino).
                if (spec.TableName == "CashRegisterSessions")
                {
                    await _tableCopier.CopyCashRegisterSessionsAsync(source, dest, destTx, userIdToAccountId, _logger, cancellationToken);
                    continue;
                }
                _logger.LogInformation(
                    "Copiando {Source} -> {Dest} (BusinessId -> {Bid})",
                    spec.SourceTableName, spec.TableName, spec.BusinessIdColumn ?? "(no aplica)");
                var rows = await _tableCopier.CopyDirectAsync(source, dest, destTx, spec, cancellationToken);
                _logger.LogInformation("  -> {Rows} filas copiadas", rows);
            }

            // E) Clients.AccountId = null (defensivo: la columna ya queda null porque no existia en origen,
            //    pero nos aseguramos).
            await using (var cmd = new NpgsqlCommand(
                "UPDATE \"Clients\" SET \"AccountId\" = NULL WHERE \"BusinessId\" = @bid",
                dest, destTx))
            {
                cmd.Parameters.AddWithValue("bid", MigrationPlan.BusinessId);
                var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
                if (affected > 0)
                {
                    _logger.LogInformation("Clients.AccountId = NULL en {Count} filas (reclamar perfil, E)", affected);
                }
            }

            // C) Remapear CashRegisterSession.UserId -> AccountId: ya se hizo dentro de
            //    CopyCashRegisterSessionsAsync (mapeo UserId origen -> AccountId destino, identidad).
            //    No hace falta el UPDATE posterior; se omite.

            // Reescritura de ImagePath en DeliveryEvidences con evidence-map
            if (evidenceMap.Count > 0)
            {
                _logger.LogInformation("Aplicando evidence-map a DeliveryEvidences ({Count} entradas)", evidenceMap.Count);
                var rewritten = await _tableCopier.RewriteEvidenceImagePathsAsync(dest, destTx, evidenceMap, _logger, cancellationToken);
                _logger.LogInformation("  -> {Count} ImagePath reescritos a Cloudinary", rewritten);
            }

            // Reset de secuencias int-PK
            await _sequenceResetter.ResetAllAsync(dest, destTx, cancellationToken);

            await destTx.CommitAsync(cancellationToken);
            _logger.LogInformation("Transaccion confirmada. Migracion completa.");
        }
        catch
        {
            try { await destTx.RollbackAsync(cancellationToken); }
            catch (Exception rollbackEx)
            {
                _logger.LogError(rollbackEx, "Fallo adicional durante ROLLBACK");
            }
            throw;
        }
    }

    private static async Task AssertDestinationIsEmptyAsync(NpgsqlConnection dest, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
              (SELECT COUNT(*) FROM "Businesses" WHERE "Id" = 1) AS business,
              (SELECT COUNT(*) FROM "Accounts") AS accounts,
              (SELECT COUNT(*) FROM "Memberships") AS memberships,
              (SELECT COUNT(*) FROM "Orders") AS orders,
              (SELECT COUNT(*) FROM "Clients") AS clients
            """;
        await using var cmd = new NpgsqlCommand(sql, dest);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("No se pudo leer el estado del destino.");
        }
        long business = reader.GetInt64(0);
        long accounts = reader.GetInt64(1);
        long memberships = reader.GetInt64(2);
        long orders = reader.GetInt64(3);
        long clients = reader.GetInt64(4);
        if (business > 0 || accounts > 0 || memberships > 0 || orders > 0 || clients > 0)
        {
            throw new InvalidOperationException(
                $"El destino NO esta vacio. Conteos: Business={business}, Accounts={accounts}, " +
                $"Memberships={memberships}, Orders={orders}, Clients={clients}. " +
                "Abortando para evitar doble corrida. Si la corrida anterior fallo, limpia manualmente.");
        }
    }

    private async Task CopyAppSettingsAsync(
        NpgsqlConnection source,
        NpgsqlConnection dest,
        NpgsqlTransaction destTx,
        CancellationToken cancellationToken)
    {
        // F) AppSettings: copia la fila unica (Id=1) y estamapale BusinessId=1.
        //    Si el origen no tiene la fila (raro), insertamos defaults.
        var srcRow = new List<(int Id, decimal Cost, int Hours)>();
        const string selSql = "SELECT \"Id\", \"DefaultShippingCost\", \"LinkExpirationHours\" FROM \"AppSettings\"";
        await using (var cmd = new NpgsqlCommand(selSql, source))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                srcRow.Add((reader.GetInt32(0), reader.GetDecimal(1), reader.GetInt32(2)));
            }
        }

        if (srcRow.Count == 0)
        {
            _logger.LogWarning("AppSettings: el origen no tiene la fila. Se insertan defaults (60/72).");
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO "AppSettings" ("Id", "BusinessId", "DefaultShippingCost", "LinkExpirationHours")
                VALUES (1, @bid, 60, 72)
                ON CONFLICT ("Id") DO NOTHING
                """, dest, destTx);
            cmd.Parameters.AddWithValue("bid", MigrationPlan.BusinessId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        if (srcRow.Count > 1)
        {
            _logger.LogWarning("AppSettings: el origen tiene {Count} filas (se esperaba 1). Se toma la primera.", srcRow.Count);
        }
        var row = srcRow[0];
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO "AppSettings" ("Id", "BusinessId", "DefaultShippingCost", "LinkExpirationHours")
            VALUES (@Id, @bid, @Cost, @Hours)
            ON CONFLICT ("Id") DO NOTHING
            """, dest, destTx))
        {
            cmd.Parameters.AddWithValue("Id", row.Id);
            cmd.Parameters.AddWithValue("bid", MigrationPlan.BusinessId);
            cmd.Parameters.AddWithValue("Cost", row.Cost);
            cmd.Parameters.AddWithValue("Hours", row.Hours);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        _logger.LogInformation("AppSettings copiado: Id={Id}, ShippingCost={Cost}, LinkHours={Hours}", row.Id, row.Cost, row.Hours);
    }

    private async Task RemapCashRegisterSessionUserIdAsync(
        NpgsqlConnection dest,
        NpgsqlTransaction destTx,
        IReadOnlyDictionary<int, int> userIdToAccountId,
        CancellationToken cancellationToken)
    {
        // La tabla origen tiene UserId. La tabla destino tiene AccountId (mismo int).
        // Como en el paso B ya creamos Account.Id = User.Id, el remapeo es identidad
        // para todos los UserId que existan en el diccionario. Para los que NO
        // existan (huérfanos), los dejamos NULL con un WARNING.
        const string selSql = "SELECT \"Id\", \"UserId\" FROM \"CashRegisterSessions\"";
        var orphans = new List<(int Id, int UserId)>();
        await using (var cmd = new NpgsqlCommand(selSql, dest, destTx))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt32(0);
                var userId = reader.GetInt32(1);
                if (!userIdToAccountId.ContainsKey(userId))
                {
                    orphans.Add((id, userId));
                }
            }
        }

        if (orphans.Count == 0)
        {
            _logger.LogInformation("CashRegisterSession.UserId: todas las FK se remapearon a AccountId valido (identidad).");
            return;
        }

        _logger.LogWarning(
            "CashRegisterSession: {Count} filas con UserId huerfano. Se ponen AccountId = NULL y se registran.",
            orphans.Count);
        foreach (var (id, userId) in orphans)
        {
            await using var upd = new NpgsqlCommand(
                "UPDATE \"CashRegisterSessions\" SET \"AccountId\" = NULL WHERE \"Id\" = @id",
                dest, destTx);
            upd.Parameters.AddWithValue("id", id);
            await upd.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogWarning("  CashRegisterSession[{Id}].AccountId = NULL (UserId={UserId} no existe en Users)", id, userId);
        }
    }

    private static IReadOnlyDictionary<int, string> LoadEvidenceMap(string? path, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new Dictionary<int, string>();
        }
        if (!File.Exists(path))
        {
            logger.LogWarning("--evidence-map apunta a {Path} que no existe. Se omite.", path);
            return new Dictionary<int, string>();
        }
        try
        {
            using var stream = File.OpenRead(path);
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                ?? throw new InvalidDataException("evidence-map vacio o mal formado");
            var result = new Dictionary<int, string>(raw.Count);
            foreach (var kv in raw)
            {
                if (!int.TryParse(kv.Key, out var id))
                {
                    logger.LogWarning("evidence-map: clave {Key} no es int. Se omite.", kv.Key);
                    continue;
                }
                result[id] = kv.Value;
            }
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo parsear --evidence-map. Se omite (se conservaran las rutas locales).");
            return new Dictionary<int, string>();
        }
    }
}
