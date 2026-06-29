using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace EntregasApi.Migrator.Migration;

/// <summary>
/// Copia una tabla del origen al destino respetando PKs (int y Guid), estampando BusinessId
/// cuando aplique y dejando la columna nula cuando la fuente es null. Usa COPY ... FROM STDIN
/// para volumenes grandes, y un fallback INSERT parametrizado cuando se requiere transformar
/// valores fila a fila (e.g. ImagePath en DeliveryEvidences).
/// </summary>
public sealed class TableCopier
{
    private readonly ILogger<TableCopier> _logger;

    public TableCopier(ILogger<TableCopier> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Copia la tabla origen -> destino respetando PKs y estampando BusinessId.
    /// Devuelve la cantidad de filas copiadas.
    /// </summary>
    public async Task<long> CopyDirectAsync(
        NpgsqlConnection source,
        NpgsqlConnection dest,
        NpgsqlTransaction destTx,
        TableSpec spec,
        CancellationToken cancellationToken)
    {
        // 1) Listar columnas destino que existan en origen (interseccion).
        var destColumns = await GetTableColumnsAsync(dest, spec.TableName, cancellationToken);
        var sourceColumns = await GetTableColumnsAsync(source, spec.SourceTableName, cancellationToken);
        var intersection = destColumns
            .Where(c => sourceColumns.Contains(c) && !spec.DropColumns.Contains(c))
            .ToList();

        if (intersection.Count == 0)
        {
            _logger.LogWarning("Tabla {Table}: sin columnas en comun con el origen. Se omite.", spec.TableName);
            return 0;
        }

        // 2) selectColumns: las que se LEEN del origen. NO incluye BusinessId (no existe
        //    en el origen single-tenant); se estampa en el COPY al destino.
        var selectColumns = new List<string>(intersection);

        // 3) copyColumns: las que se INSERTAN en el destino. Incluye BusinessId si aplica.
        var copyColumns = new List<string>(intersection);
        var stampBusinessId = !string.IsNullOrEmpty(spec.BusinessIdColumn) && !copyColumns.Contains(spec.BusinessIdColumn);
        if (stampBusinessId)
        {
            copyColumns.Add(spec.BusinessIdColumn!);
        }

        // 4) Sentencia COPY: para binary import, el formato es fijo (binario), no acepta
        //    opciones como FORMAT csv / ENCODING. La convencion de NULL la maneja el importer.
        var quotedCopyCols = string.Join(", ", copyColumns.Select(MigrationPlan.Quote));
        var quotedSelectCols = string.Join(", ", selectColumns.Select(MigrationPlan.Quote));
        var copySql = $"COPY {MigrationPlan.Quote(spec.TableName)} ({quotedCopyCols}) FROM STDIN (FORMAT BINARY)";

        // 5) Sentencia SELECT origen: leer SOLO las columnas que existen en origen.
        var selectSql = $"SELECT {quotedSelectCols} FROM {MigrationPlan.Quote(spec.SourceTableName)}";

        long count;
        await using (var srcCmd = new NpgsqlCommand(selectSql, source))
        await using (var reader = await srcCmd.ExecuteReaderAsync(cancellationToken))
        await using (var writer = await dest.BeginBinaryImportAsync(copySql, cancellationToken))
        {
            // Capturar el tipo Postgres de cada columna origen para que el binary importer
            // use el formato correcto (date vs timestamptz vs text, etc.). Sin esto, siempre
            // mandamos timestamptz y choca con columnas date puras (tandas.start_date).
            var sourceTypeNames = new string[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++)
            {
                sourceTypeNames[i] = reader.GetDataTypeName(i);
            }
            count = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                await writer.StartRowAsync(cancellationToken);
                // Primero las columnas que vienen del reader (en el mismo orden que selectColumns).
                for (int i = 0; i < selectColumns.Count; i++)
                {
                    var column = selectColumns[i];
                    object? value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    await WriteValueAsync(writer, value, sourceTypeNames[i], cancellationToken);
                }
                // Luego, si hay que estampar BusinessId, agregarlo como constante.
                if (stampBusinessId)
                {
                    await WriteValueAsync(writer, MigrationPlan.BusinessId, "integer", cancellationToken);
                }
                count++;
            }
            await writer.CompleteAsync(cancellationToken);
        }

        return count;
    }

    /// <summary>
    /// Copia especial para CashRegisterSessions: en el origen la columna es "UserId",
    /// en el destino es "AccountId". Ademas, el destino tiene un FK AccountId -> Accounts.
    /// Como el Account.Id = User.Id (creado en UsersToAccountsMapper), el mapeo es
    /// identidad. Hacemos INSERT parametrizado en vez de COPY porque:
    ///   1) necesitamos renombrar la columna en vuelo (UserId -> AccountId).
    ///   2) hay que validar que el UserId existe en el diccionario (si no, NULL + WARN).
    /// </summary>
    public async Task<int> CopyCashRegisterSessionsAsync(
        NpgsqlConnection source,
        NpgsqlConnection dest,
        NpgsqlTransaction destTx,
        IReadOnlyDictionary<int, int> userIdToAccountId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        const string selectSql = "SELECT \"Id\", \"UserId\", \"OpeningTime\", \"ClosingTime\", \"InitialCash\", \"FinalCashExpected\", \"FinalCashActual\", \"Status\" FROM \"CashRegisterSessions\"";
        const string insertSql = """
            INSERT INTO "CashRegisterSessions"
                ("Id", "AccountId", "BusinessId", "OpeningTime", "ClosingTime", "InitialCash", "FinalCashExpected", "FinalCashActual", "Status")
            VALUES
                (@Id, @AccountId, @BusinessId, @OpeningTime, @ClosingTime, @InitialCash, @FinalCashExpected, @FinalCashActual, @Status)
            """;
        int count = 0, orphans = 0;
        await using (var cmd = new NpgsqlCommand(selectSql, source))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt32(0);
                var userId = reader.GetInt32(1);
                object? accountId = userIdToAccountId.TryGetValue(userId, out var acc) ? (object)acc : DBNull.Value;
                if (accountId is DBNull) orphans++;
                await using var ins = new NpgsqlCommand(insertSql, dest, destTx);
                ins.Parameters.AddWithValue("Id", id);
                ins.Parameters.AddWithValue("AccountId", accountId);
                ins.Parameters.AddWithValue("BusinessId", MigrationPlan.BusinessId);
                ins.Parameters.AddWithValue("OpeningTime", reader.GetDateTime(2));
                ins.Parameters.AddWithValue("ClosingTime", reader.IsDBNull(3) ? DBNull.Value : reader.GetDateTime(3));
                ins.Parameters.AddWithValue("InitialCash", reader.GetDecimal(4));
                ins.Parameters.AddWithValue("FinalCashExpected", reader.GetDecimal(5));
                ins.Parameters.AddWithValue("FinalCashActual", reader.IsDBNull(6) ? DBNull.Value : reader.GetDecimal(6));
                ins.Parameters.AddWithValue("Status", reader.GetInt32(7));
                await ins.ExecuteNonQueryAsync(cancellationToken);
                count++;
            }
        }
        if (orphans > 0)
        {
            logger.LogWarning("CashRegisterSessions: {Count} filas con UserId huerfano quedaron con AccountId=NULL.", orphans);
        }
        logger.LogInformation("Copiando CashRegisterSessions -> CashRegisterSessions (UserId->AccountId)");
        logger.LogInformation("  -> {Count} filas copiadas (con {Orphans} huerfanos NULL)", count, orphans);
        return count;
    }

    /// <summary>
    /// Caso especial: la columna ImagePath de DeliveryEvidences puede reescribirse via
    /// el mapa evidence-id -> url Cloudinary. Si no hay entrada, conserva la ruta local y registra WARN.
    /// </summary>
    public async Task<int> RewriteEvidenceImagePathsAsync(
        NpgsqlConnection dest,
        NpgsqlTransaction destTx,
        IReadOnlyDictionary<int, string> evidenceMap,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        const string table = "DeliveryEvidences";
        const string idCol = "Id";
        const string pathCol = "ImagePath";

        var rows = new List<(int Id, string CurrentPath)>();
        await using (var cmd = new NpgsqlCommand($"SELECT {idCol}, {pathCol} FROM {MigrationPlan.Quote(table)}", dest, destTx))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetInt32(0), reader.GetString(1)));
            }
        }

        int updated = 0;
        foreach (var (id, current) in rows)
        {
            if (!current.StartsWith("evidence/", StringComparison.OrdinalIgnoreCase))
            {
                continue; // ya es una URL Cloudinary u otro path; no tocamos.
            }

            if (evidenceMap.TryGetValue(id, out var cloudinaryUrl))
            {
                await using var upd = new NpgsqlCommand(
                    $"UPDATE {MigrationPlan.Quote(table)} SET {pathCol} = @p WHERE {idCol} = @id",
                    dest, destTx);
                upd.Parameters.AddWithValue("p", cloudinaryUrl);
                upd.Parameters.AddWithValue("id", id);
                await upd.ExecuteNonQueryAsync(cancellationToken);
                updated++;
            }
            else
            {
                logger.LogWarning(
                    "Evidence legacy sin mapeo: {Table}.{Id} = {Path} (se conserva la ruta local)",
                    table, id, current);
            }
        }
        return updated;
    }

    private static async Task WriteValueAsync(NpgsqlBinaryImporter writer, object? value, string sourceTypeName, CancellationToken cancellationToken)
    {
        if (value is null || value is DBNull)
        {
            await writer.WriteNullAsync(cancellationToken);
            return;
        }
        // sourceTypeName viene de NpgsqlDataReader.GetDataTypeName (e.g. "date",
        // "timestamp with time zone", "integer", "uuid", "text", "numeric", "boolean").
        // Esto es necesario para distinguir `date` (sin hora) de `timestamptz` (con hora),
        // y para no chocar al binary importer con un formato que no coincide con la
        // columna destino.
        var lower = sourceTypeName.ToLowerInvariant();
        switch (value)
        {
            case bool b:
                await writer.WriteAsync(b, NpgsqlDbType.Boolean, cancellationToken);
                break;
            case short s:
                await writer.WriteAsync(s, NpgsqlDbType.Smallint, cancellationToken);
                break;
            case int i:
                await writer.WriteAsync(i, NpgsqlDbType.Integer, cancellationToken);
                break;
            case long l:
                await writer.WriteAsync(l, NpgsqlDbType.Bigint, cancellationToken);
                break;
            case float f:
                await writer.WriteAsync(f, NpgsqlDbType.Real, cancellationToken);
                break;
            case double d:
                await writer.WriteAsync(d, NpgsqlDbType.Double, cancellationToken);
                break;
            case decimal dec:
                await writer.WriteAsync(dec, NpgsqlDbType.Numeric, cancellationToken);
                break;
            case DateTime dt when lower == "date":
                // Columna date pura (sin hora): usar NpgsqlDbType.Date, no DateTime.
                await writer.WriteAsync(dt.Date, NpgsqlDbType.Date, cancellationToken);
                break;
            case DateTime dt:
                // timestamp with time zone / timestamp without time zone / etc.
                await writer.WriteAsync(DateTime.SpecifyKind(dt, DateTimeKind.Utc), NpgsqlDbType.TimestampTz, cancellationToken);
                break;
            case Guid g:
                await writer.WriteAsync(g, NpgsqlDbType.Uuid, cancellationToken);
                break;
            case string str:
                await writer.WriteAsync(str, NpgsqlDbType.Text, cancellationToken);
                break;
            case byte[] bytes:
                await writer.WriteAsync(bytes, NpgsqlDbType.Bytea, cancellationToken);
                break;
            default:
                await writer.WriteAsync(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, NpgsqlDbType.Text, cancellationToken);
                break;
        }
    }

    private static async Task<HashSet<string>> GetTableColumnsAsync(NpgsqlConnection conn, string tableName, CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        const string sql = """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = current_schema() AND table_name = @t
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("t", tableName);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }
}
