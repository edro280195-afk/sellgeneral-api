using Npgsql;

namespace EntregasApi.Migrator.Migration;

/// <summary>
/// Convierte un connection string en formato URI (`postgresql://user:pass@host:port/db?...`)
/// al formato clave-valor de Npgsql (`Host=...;Database=...;...`). Acepta ambos formatos
/// de entrada (si ya viene en formato Npgsql, lo pasa tal cual).
/// </summary>
public static class ConnectionStringParser
{
    public static string ToNpgsql(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException("Connection string vacia.", nameof(raw));
        }

        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            // Ya esta en formato Npgsql o libre.
            return trimmed;
        }

        // Parseo URI tolerante (no usamos Uri porque las connection strings reales
        // no siempre son URIs bien formadas: passwords pueden contener ':' o '@').
        var noScheme = trimmed.Substring(trimmed.IndexOf("://", StringComparison.Ordinal) + 3);
        var atIdx = noScheme.LastIndexOf('@');
        if (atIdx < 0)
        {
            throw new ArgumentException("URI sin '@' (falta host).");
        }
        var userInfo = noScheme.Substring(0, atIdx);
        var hostAndDb = noScheme.Substring(atIdx + 1);

        var colonInUser = userInfo.IndexOf(':');
        var username = colonInUser >= 0 ? userInfo.Substring(0, colonInUser) : userInfo;
        var password = colonInUser >= 0 ? userInfo.Substring(colonInUser + 1) : string.Empty;

        var qIdx = hostAndDb.IndexOf('?');
        var hostAndDbNoQuery = qIdx >= 0 ? hostAndDb.Substring(0, qIdx) : hostAndDb;
        var query = qIdx >= 0 ? hostAndDb.Substring(qIdx + 1) : string.Empty;

        var slashIdx = hostAndDbNoQuery.IndexOf('/');
        var hostPort = slashIdx >= 0 ? hostAndDbNoQuery.Substring(0, slashIdx) : hostAndDbNoQuery;
        var database = slashIdx >= 0 ? hostAndDbNoQuery.Substring(slashIdx + 1) : string.Empty;
        if (string.IsNullOrEmpty(database))
        {
            database = "neondb";
        }

        // Parsear query string a dict (sin UrlDecode agresivo, los valores simples no lo necesitan).
        var queryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = pair.Substring(0, eq);
            var val = pair.Substring(eq + 1);
            queryParams[key] = val;
        }

        var csb = new NpgsqlConnectionStringBuilder
        {
            Host = hostPort.Contains(':') ? hostPort.Substring(0, hostPort.IndexOf(':')) : hostPort,
            Port = hostPort.Contains(':') && int.TryParse(hostPort[(hostPort.IndexOf(':') + 1)..], out var p) ? p : 5432,
            Database = database,
            Username = username,
            Password = password,
        };
        // Mapear query -> Npgsql conocido
        if (queryParams.TryGetValue("sslmode", out var sslmode))
        {
            csb.SslMode = sslmode.ToLowerInvariant() switch
            {
                "require" or "preferred" => SslMode.Require,
                "verify-ca" => SslMode.VerifyCA,
                "verify-full" => SslMode.VerifyFull,
                "disable" => SslMode.Disable,
                _ => SslMode.Require,
            };
            // Neon exige Trust en la mayoria de clientes; no lo asumimos, el cliente lo manda.
        }
        if (queryParams.TryGetValue("channel_binding", out var cb))
        {
            // Npgsql no tiene un equivalente directo; lo registramos como ApplicationName o lo dejamos pasar.
            // No es critico para la migracion.
        }
        if (queryParams.TryGetValue("options", out var opt))
        {
            csb.Options = opt;
        }
        return csb.ConnectionString;
    }

    /// <summary>
    /// Devuelve un sufijo del host (los ultimos 12 chars) que sirve para identificar la
    /// conexion en logs SIN exponer la URL completa (util cuando hay passwords en la URI).
    /// </summary>
    public static string HostTag(string connectionString)
    {
        try
        {
            var csb = new NpgsqlConnectionStringBuilder(connectionString);
            var host = csb.Host ?? "?";
            return host.Length <= 12 ? host : host.Substring(host.Length - 12);
        }
        catch
        {
            return "(?)";
        }
    }
}
