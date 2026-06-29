using Npgsql;

namespace EntregasApi.Migrator.Migration;

/// <summary>
/// Abre conexiones Npgsql. El origen se marca como READ ONLY en la sesion
/// para reforzar el principio "PROHIBIDO escribir una sola fila en origen".
/// El destino abre transacciones de escritura.
/// </summary>
public static class ConnectionFactory
{
    /// <summary>
    /// Conexion al origen. Se ejecuta "SET TRANSACTION READ ONLY" justo despues
    /// de abrir para impedir cualquier intento de escritura.
    /// </summary>
    public static async Task<NpgsqlConnection> OpenSourceAsync(string connectionString, CancellationToken cancellationToken)
    {
        var csb = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = "EntregasApi.Migrator.Source",
        };
        var conn = new NpgsqlConnection(csb.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        // Bloqueo defensivo: la sesion no podra ejecutar INSERT/UPDATE/DELETE/COPY FROM.
        await using (var cmd = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var cmd = new NpgsqlCommand("SET default_transaction_read_only = on", conn))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        return conn;
    }

    /// <summary>Conexion al destino, en modo escritura normal.</summary>
    public static async Task<NpgsqlConnection> OpenDestinationAsync(string connectionString, CancellationToken cancellationToken)
    {
        var csb = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = "EntregasApi.Migrator.Destination",
        };
        var conn = new NpgsqlConnection(csb.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }
}
