using Microsoft.Extensions.Logging;
using Npgsql;

namespace EntregasApi.Migrator.Migration;

/// <summary>
/// Tras insertar PKs int explicitos en el destino, resetea cada secuencia int-PK
/// (columnas con default IdentityByDefault) al MAX(Id) de su tabla. Asi el proximo
/// INSERT normal de la aplicacion no colisiona.
///
/// Si la columna es GENERATED ALWAYS AS IDENTITY, los INSERTs anteriores ya requieren
/// OVERRIDING SYSTEM VALUE (Npgsql no lo envia por defecto, pero COPY con columna
/// explicita lo permite). De cualquier modo el setval aqui no es destructivo.
/// </summary>
public sealed class SequenceResetter
{
    private readonly ILogger<SequenceResetter> _logger;

    public SequenceResetter(ILogger<SequenceResetter> logger)
    {
        _logger = logger;
    }

    public async Task<int> ResetAllAsync(
        NpgsqlConnection dest,
        NpgsqlTransaction destTx,
        CancellationToken cancellationToken)
    {
        int touched = 0;
        foreach (var (table, pk) in MigrationPlan.IntPrimaryKeyTables)
        {
            // setval(pg_get_serial_sequence('"Tabla"','Col'), GREATEST(MAX(Col), 1))
            // Si la tabla esta vacia, GREATEST(MAX,1) protege de setval(0) que en PG
            // deja la secuencia devolviendo 1 en el proximo nextval (comportamiento valido).
            var sql = $"""
                DO $$
                DECLARE
                    seq text;
                    max_id integer;
                BEGIN
                    seq := pg_get_serial_sequence('{MigrationPlan.Quote(table)}', '{pk}');
                    IF seq IS NULL THEN
                        RETURN;
                    END IF;
                    EXECUTE format('SELECT COALESCE(MAX(%I), 0) FROM %I', '{pk}', '{table}')
                        INTO max_id;
                    EXECUTE format('SELECT setval(%L, GREATEST(%s, 1), true)', seq, max_id);
                END $$;
                """;
            await using var cmd = new NpgsqlCommand(sql, dest, destTx);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            touched++;
        }
        _logger.LogInformation("Secuencias reseteadas para {Count} tablas int-PK.", touched);
        return touched;
    }
}
