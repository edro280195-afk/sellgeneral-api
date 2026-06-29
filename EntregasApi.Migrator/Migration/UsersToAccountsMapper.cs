using Microsoft.Extensions.Logging;
using Npgsql;

namespace EntregasApi.Migrator.Migration;

/// <summary>
/// Transformacion B del plan: Users (origen, ya no existe en destino) -> Accounts + Memberships.
///
/// Origen esperado (Users):
///   Id, Name, Email, PasswordHash, CreatedAt, Rol  (text: "Admin" | "Driver" | "Scaner")
///
/// Destino (Accounts):
///   Id, DisplayName, ProfilePhotoUrl, Phone, FacebookUserId, Email, PasswordHash, CreatedAt
///
/// Destino (Memberships):
///   Id, AccountId, BusinessId, Role, CreatedAt
///     Role (int): 0=Owner, 1=Admin, 2=Driver, 3=Scaner
///     Mapeo Users.Rol: "Admin"->Owner, "Driver"->Driver, "Scaner"->Scaner (default Owner).
///
/// Devuelve un diccionario legacy User.Id -> nuevo Account.Id para que
/// TransformationC (remapear FKs) pueda usarlo.
/// </summary>
public sealed class UsersToAccountsMapper
{
    private const int RoleOwner = 0;
    private const int RoleAdmin = 1;
    private const int RoleDriver = 2;
    private const int RoleScaner = 3;

    /// <summary>Offset para los Ids de Membership (evita colision con Accounts).</summary>
    private const int MembershipIdOffset = 100000;

    private readonly ILogger<UsersToAccountsMapper> _logger;

    public UsersToAccountsMapper(ILogger<UsersToAccountsMapper> logger)
    {
        _logger = logger;
    }

    public async Task<Dictionary<int, int>> MigrateAsync(
        NpgsqlConnection source,
        NpgsqlConnection dest,
        NpgsqlTransaction destTx,
        CancellationToken cancellationToken)
    {
        var userIdToAccountId = new Dictionary<int, int>();

        // 1) Leer Users desde el origen.
        var users = new List<UserRow>();
        const string selectSql = """
            SELECT "Id", "Name", "Email", "PasswordHash", "CreatedAt", "Rol"
            FROM "Users"
            ORDER BY "Id"
            """;
        await using (var srcCmd = new NpgsqlCommand(selectSql, source))
        await using (var reader = await srcCmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                users.Add(new UserRow(
                    Id: reader.GetInt32(0),
                    Name: reader.IsDBNull(1) ? null : reader.GetString(1),
                    Email: reader.IsDBNull(2) ? null : reader.GetString(2),
                    PasswordHash: reader.IsDBNull(3) ? null : reader.GetString(3),
                    CreatedAt: reader.IsDBNull(4) ? DateTime.UtcNow : reader.GetDateTime(4),
                    Rol: reader.IsDBNull(5) ? "Admin" : reader.GetString(5)));
            }
        }

        if (users.Count == 0)
        {
            _logger.LogWarning("Users: el origen no tiene filas. Se asume que la migracion ya corrio antes.");
            return userIdToAccountId;
        }

        // 2) Insertar Accounts con PK explicito (mismo Id) y luego Memberships.
        const string insertAccountSql = """
            INSERT INTO "Accounts"
                ("Id", "DisplayName", "ProfilePhotoUrl", "Phone", "FacebookUserId", "Email", "PasswordHash", "CreatedAt")
            VALUES
                (@Id, @DisplayName, NULL, NULL, NULL, @Email, @PasswordHash, @CreatedAt)
            """;
        const string insertMembershipSql = """
            INSERT INTO "Memberships"
                ("Id", "AccountId", "BusinessId", "Role", "CreatedAt")
            VALUES
                (@Id, @AccountId, @BusinessId, @Role, @CreatedAt)
            """;

        int owners = 0, drivers = 0, scaners = 0;
        foreach (var u in users)
        {
            var displayName = !string.IsNullOrWhiteSpace(u.Name)
                ? u.Name
                : (!string.IsNullOrWhiteSpace(u.Email) ? u.Email.Split('@')[0] : $"User {u.Id}");

            // Insertar Account con PK explicito (mismo Id que User).
            await using (var cmd = new NpgsqlCommand(insertAccountSql, dest, destTx))
            {
                cmd.Parameters.AddWithValue("Id", u.Id);
                cmd.Parameters.AddWithValue("DisplayName", displayName);
                cmd.Parameters.AddWithValue("Email", (object?)u.Email ?? DBNull.Value);
                cmd.Parameters.AddWithValue("PasswordHash", (object?)u.PasswordHash ?? DBNull.Value);
                cmd.Parameters.AddWithValue("CreatedAt", u.CreatedAt);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            userIdToAccountId[u.Id] = u.Id;

            // Insertar Membership con Id derivado (User.Id + 100000) para evitar colision con Accounts.
            var role = MapRole(u.Rol);
            await using (var cmd = new NpgsqlCommand(insertMembershipSql, dest, destTx))
            {
                cmd.Parameters.AddWithValue("Id", u.Id + MembershipIdOffset);
                cmd.Parameters.AddWithValue("AccountId", u.Id);
                cmd.Parameters.AddWithValue("BusinessId", MigrationPlan.BusinessId);
                cmd.Parameters.AddWithValue("Role", role);
                cmd.Parameters.AddWithValue("CreatedAt", u.CreatedAt);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            switch (role)
            {
                case RoleOwner: owners++; break;
                case RoleDriver: drivers++; break;
                case RoleScaner: scaners++; break;
            }
        }

        _logger.LogInformation(
            "Users -> Accounts+Memberships: {Count} cuentas, {Owners} Owner / {Drivers} Driver / {Scaners} Scaner",
            users.Count, owners, drivers, scaners);

        return userIdToAccountId;
    }

    private static int MapRole(string rol) => rol?.Trim().ToLowerInvariant() switch
    {
        "admin" => RoleOwner,
        "owner" => RoleOwner,
        "driver" => RoleDriver,
        "scaner" => RoleScaner,
        _ => RoleOwner,
    };

    private sealed record UserRow(
        int Id,
        string? Name,
        string? Email,
        string? PasswordHash,
        DateTime CreatedAt,
        string Rol);
}
