namespace EntregasApi.Migrator;

/// <summary>
/// Opciones parseadas de la linea de comandos del migrador. Ver <see cref="HelpText"/>.
/// </summary>
public sealed class MigratorOptions
{
    /// <summary>Modo: solo comparar origen y destino SIN escribir nada.</summary>
    public bool Verify { get; init; }

    /// <summary>Modo: validar accesibilidad y estado de las bases SIN escribir nada.</summary>
    public bool Preflight { get; init; }

    /// <summary>Connection string del origen (single-tenant, READ ONLY en la sesion).</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Connection string del destino (multi-tenant, unica transaccion).</summary>
    public string Destination { get; init; } = string.Empty;

    /// <summary>Token MP de la vendedora (encriptado en el destino con el mismo protector que usa la app).</summary>
    public string? RbMpToken { get; init; }

    /// <summary>Ruta a un JSON opcional { "evidenceId": "urlCloudinary" } para reescribir ImagePath local.</summary>
    public string? EvidenceMapPath { get; init; }

    /// <summary>Archivo con 3 connection strings (ORIGEN, DESTINO_PROD, DESTINO_ENSAYO). Solo para --preflight.</summary>
    public string? ConnFile { get; init; }

    /// <summary>Log a nivel Debug.</summary>
    public bool Verbose { get; init; }

    public bool ShowHelp { get; init; }

    public static string HelpText => """
        EntregasApi.Migrator - Migrador one-shot de Regi Bazar (single-tenant) a Neni's App (multi-tenant).

        Uso:
          EntregasApi.Migrator --source <conn> --dest <conn> [opciones]

        Opciones obligatorias:
          --source <conn>        Connection string de la base VIEJA (single-tenant, NO escribir ahi).
          --dest   <conn>        Connection string de la base NUEVA (multi-tenant, ya migrada con EF).

        Opciones de modo:
          --verify               NO escribe. Compara conteos/tokens/IDs/secuencias/FKs origen vs destino.
          --preflight            NO escribe. Solo valida accesibilidad y estado de las 3 bases.
                                 Usa --conn-file (default: connectionStrings.txt) para leer ORIGEN,
                                 DESTINO_PROD y DESTINO_ENSAYO.

        Opciones de transformacion:
          --rb-mp-token <token>  Access token de MP de la vendedora. Se encripta con DataProtection
                                 (mismo protector que la app, aplicacion "EntregasApi"). Si se omite,
                                 el token queda NULL y se registra una advertencia.
          --evidence-map <path>  JSON { "evidenceId": "urlCloudinary" } para reescribir ImagePath
                                 de las 3 evidencias legacy con ruta local. Si falta una entrada,
                                 se conserva la ruta local y se registra una advertencia por fila.

        Opciones de salida:
          --verbose              Log a nivel Debug.
          -h | --help            Muestra esta ayuda.

        Ejemplos:
          # Solo verificar (no toca el destino):
          EntregasApi.Migrator --verify ^
              --source "Host=...neondb...;Database=neondb" ^
              --dest   "Host=...neondb...;Database=sellgeneral"

          # Copia real (una sola vez, en el corte):
          EntregasApi.Migrator ^
              --source "Host=...neondb...;Database=neondb" ^
              --dest   "Host=...neondb...;Database=sellgeneral" ^
              --rb-mp-token "APP_USR-..." ^
              --evidence-map "./evidence-map.json"
        """;

    public static MigratorOptions Parse(string[] args)
    {
        string? source = null;
        string? dest = null;
        string? rbMpToken = null;
        string? evidenceMap = null;
        string? connFile = null;
        bool verify = false;
        bool preflight = false;
        bool verbose = false;
        bool showHelp = false;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--source":
                    source = RequireValue(args, ref i, "--source");
                    break;
                case "--dest":
                case "--destination":
                    dest = RequireValue(args, ref i, "--dest");
                    break;
                case "--rb-mp-token":
                    rbMpToken = RequireValue(args, ref i, "--rb-mp-token");
                    break;
                case "--evidence-map":
                    evidenceMap = RequireValue(args, ref i, "--evidence-map");
                    break;
                case "--conn-file":
                    connFile = RequireValue(args, ref i, "--conn-file");
                    break;
                case "--verify":
                    verify = true;
                    break;
                case "--preflight":
                    preflight = true;
                    break;
                case "--verbose":
                case "-v":
                    verbose = true;
                    break;
                case "-h":
                case "--help":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Argumento desconocido: {arg}");
            }
        }

        if (showHelp)
        {
            return new MigratorOptions { ShowHelp = true };
        }

        if (preflight)
        {
            // --preflight no requiere --source/--dest; lee de --conn-file.
            return new MigratorOptions
            {
                ConnFile = string.IsNullOrWhiteSpace(connFile) ? "connectionStrings.txt" : connFile,
                Preflight = true,
                Verbose = verbose,
            };
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Falta --source (connection string del origen).");
        }
        if (string.IsNullOrWhiteSpace(dest))
        {
            throw new ArgumentException("Falta --dest (connection string del destino).");
        }
        if (string.Equals(source, dest, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Origen y destino son el mismo connection string. Peligro: se sobrescribiria el origen.");
        }

        return new MigratorOptions
        {
            Source = source,
            Destination = dest,
            RbMpToken = rbMpToken,
            EvidenceMapPath = evidenceMap,
            Verify = verify,
            Preflight = preflight,
            Verbose = verbose,
        };
    }

    private static string RequireValue(string[] args, ref int i, string name)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"{name} requiere un valor.");
        }
        i++;
        return args[i];
    }
}
