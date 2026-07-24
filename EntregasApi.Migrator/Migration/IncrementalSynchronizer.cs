using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace EntregasApi.Migrator.Migration;

/// <summary>
/// Sincroniza la base legacy de Regi Bazar con el tenant 1 que ya vive en
/// SellGeneral. Es aditiva: inserta faltantes y actualiza columnas que también
/// existen en el origen, pero nunca borra registros del destino ni toca otro
/// negocio. El origen siempre se abre en modo de solo lectura.
/// </summary>
public sealed class IncrementalSynchronizer
{
    private const int BusinessId = MigrationPlan.BusinessId;
    private const int BatchSize = 100;

    private static readonly IReadOnlyList<SyncTable> TablesInDependencyOrder = new[]
    {
        new SyncTable("AppSettings"),
        new SyncTable("Clients"),
        new SyncTable("DeliveryRoutes"),
        new SyncTable("Suppliers"),
        new SyncTable("LoyaltyRewards"),
        new SyncTable("FcmTokens"),
        new SyncTable("PushSubscriptions"),
        new SyncTable("Products"),
        new SyncTable("products"),
        new SyncTable("tandas"),
        new SyncTable("tanda_participants"),
        new SyncTable("payments"),
        new SyncTable("raffles"),
        new SyncTable("raffle_participants"),
        new SyncTable("raffle_draws"),
        new SyncTable("LoyaltyTransactions"),
        new SyncTable("SalesPeriods"),
        new SyncTable("Investments"),
        new SyncTable("DriverExpenses"),
        new SyncTable("Orders"),
        new SyncTable("OrderItems"),
        new SyncTable("OrderPayments"),
        new SyncTable("OrderPackages"),
        new SyncTable("Deliveries"),
        new SyncTable("DeliveryEvidences"),
        new SyncTable("ChatMessages"),
        new SyncTable("raffle_entries"),
        new SyncTable("ClientAliases"),
        new SyncTable("ClientMergeAudits"),
        new SyncTable("InventoryBoxes"),
        new SyncTable("InventoryItems"),
        new SyncTable("InventoryMovements"),
        new SyncTable("InventoryCountSessions"),
        new SyncTable("InventoryCountEntries"),
        new SyncTable("LabelAssets"),
        new SyncTable(
            "LabelTemplates",
            new[] { "PublishedVersionId" },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MediaSize"] = "PrinterProfile",
            }),
        new SyncTable("LabelTemplateVersions"),
    };

    private static readonly IReadOnlyList<string> LegacyOnlyTables = new[]
    {
        "LabelPrintEvents",
        "LiveSessions",
        "LiveProducts",
        "LiveSpokenOrders",
        "LiveCommentOrders",
        "LiveCandidates",
    };

    private readonly MigratorOptions _options;
    private readonly ILogger<IncrementalSynchronizer> _logger;

    public IncrementalSynchronizer(
        MigratorOptions options,
        ILogger<IncrementalSynchronizer> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        await using var source = await ConnectionFactory.OpenSourceAsync(_options.Source, cancellationToken);
        await using var destination = await ConnectionFactory.OpenDestinationAsync(_options.Destination, cancellationToken);
        await using var sourceTransaction = await source.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);

        var report = await BuildReportAsync(source, sourceTransaction, destination, cancellationToken);
        LogReport(report);
        if (report.HasCollisions)
        {
            _logger.LogError(
                "Se encontraron {Collisions} colisiones de PK con otro negocio. No se escribio nada.",
                report.CollisionCount);
            return false;
        }

        if (!_options.Apply)
        {
            _logger.LogInformation(
                "Reporte terminado. Para aplicar esta sincronizacion usa --sync --apply.");
            return true;
        }

        var backupPath = await CreateDestinationBackupAsync(destination, cancellationToken);
        _logger.LogInformation("Respaldo local creado en {BackupPath}", backupPath);

        await using var destinationTransaction = await destination.BeginTransactionAsync(cancellationToken);
        try
        {
            await EnsureLegacyArchiveAsync(destination, destinationTransaction, cancellationToken);

            var sourceUserIds = await SynchronizeUsersAsync(
                source,
                sourceTransaction,
                destination,
                destinationTransaction,
                cancellationToken);

            await ReconcileDeliveryIdConflictsAsync(
                source,
                sourceTransaction,
                destination,
                destinationTransaction,
                cancellationToken);

            foreach (var table in TablesInDependencyOrder)
            {
                if (table.Name == "LabelTemplates")
                {
                    await PrepareLabelTemplateDefaultsAsync(
                        source,
                        sourceTransaction,
                        destination,
                        destinationTransaction,
                        cancellationToken);
                }
                var rows = await SynchronizeTableAsync(
                    source,
                    sourceTransaction,
                    destination,
                    destinationTransaction,
                    table,
                    cancellationToken);
                _logger.LogInformation("{Table}: {Rows} filas sincronizadas", table.Name, rows);
            }

            var cashRows = await SynchronizeCashRegisterSessionsAsync(
                source,
                sourceTransaction,
                destination,
                destinationTransaction,
                sourceUserIds,
                cancellationToken);
            _logger.LogInformation("CashRegisterSessions: {Rows} filas sincronizadas", cashRows);

            await SynchronizeTemplatePublicationAsync(
                source,
                sourceTransaction,
                destination,
                destinationTransaction,
                cancellationToken);

            foreach (var table in LegacyOnlyTables)
            {
                var rows = await ArchiveLegacyTableAsync(
                    source,
                    sourceTransaction,
                    destination,
                    destinationTransaction,
                    table,
                    cancellationToken);
                _logger.LogInformation("Archivo historico {Table}: {Rows} filas conservadas", table, rows);
            }

            await ResetSequencesAsync(destination, destinationTransaction, cancellationToken);
            await destinationTransaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await destinationTransaction.RollbackAsync(cancellationToken);
            throw;
        }

        var verification = await BuildReportAsync(source, sourceTransaction, destination, cancellationToken);
        LogReport(verification, postSynchronization: true);
        return !verification.HasCollisions && verification.MissingCount == 0;
    }

    private async Task<SyncReport> BuildReportAsync(
        NpgsqlConnection source,
        NpgsqlTransaction sourceTransaction,
        NpgsqlConnection destination,
        CancellationToken cancellationToken)
    {
        var tableReports = new List<TableReport>();
        foreach (var table in TablesInDependencyOrder.Append(new SyncTable("CashRegisterSessions")))
        {
            var sourcePrimaryKey = await GetPrimaryKeyAsync(source, sourceTransaction, table.Name, cancellationToken);
            var destinationPrimaryKey = await GetPrimaryKeyAsync(destination, null, table.Name, cancellationToken);
            EnsureSingleMatchingPrimaryKey(table.Name, sourcePrimaryKey, destinationPrimaryKey);

            var destinationColumns = await GetColumnsAsync(destination, null, table.Name, cancellationToken);
            var sourceKeys = await ReadKeysAsync(source, sourceTransaction, table.Name, sourcePrimaryKey[0], null, cancellationToken);
            var destinationScope = BuildTenantScope(table.Name, destinationColumns, "t");
            var destinationKeys = await ReadKeysAsync(
                destination,
                null,
                table.Name,
                destinationPrimaryKey[0],
                destinationScope,
                cancellationToken);
            var destinationAllKeys = await ReadKeysAsync(
                destination,
                null,
                table.Name,
                destinationPrimaryKey[0],
                null,
                cancellationToken);

            var missing = sourceKeys.Except(destinationKeys, StringComparer.Ordinal).Count();
            var collisions = sourceKeys
                .Except(destinationKeys, StringComparer.Ordinal)
                .Intersect(destinationAllKeys, StringComparer.Ordinal)
                .Count();

            tableReports.Add(new TableReport(
                table.Name,
                sourceKeys.Count,
                destinationKeys.Count,
                missing,
                collisions));
        }

        var sourceUsers = await ReadKeysAsync(source, sourceTransaction, "Users", "Id", null, cancellationToken);
        var destinationMemberships = await ReadKeysAsync(
            destination,
            null,
            "Memberships",
            "AccountId",
            "t.\"BusinessId\" = @businessId",
            cancellationToken);
        tableReports.Add(new TableReport(
            "Users -> Accounts/Memberships",
            sourceUsers.Count,
            destinationMemberships.Count,
            sourceUsers.Except(destinationMemberships, StringComparer.Ordinal).Count(),
            0));

        return new SyncReport(tableReports);
    }

    private void LogReport(SyncReport report, bool postSynchronization = false)
    {
        _logger.LogInformation(
            "{Stage}: origen={SourceRows}, destino tenant 1={DestinationRows}, faltantes={Missing}, colisiones={Collisions}",
            postSynchronization ? "Verificacion posterior" : "Prechequeo",
            report.SourceRowCount,
            report.DestinationRowCount,
            report.MissingCount,
            report.CollisionCount);

        foreach (var table in report.Tables.Where(t => t.Missing > 0 || t.Collisions > 0))
        {
            _logger.LogInformation(
                "  {Table}: origen={Source}, destino={Destination}, faltantes={Missing}, colisiones={Collisions}",
                table.Table,
                table.SourceRows,
                table.DestinationRows,
                table.Missing,
                table.Collisions);
        }
    }

    private async Task<HashSet<int>> SynchronizeUsersAsync(
        NpgsqlConnection source,
        NpgsqlTransaction sourceTransaction,
        NpgsqlConnection destination,
        NpgsqlTransaction destinationTransaction,
        CancellationToken cancellationToken)
    {
        var users = new List<LegacyUser>();
        const string selectSql = """
            SELECT "Id", "Name", "Email", "PasswordHash", "CreatedAt", "Rol"
            FROM "Users"
            ORDER BY "Id"
            """;
        await using (var command = new NpgsqlCommand(selectSql, source, sourceTransaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                users.Add(new LegacyUser(
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? DateTime.UtcNow : reader.GetDateTime(4),
                    reader.IsDBNull(5) ? "Admin" : reader.GetString(5)));
            }
        }

        foreach (var user in users)
        {
            await AssertAccountCanBeSynchronizedAsync(destination, destinationTransaction, user, cancellationToken);

            var displayName = !string.IsNullOrWhiteSpace(user.Name)
                ? user.Name
                : !string.IsNullOrWhiteSpace(user.Email)
                    ? user.Email.Split('@')[0]
                    : $"Usuario {user.Id}";
            const string accountSql = """
                INSERT INTO "Accounts" ("Id", "DisplayName", "Email", "PasswordHash", "CreatedAt")
                VALUES (@id, @displayName, @email, @passwordHash, @createdAt)
                ON CONFLICT ("Id") DO UPDATE SET
                    "DisplayName" = EXCLUDED."DisplayName",
                    "Email" = EXCLUDED."Email",
                    "PasswordHash" = EXCLUDED."PasswordHash",
                    "CreatedAt" = EXCLUDED."CreatedAt"
                """;
            await using (var account = new NpgsqlCommand(accountSql, destination, destinationTransaction))
            {
                account.Parameters.AddWithValue("id", user.Id);
                account.Parameters.AddWithValue("displayName", displayName);
                account.Parameters.AddWithValue("email", (object?)user.Email ?? DBNull.Value);
                account.Parameters.AddWithValue("passwordHash", (object?)user.PasswordHash ?? DBNull.Value);
                account.Parameters.AddWithValue("createdAt", user.CreatedAt);
                await account.ExecuteNonQueryAsync(cancellationToken);
            }

            const string membershipSql = """
                INSERT INTO "Memberships" ("AccountId", "BusinessId", "Role", "CreatedAt")
                VALUES (@accountId, @businessId, @role, @createdAt)
                ON CONFLICT ("AccountId", "BusinessId") DO UPDATE SET
                    "Role" = EXCLUDED."Role"
                """;
            await using var membership = new NpgsqlCommand(membershipSql, destination, destinationTransaction);
            membership.Parameters.AddWithValue("accountId", user.Id);
            membership.Parameters.AddWithValue("businessId", BusinessId);
            membership.Parameters.AddWithValue("role", MapRole(user.Role));
            membership.Parameters.AddWithValue("createdAt", user.CreatedAt);
            await membership.ExecuteNonQueryAsync(cancellationToken);
        }

        _logger.LogInformation("Users -> Accounts/Memberships: {Count} accesos sincronizados", users.Count);
        return users.Select(user => user.Id).ToHashSet();
    }

    private static async Task AssertAccountCanBeSynchronizedAsync(
        NpgsqlConnection destination,
        NpgsqlTransaction transaction,
        LegacyUser user,
        CancellationToken cancellationToken)
    {
        const string byIdSql = """
            SELECT "Email"
            FROM "Accounts"
            WHERE "Id" = @id
            """;
        string? existingEmail = null;
        await using (var command = new NpgsqlCommand(byIdSql, destination, transaction))
        {
            command.Parameters.AddWithValue("id", user.Id);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            existingEmail = result is null or DBNull ? null : (string)result;
        }
        if (existingEmail is not null &&
            !string.Equals(existingEmail, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"La cuenta legacy {user.Id} coincide con otra cuenta en SellGeneral. Se aborto para no mezclar identidades.");
        }

        if (string.IsNullOrWhiteSpace(user.Email))
        {
            return;
        }

        const string byEmailSql = """
            SELECT "Id"
            FROM "Accounts"
            WHERE "Email" = @email
            """;
        await using var byEmail = new NpgsqlCommand(byEmailSql, destination, transaction);
        byEmail.Parameters.AddWithValue("email", user.Email);
        var existingId = await byEmail.ExecuteScalarAsync(cancellationToken);
        if (existingId is int id && id != user.Id)
        {
            throw new InvalidOperationException(
                "Una identidad legacy ya existe con otro Id en SellGeneral. Se aborto para no duplicar una cuenta.");
        }
    }

    private async Task<long> SynchronizeTableAsync(
        NpgsqlConnection source,
        NpgsqlTransaction sourceTransaction,
        NpgsqlConnection destination,
        NpgsqlTransaction destinationTransaction,
        SyncTable table,
        CancellationToken cancellationToken)
    {
        var sourceColumns = await GetColumnsAsync(source, sourceTransaction, table.Name, cancellationToken);
        var destinationColumns = await GetColumnsAsync(destination, destinationTransaction, table.Name, cancellationToken);
        var destinationColumnTypes = await GetColumnTypesAsync(destination, destinationTransaction, table.Name, cancellationToken);
        var sourcePrimaryKey = await GetPrimaryKeyAsync(source, sourceTransaction, table.Name, cancellationToken);
        var destinationPrimaryKey = await GetPrimaryKeyAsync(destination, destinationTransaction, table.Name, cancellationToken);
        EnsureSingleMatchingPrimaryKey(table.Name, sourcePrimaryKey, destinationPrimaryKey);

        var copiedColumns = sourceColumns
            .Where(sourceColumn => destinationColumns.Contains(sourceColumn) && !table.ExcludedColumns.Contains(sourceColumn))
            .Select(column => new ColumnMapping(column, column))
            .ToList();
        foreach (var (destinationColumn, sourceColumn) in table.SourceColumnByDestination)
        {
            copiedColumns.RemoveAll(column => string.Equals(column.Destination, destinationColumn, StringComparison.Ordinal));
            if (sourceColumns.Contains(sourceColumn) &&
                destinationColumns.Contains(destinationColumn) &&
                !table.ExcludedColumns.Contains(destinationColumn))
            {
                copiedColumns.Add(new ColumnMapping(sourceColumn, destinationColumn));
            }
        }

        if (!copiedColumns.Any(column => string.Equals(column.Destination, sourcePrimaryKey[0], StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"{table.Name}: la PK no se puede sincronizar.");
        }

        var tenantColumn = GetTenantColumn(destinationColumns);
        var insertColumns = copiedColumns.Select(column => column.Destination).ToList();
        if (tenantColumn is not null && !insertColumns.Contains(tenantColumn))
        {
            insertColumns.Add(tenantColumn);
        }

        var selectSql = $"SELECT {string.Join(", ", copiedColumns.Select(column => MigrationPlan.Quote(column.Source)))} FROM {MigrationPlan.Quote(table.Name)}";
        var upsertSql = BuildUpsertSql(
            table.Name,
            insertColumns,
            copiedColumns.Select(column => column.Destination).ToArray(),
            sourcePrimaryKey[0]);
        long rowCount = 0;
        var batch = new List<RowValues>(BatchSize);

        await using var sourceCommand = new NpgsqlCommand(selectSql, source, sourceTransaction);
        await using var reader = await sourceCommand.ExecuteReaderAsync(cancellationToken);
        var destinationTypeNames = copiedColumns
            .Select(column => destinationColumnTypes[column.Destination])
            .ToArray();
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new object?[reader.FieldCount];
            for (var columnIndex = 0; columnIndex < reader.FieldCount; columnIndex++)
            {
                values[columnIndex] = reader.IsDBNull(columnIndex) ? null : reader.GetValue(columnIndex);
            }
            batch.Add(new RowValues(values));
            if (batch.Count == BatchSize)
            {
                await UpsertBatchAsync(
                    destination,
                    destinationTransaction,
                    upsertSql,
                    batch,
                    copiedColumns.Count,
                    tenantColumn is not null,
                    destinationTypeNames,
                    cancellationToken);
                rowCount += batch.Count;
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await UpsertBatchAsync(
                destination,
                destinationTransaction,
                upsertSql,
                batch,
                copiedColumns.Count,
                tenantColumn is not null,
                destinationTypeNames,
                cancellationToken);
            rowCount += batch.Count;
        }
        return rowCount;
    }

    private static async Task UpsertBatchAsync(
        NpgsqlConnection destination,
        NpgsqlTransaction transaction,
        string upsertSql,
        IReadOnlyList<RowValues> rows,
        int sourceColumnCount,
        bool stampsTenant,
        IReadOnlyList<string> destinationTypeNames,
        CancellationToken cancellationToken)
    {
        var valuesSql = new List<string>(rows.Count);
        await using var command = new NpgsqlCommand { Connection = destination, Transaction = transaction };
        if (stampsTenant)
        {
            command.Parameters.AddWithValue("businessId", BusinessId);
        }

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var parameters = new List<string>(sourceColumnCount + (stampsTenant ? 1 : 0));
            for (var columnIndex = 0; columnIndex < sourceColumnCount; columnIndex++)
            {
                var parameterName = $"p_{rowIndex}_{columnIndex}";
                parameters.Add($"@{parameterName}");
                var parameter = CreateParameter(
                    parameterName,
                    destinationTypeNames[columnIndex],
                    rows[rowIndex].Values[columnIndex]);
                command.Parameters.Add(parameter);
            }
            if (stampsTenant)
            {
                parameters.Add("@businessId");
            }
            valuesSql.Add($"({string.Join(", ", parameters)})");
        }

        command.CommandText = upsertSql.Replace("{VALUES}", string.Join(", ", valuesSql), StringComparison.Ordinal);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildUpsertSql(
        string table,
        IReadOnlyList<string> insertColumns,
        IReadOnlyList<string> copiedColumns,
        string primaryKey)
    {
        var quotedInsertColumns = string.Join(", ", insertColumns.Select(MigrationPlan.Quote));
        var updateColumns = copiedColumns.Where(column => !string.Equals(column, primaryKey, StringComparison.Ordinal)).ToArray();
        var conflictAction = updateColumns.Length == 0
            ? "DO NOTHING"
            : "DO UPDATE SET " + string.Join(", ", updateColumns.Select(column =>
                $"{MigrationPlan.Quote(column)} = EXCLUDED.{MigrationPlan.Quote(column)}"));
        return $"INSERT INTO {MigrationPlan.Quote(table)} ({quotedInsertColumns}) VALUES {{VALUES}} " +
               $"ON CONFLICT ({MigrationPlan.Quote(primaryKey)}) {conflictAction}";
    }

    private async Task<long> SynchronizeCashRegisterSessionsAsync(
        NpgsqlConnection source,
        NpgsqlTransaction sourceTransaction,
        NpgsqlConnection destination,
        NpgsqlTransaction destinationTransaction,
        IReadOnlySet<int> sourceUserIds,
        CancellationToken cancellationToken)
    {
        const string selectSql = """
            SELECT "Id", "UserId", "OpeningTime", "ClosingTime", "InitialCash", "FinalCashExpected", "FinalCashActual", "Status"
            FROM "CashRegisterSessions"
            """;
        const string upsertSql = """
            INSERT INTO "CashRegisterSessions"
                ("Id", "AccountId", "BusinessId", "OpeningTime", "ClosingTime", "InitialCash", "FinalCashExpected", "FinalCashActual", "Status")
            VALUES
                (@id, @accountId, @businessId, @openingTime, @closingTime, @initialCash, @finalCashExpected, @finalCashActual, @status)
            ON CONFLICT ("Id") DO UPDATE SET
                "AccountId" = EXCLUDED."AccountId",
                "OpeningTime" = EXCLUDED."OpeningTime",
                "ClosingTime" = EXCLUDED."ClosingTime",
                "InitialCash" = EXCLUDED."InitialCash",
                "FinalCashExpected" = EXCLUDED."FinalCashExpected",
                "FinalCashActual" = EXCLUDED."FinalCashActual",
                "Status" = EXCLUDED."Status"
            """;
        long rows = 0;
        await using var sourceCommand = new NpgsqlCommand(selectSql, source, sourceTransaction);
        await using var reader = await sourceCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var userId = reader.GetInt32(1);
            await using var command = new NpgsqlCommand(upsertSql, destination, destinationTransaction);
            command.Parameters.AddWithValue("id", reader.GetInt32(0));
            command.Parameters.AddWithValue("accountId", sourceUserIds.Contains(userId) ? userId : DBNull.Value);
            command.Parameters.AddWithValue("businessId", BusinessId);
            command.Parameters.AddWithValue("openingTime", reader.GetDateTime(2));
            command.Parameters.AddWithValue("closingTime", reader.IsDBNull(3) ? DBNull.Value : reader.GetDateTime(3));
            command.Parameters.AddWithValue("initialCash", reader.GetDecimal(4));
            command.Parameters.AddWithValue("finalCashExpected", reader.GetDecimal(5));
            command.Parameters.AddWithValue("finalCashActual", reader.IsDBNull(6) ? DBNull.Value : reader.GetDecimal(6));
            command.Parameters.AddWithValue("status", reader.GetInt32(7));
            await command.ExecuteNonQueryAsync(cancellationToken);
            rows++;
        }
        return rows;
    }

    private static async Task SynchronizeTemplatePublicationAsync(
        NpgsqlConnection source,
        NpgsqlTransaction sourceTransaction,
        NpgsqlConnection destination,
        NpgsqlTransaction destinationTransaction,
        CancellationToken cancellationToken)
    {
        const string selectSql = "SELECT \"Id\", \"PublishedVersionId\" FROM \"LabelTemplates\"";
        const string updateSql = """
            UPDATE "LabelTemplates"
            SET "PublishedVersionId" = @publishedVersionId
            WHERE "Id" = @id AND "BusinessId" = @businessId
            """;
        await using var sourceCommand = new NpgsqlCommand(selectSql, source, sourceTransaction);
        await using var reader = await sourceCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            await using var update = new NpgsqlCommand(updateSql, destination, destinationTransaction);
            update.Parameters.AddWithValue("id", reader.GetGuid(0));
            update.Parameters.AddWithValue("publishedVersionId", reader.IsDBNull(1) ? DBNull.Value : reader.GetGuid(1));
            update.Parameters.AddWithValue("businessId", BusinessId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task PrepareLabelTemplateDefaultsAsync(
        NpgsqlConnection source,
        NpgsqlTransaction sourceTransaction,
        NpgsqlConnection destination,
        NpgsqlTransaction destinationTransaction,
        CancellationToken cancellationToken)
    {
        const string selectSql = """
            SELECT "Id", "Kind", "PrinterProfile"
            FROM "LabelTemplates"
            WHERE "IsDefault" = TRUE AND "IsArchived" = FALSE
            """;
        const string updateSql = """
            UPDATE "LabelTemplates"
            SET "IsDefault" = FALSE
            WHERE "BusinessId" = @businessId
              AND "Kind" = @kind
              AND "MediaSize" = @mediaSize
              AND "IsDefault" = TRUE
              AND "Id" <> @sourceId
            """;
        await using var sourceCommand = new NpgsqlCommand(selectSql, source, sourceTransaction);
        await using var reader = await sourceCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            await using var update = new NpgsqlCommand(updateSql, destination, destinationTransaction);
            update.Parameters.AddWithValue("businessId", BusinessId);
            update.Parameters.AddWithValue("kind", reader.GetInt32(1));
            update.Parameters.AddWithValue("mediaSize", reader.GetInt32(2));
            update.Parameters.AddWithValue("sourceId", reader.GetGuid(0));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task ReconcileDeliveryIdConflictsAsync(
        NpgsqlConnection source,
        NpgsqlTransaction sourceTransaction,
        NpgsqlConnection destination,
        NpgsqlTransaction destinationTransaction,
        CancellationToken cancellationToken)
    {
        var sourceByOrder = new Dictionary<int, int>();
        const string sourceSql = """
            SELECT "Id", "OrderId"
            FROM "Deliveries"
            WHERE "OrderId" IS NOT NULL
            """;
        await using (var command = new NpgsqlCommand(sourceSql, source, sourceTransaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                sourceByOrder[reader.GetInt32(1)] = reader.GetInt32(0);
            }
        }

        var destinationIdsToReplace = new List<int>();
        const string destinationSql = """
            SELECT "Id", "OrderId"
            FROM "Deliveries"
            WHERE "BusinessId" = @businessId AND "OrderId" IS NOT NULL
            """;
        await using (var command = new NpgsqlCommand(destinationSql, destination, destinationTransaction))
        {
            command.Parameters.AddWithValue("businessId", BusinessId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var destinationId = reader.GetInt32(0);
                var orderId = reader.GetInt32(1);
                if (sourceByOrder.TryGetValue(orderId, out var sourceId) && sourceId != destinationId)
                {
                    destinationIdsToReplace.Add(destinationId);
                }
            }
        }

        if (destinationIdsToReplace.Count == 0)
        {
            return;
        }

        var idParameter = new NpgsqlParameter("deliveryIds", NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = destinationIdsToReplace.ToArray(),
        };
        const string deleteEvidenceSql = """
            DELETE FROM "DeliveryEvidences"
            WHERE "BusinessId" = @businessId AND "DeliveryId" = ANY(@deliveryIds)
            """;
        await using (var deleteEvidence = new NpgsqlCommand(deleteEvidenceSql, destination, destinationTransaction))
        {
            deleteEvidence.Parameters.AddWithValue("businessId", BusinessId);
            deleteEvidence.Parameters.Add(idParameter);
            await deleteEvidence.ExecuteNonQueryAsync(cancellationToken);
        }

        const string deleteMessagesSql = "DELETE FROM \"ChatMessages\" WHERE \"DeliveryId\" = ANY(@deliveryIds)";
        await using (var deleteMessages = new NpgsqlCommand(deleteMessagesSql, destination, destinationTransaction))
        {
            deleteMessages.Parameters.Add(new NpgsqlParameter("deliveryIds", NpgsqlDbType.Array | NpgsqlDbType.Integer)
            {
                Value = destinationIdsToReplace.ToArray(),
            });
            await deleteMessages.ExecuteNonQueryAsync(cancellationToken);
        }

        const string deleteDeliveriesSql = """
            DELETE FROM "Deliveries"
            WHERE "BusinessId" = @businessId AND "Id" = ANY(@deliveryIds)
            """;
        await using (var deleteDeliveries = new NpgsqlCommand(deleteDeliveriesSql, destination, destinationTransaction))
        {
            deleteDeliveries.Parameters.AddWithValue("businessId", BusinessId);
            deleteDeliveries.Parameters.Add(new NpgsqlParameter("deliveryIds", NpgsqlDbType.Array | NpgsqlDbType.Integer)
            {
                Value = destinationIdsToReplace.ToArray(),
            });
            await deleteDeliveries.ExecuteNonQueryAsync(cancellationToken);
        }

        _logger.LogInformation(
            "Entregas: {Count} relaciones previas se reemplazaran por los IDs reales del origen",
            destinationIdsToReplace.Count);
    }

    private static async Task EnsureLegacyArchiveAsync(
        NpgsqlConnection destination,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS "LegacyRegiBazarArchives" (
                "BusinessId" integer NOT NULL REFERENCES "Businesses"("Id") ON DELETE RESTRICT,
                "SourceTable" text NOT NULL,
                "SourceId" text NOT NULL,
                "Payload" jsonb NOT NULL,
                "ImportedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
                CONSTRAINT "PK_LegacyRegiBazarArchives" PRIMARY KEY ("BusinessId", "SourceTable", "SourceId")
            );
            CREATE INDEX IF NOT EXISTS "IX_LegacyRegiBazarArchives_Business_SourceTable"
                ON "LegacyRegiBazarArchives" ("BusinessId", "SourceTable");
            """;
        await using var command = new NpgsqlCommand(sql, destination, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ArchiveLegacyTableAsync(
        NpgsqlConnection source,
        NpgsqlTransaction sourceTransaction,
        NpgsqlConnection destination,
        NpgsqlTransaction destinationTransaction,
        string table,
        CancellationToken cancellationToken)
    {
        var primaryKey = await GetPrimaryKeyAsync(source, sourceTransaction, table, cancellationToken);
        if (primaryKey.Count != 1)
        {
            throw new InvalidOperationException($"{table}: solo se admite una PK para el archivo historico.");
        }

        var selectSql = $"SELECT {MigrationPlan.Quote(primaryKey[0])}, row_to_json(t)::text FROM {MigrationPlan.Quote(table)} t";
        const string insertSql = """
            INSERT INTO "LegacyRegiBazarArchives" ("BusinessId", "SourceTable", "SourceId", "Payload")
            VALUES (@businessId, @sourceTable, @sourceId, @payload)
            ON CONFLICT ("BusinessId", "SourceTable", "SourceId") DO UPDATE SET
                "Payload" = EXCLUDED."Payload",
                "ImportedAt" = NOW()
            """;
        long rows = 0;
        await using var sourceCommand = new NpgsqlCommand(selectSql, source, sourceTransaction);
        await using var reader = await sourceCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            await using var insert = new NpgsqlCommand(insertSql, destination, destinationTransaction);
            insert.Parameters.AddWithValue("businessId", BusinessId);
            insert.Parameters.AddWithValue("sourceTable", table);
            insert.Parameters.AddWithValue("sourceId", KeyValue(reader.GetValue(0)));
            insert.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb)
            {
                Value = reader.GetString(1),
            });
            await insert.ExecuteNonQueryAsync(cancellationToken);
            rows++;
        }
        return rows;
    }

    private async Task<string> CreateDestinationBackupAsync(
        NpgsqlConnection destination,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(_options.BackupDirectory);
        var backupPath = Path.Combine(root, $"sync-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(backupPath);

        var entries = new List<BackupEntry>();
        foreach (var table in TablesInDependencyOrder.Select(table => table.Name).Append("CashRegisterSessions"))
        {
            entries.Add(await ExportTableBackupAsync(destination, backupPath, table, cancellationToken));
        }
        entries.Add(await ExportAccountsBackupAsync(destination, backupPath, cancellationToken));
        entries.Add(await ExportTableBackupAsync(destination, backupPath, "Memberships", cancellationToken));
        if (await TableExistsAsync(destination, null, "LegacyRegiBazarArchives", cancellationToken))
        {
            entries.Add(await ExportTableBackupAsync(destination, backupPath, "LegacyRegiBazarArchives", cancellationToken));
        }

        var manifest = new
        {
            createdAtUtc = DateTime.UtcNow,
            businessId = BusinessId,
            purpose = "Respaldo previo a sincronizacion incremental Regi Bazar -> SellGeneral",
            tables = entries,
        };
        var manifestPath = Path.Combine(backupPath, "manifest.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        return backupPath;
    }

    private static async Task<BackupEntry> ExportAccountsBackupAsync(
        NpgsqlConnection destination,
        string backupPath,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT row_to_json(row_data)::text
            FROM (
                SELECT account.*
                FROM "Accounts" account
                INNER JOIN "Memberships" membership ON membership."AccountId" = account."Id"
                WHERE membership."BusinessId" = @businessId
            ) row_data
            """;
        return await WriteJsonLinesAsync(destination, backupPath, "Accounts", sql, cancellationToken);
    }

    private static async Task<BackupEntry> ExportTableBackupAsync(
        NpgsqlConnection destination,
        string backupPath,
        string table,
        CancellationToken cancellationToken)
    {
        var columns = await GetColumnsAsync(destination, null, table, cancellationToken);
        var scope = BuildTenantScope(table, columns, "t");
        var where = string.IsNullOrWhiteSpace(scope) ? string.Empty : $" WHERE {scope}";
        var sql = $"SELECT row_to_json(row_data)::text FROM (SELECT t.* FROM {MigrationPlan.Quote(table)} t{where}) row_data";
        return await WriteJsonLinesAsync(destination, backupPath, table, sql, cancellationToken);
    }

    private static async Task<BackupEntry> WriteJsonLinesAsync(
        NpgsqlConnection destination,
        string backupPath,
        string table,
        string sql,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(backupPath, $"{table}.jsonl");
        long rows = 0;
        await using var writer = new StreamWriter(path, append: false);
        await using var command = new NpgsqlCommand(sql, destination);
        command.Parameters.AddWithValue("businessId", BusinessId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            await writer.WriteLineAsync(reader.GetString(0));
            rows++;
        }
        return new BackupEntry(table, Path.GetFileName(path), rows);
    }

    private static async Task ResetSequencesAsync(
        NpgsqlConnection destination,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (var table in TablesInDependencyOrder.Select(table => table.Name).Append("CashRegisterSessions"))
        {
            var primaryKey = await GetPrimaryKeyAsync(destination, transaction, table, cancellationToken);
            if (primaryKey.Count != 1 ||
                !string.Equals(await GetColumnTypeAsync(destination, transaction, table, primaryKey[0], cancellationToken), "integer", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            const string sequenceSql = "SELECT pg_get_serial_sequence(@tableName, @columnName)";
            await using var sequenceCommand = new NpgsqlCommand(sequenceSql, destination, transaction);
            sequenceCommand.Parameters.AddWithValue("tableName", $"public.\"{table}\"");
            sequenceCommand.Parameters.AddWithValue("columnName", primaryKey[0]);
            var sequence = await sequenceCommand.ExecuteScalarAsync(cancellationToken) as string;
            if (string.IsNullOrWhiteSpace(sequence))
            {
                continue;
            }

            var resetSql = $"""
                SELECT setval(@sequence::regclass,
                    GREATEST(COALESCE((SELECT MAX({MigrationPlan.Quote(primaryKey[0])}) FROM {MigrationPlan.Quote(table)}), 1), 1),
                    true)
                """;
            await using var reset = new NpgsqlCommand(resetSql, destination, transaction);
            reset.Parameters.AddWithValue("sequence", sequence);
            await reset.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<HashSet<string>> ReadKeysAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string table,
        string primaryKey,
        string? scope,
        CancellationToken cancellationToken)
    {
        var where = string.IsNullOrWhiteSpace(scope) ? string.Empty : $" WHERE {scope}";
        var sql = $"SELECT t.{MigrationPlan.Quote(primaryKey)} FROM {MigrationPlan.Quote(table)} t{where}";
        var keys = new HashSet<string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        if (!string.IsNullOrWhiteSpace(scope))
        {
            command.Parameters.AddWithValue("businessId", BusinessId);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            keys.Add(KeyValue(reader.GetValue(0)));
        }
        return keys;
    }

    private static async Task<HashSet<string>> GetColumnsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table
            ORDER BY ordinal_position
            """;
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(0));
        }
        if (columns.Count == 0)
        {
            throw new InvalidOperationException($"La tabla {table} no existe.");
        }
        return columns;
    }

    private static async Task<IReadOnlyDictionary<string, string>> GetColumnTypesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT column_name,
                   CASE WHEN data_type = 'ARRAY' THEN udt_name ELSE data_type END
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table
            """;
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            types[reader.GetString(0)] = reader.GetString(1);
        }
        return types;
    }

    private static async Task<IReadOnlyList<string>> GetPrimaryKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT key_column_usage.column_name
            FROM information_schema.table_constraints table_constraints
            INNER JOIN information_schema.key_column_usage key_column_usage
                ON key_column_usage.constraint_name = table_constraints.constraint_name
                AND key_column_usage.table_schema = table_constraints.table_schema
            WHERE table_constraints.table_schema = 'public'
              AND table_constraints.table_name = @table
              AND table_constraints.constraint_type = 'PRIMARY KEY'
            ORDER BY key_column_usage.ordinal_position
            """;
        var columns = new List<string>();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(0));
        }
        return columns;
    }

    private static async Task<string?> GetColumnTypeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT data_type
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table AND column_name = @column
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM information_schema.tables
                WHERE table_schema = 'public' AND table_name = @table
            )
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("table", table);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static void EnsureSingleMatchingPrimaryKey(
        string table,
        IReadOnlyList<string> sourcePrimaryKey,
        IReadOnlyList<string> destinationPrimaryKey)
    {
        if (sourcePrimaryKey.Count != 1 || destinationPrimaryKey.Count != 1 ||
            !string.Equals(sourcePrimaryKey[0], destinationPrimaryKey[0], StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{table}: la PK del origen y destino no es compatible.");
        }
    }

    private static string? GetTenantColumn(IReadOnlySet<string> columns)
    {
        if (columns.Contains("BusinessId")) return "BusinessId";
        if (columns.Contains("business_id")) return "business_id";
        return null;
    }

    private static string? BuildTenantScope(string table, IReadOnlySet<string> columns, string alias)
    {
        var tenantColumn = GetTenantColumn(columns);
        if (tenantColumn is not null)
        {
            return $"{alias}.{MigrationPlan.Quote(tenantColumn)} = @businessId";
        }

        return table switch
        {
            "ChatMessages" => $"EXISTS (SELECT 1 FROM \"DeliveryRoutes\" route WHERE route.\"Id\" = {alias}.\"DeliveryRouteId\" AND route.\"BusinessId\" = @businessId)",
            "ClientMergeAudits" => $"EXISTS (SELECT 1 FROM \"Clients\" client WHERE client.\"Id\" = {alias}.\"SourceClientId\" AND client.\"BusinessId\" = @businessId)",
            "LegacyRegiBazarArchives" => $"{alias}.\"BusinessId\" = @businessId",
            _ => null,
        };
    }

    private static NpgsqlParameter CreateParameter(string name, string destinationTypeName, object? value)
    {
        var normalizedValue = NormalizeValue(value, destinationTypeName);
        var type = destinationTypeName.ToLowerInvariant() switch
        {
            "boolean" => NpgsqlDbType.Boolean,
            "smallint" => NpgsqlDbType.Smallint,
            "integer" => NpgsqlDbType.Integer,
            "bigint" => NpgsqlDbType.Bigint,
            "real" => NpgsqlDbType.Real,
            "double precision" => NpgsqlDbType.Double,
            "numeric" => NpgsqlDbType.Numeric,
            "uuid" => NpgsqlDbType.Uuid,
            "bytea" => NpgsqlDbType.Bytea,
            "date" => NpgsqlDbType.Date,
            "timestamp without time zone" => NpgsqlDbType.Timestamp,
            "timestamp with time zone" => NpgsqlDbType.TimestampTz,
            "json" => NpgsqlDbType.Json,
            "jsonb" => NpgsqlDbType.Jsonb,
            "text[]" => NpgsqlDbType.Array | NpgsqlDbType.Text,
            "integer[]" => NpgsqlDbType.Array | NpgsqlDbType.Integer,
            "_text" => NpgsqlDbType.Array | NpgsqlDbType.Text,
            "_int4" => NpgsqlDbType.Array | NpgsqlDbType.Integer,
            "_uuid" => NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            _ => InferNpgsqlType(normalizedValue),
        };
        return new NpgsqlParameter(name, type) { Value = normalizedValue ?? DBNull.Value };
    }

    private static object? NormalizeValue(object? value, string destinationTypeName)
    {
        if (value is null || value is DBNull || value is not string text)
        {
            return value;
        }

        var culture = CultureInfo.InvariantCulture;
        return destinationTypeName.ToLowerInvariant() switch
        {
            "smallint" => short.Parse(text, NumberStyles.Integer, culture),
            "integer" => int.Parse(text, NumberStyles.Integer, culture),
            "bigint" => long.Parse(text, NumberStyles.Integer, culture),
            "real" => float.Parse(text, NumberStyles.Float, culture),
            "double precision" => double.Parse(text, NumberStyles.Float, culture),
            "numeric" => decimal.Parse(text, NumberStyles.Number, culture),
            "boolean" => bool.Parse(text),
            "uuid" => Guid.Parse(text),
            "date" => DateOnly.Parse(text, culture),
            "timestamp without time zone" => DateTime.Parse(text, culture, DateTimeStyles.None),
            "timestamp with time zone" => DateTime.Parse(text, culture, DateTimeStyles.AdjustToUniversal),
            _ => value,
        };
    }

    private static NpgsqlDbType InferNpgsqlType(object? value) => value switch
    {
        bool => NpgsqlDbType.Boolean,
        short => NpgsqlDbType.Smallint,
        int => NpgsqlDbType.Integer,
        long => NpgsqlDbType.Bigint,
        float => NpgsqlDbType.Real,
        double => NpgsqlDbType.Double,
        decimal => NpgsqlDbType.Numeric,
        Guid => NpgsqlDbType.Uuid,
        byte[] => NpgsqlDbType.Bytea,
        DateOnly => NpgsqlDbType.Date,
        DateTime dateTime when dateTime.Kind == DateTimeKind.Utc => NpgsqlDbType.TimestampTz,
        DateTime => NpgsqlDbType.Timestamp,
        _ => NpgsqlDbType.Text,
    };

    private static string KeyValue(object value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static int MapRole(string role) => role.Trim().ToLowerInvariant() switch
    {
        "admin" or "owner" => 0,
        "driver" => 2,
        "scaner" => 3,
        _ => 0,
    };

    private sealed record SyncTable(
        string Name,
        IEnumerable<string>? Excluded = null,
        IReadOnlyDictionary<string, string>? SourceColumnMappings = null)
    {
        public IReadOnlySet<string> ExcludedColumns { get; } = (Excluded ?? Array.Empty<string>())
            .ToHashSet(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, string> SourceColumnByDestination { get; } =
            SourceColumnMappings ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private sealed record ColumnMapping(string Source, string Destination);

    private sealed record RowValues(object?[] Values);

    private sealed record LegacyUser(
        int Id,
        string? Name,
        string? Email,
        string? PasswordHash,
        DateTime CreatedAt,
        string Role);

    private sealed record TableReport(
        string Table,
        int SourceRows,
        int DestinationRows,
        int Missing,
        int Collisions);

    private sealed class SyncReport
    {
        public SyncReport(IReadOnlyList<TableReport> tables)
        {
            Tables = tables;
        }

        public IReadOnlyList<TableReport> Tables { get; }
        public int SourceRowCount => Tables.Sum(table => table.SourceRows);
        public int DestinationRowCount => Tables.Sum(table => table.DestinationRows);
        public int MissingCount => Tables.Sum(table => table.Missing);
        public int CollisionCount => Tables.Sum(table => table.Collisions);
        public bool HasCollisions => CollisionCount > 0;
    }

    private sealed record BackupEntry(string Table, string File, long Rows);
}
