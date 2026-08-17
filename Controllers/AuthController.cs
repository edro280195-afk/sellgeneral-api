using System.IdentityModel.Tokens.Jwt;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private const string AccountTypeClient = "client";
    private const string AccountTypeSeller = "seller";
    private const string CurrentLegalVersion = "2026-08-17";
    private const double DefaultDepotLat = 27.4861;
    private const double DefaultDepotLng = -99.5069;
    private const string DefaultGeocodingRegion = "Nuevo Laredo, Tamaulipas, MX";
    private const int MaxFirebaseIdTokenLength = 16_384;

    private readonly AppDbContext _db;
    private readonly ITokenService _tokenService;
    private readonly IRefreshTokenService _refreshTokens;
    private readonly IHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly IPhoneVerificationService _phoneVerification;
    private readonly ISellerTrialPolicy _sellerTrialPolicy;
    private readonly ILogger<AuthController> _logger;
    private readonly IFirebaseAuthService? _firebaseAuth;

    public AuthController(
        AppDbContext db,
        ITokenService tokenService,
        IRefreshTokenService refreshTokens,
        IHostEnvironment env,
        IConfiguration config,
        IPhoneVerificationService phoneVerification,
        ILogger<AuthController>? logger = null,
        ISellerTrialPolicy? sellerTrialPolicy = null,
        IFirebaseAuthService? firebaseAuth = null)
    {
        _db = db;
        _tokenService = tokenService;
        _refreshTokens = refreshTokens;
        _env = env;
        _config = config;
        _phoneVerification = phoneVerification;
        _sellerTrialPolicy = sellerTrialPolicy ??
            new SellerTrialPolicy(db, config, TimeProvider.System);
        _logger = logger ?? NullLogger<AuthController>.Instance;
        _firebaseAuth = firebaseAuth;
    }

    /// <summary>
    /// En Development (o con Auth:DevOtpEnabled=true) se usa un código fijo.
    /// En producción el flujo se delega a Twilio Verify (canal WhatsApp).
    /// </summary>
    private bool IsDevOtpEnabled =>
        _env.IsDevelopment() ||
        string.Equals(_config["Auth:DevOtpEnabled"], "true", StringComparison.OrdinalIgnoreCase);

    private string DevOtpCode
    {
        get
        {
            var configured = _config["Auth:DevOtpCode"]?.Trim();
            return configured is { Length: 6 } && configured.All(char.IsDigit)
                ? configured
                : "000000";
        }
    }

    // ── Acceso de equipo (correo + contraseña, cuentas legacy admin/conductor) ──

    [HttpPost("register")]
    [EnableRateLimiting(SecurityRateLimitPolicies.AuthPassword)]
    public async Task<ActionResult<LoginResponse>> Register(
        RegisterRequest req,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(req.Name) ||
            string.IsNullOrWhiteSpace(req.Email) ||
            string.IsNullOrWhiteSpace(req.Password))
        {
            return BadRequest(new { message = "Nombre, correo y contraseña son obligatorios." });
        }

        if (req.Password.Length is < 8 or > 128)
        {
            return BadRequest(new { message = "La contraseña debe tener entre 8 y 128 caracteres." });
        }

        var legalError = ValidateLegalAcceptance(req.AcceptedLegal);
        if (legalError is not null) return legalError;

        var email = NormalizeEmail(req.Email);
        if (await _db.Accounts.AnyAsync(a => a.Email == email))
        {
            return Conflict(new { message = "Ya existe una cuenta con ese correo." });
        }

        var account = new Account
        {
            DisplayName = req.Name.Trim(),
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password)
        };
        MarkLegalAccepted(account, req.LegalVersion);

        _db.Accounts.Add(account);
        await _db.SaveChangesAsync();

        return Ok(await BuildLoginResponseAsync(account, [], cancellationToken));
    }

    [HttpPost("login")]
    [EnableRateLimiting(SecurityRateLimitPolicies.AuthPassword)]
    public async Task<ActionResult<LoginResponse>> Login(
        LoginRequest req,
        CancellationToken cancellationToken = default)
    {
        var email = NormalizeEmail(req.Email);
        var account = await _db.Accounts
            .Include(a => a.Memberships)
                .ThenInclude(m => m.Business)
            .FirstOrDefaultAsync(a => a.Email == email);

        if (account?.PasswordHash is null ||
            !BCrypt.Net.BCrypt.Verify(req.Password, account.PasswordHash))
        {
            return Unauthorized(new { message = "Correo o contraseña incorrectos." });
        }

        // Alta de vendedora interrumpida antes de confirmar el teléfono: el
        // Business/Membership recién se crea en ConfirmPhone (AddSellerBusinessAsync),
        // así que esta cuenta todavía no tiene tienda. Sin este candado el login
        // "funcionaba" pero el router de Flutter, al ver 0 memberships, mandaba a la
        // vendedora al tour de CLIENTA por error (hallazgo QA 2026-08-05). Acotado a
        // cuentas con teléfono y sin membership todavía, para no afectar cuentas
        // legacy de admin/conductor (sin teléfono) ni vendedoras ya confirmadas.
        if (account.Phone is not null &&
            account.PhoneVerifiedAt is null &&
            account.Memberships.Count == 0)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "phone_not_verified",
                needsPhoneVerification = true,
                phone = account.Phone,
                message = "Confirma tu teléfono para terminar de crear tu tienda. Regístrate de nuevo con el mismo correo y teléfono para reenviar el código por WhatsApp."
            });
        }

        return Ok(await BuildLoginResponseAsync(account, account.Memberships, cancellationToken));
    }

    // ── Sesión: refresh token (re-autenticación silenciosa para todas las cuentas) ──

    /// <summary>
    /// Canjea un refresh token por una sesión nueva y rota el token. La app lo
    /// llama al arrancar si el JWT expiró, evitando pedir credenciales de nuevo.
    /// </summary>
    [HttpPost("refresh")]
    [EnableRateLimiting(SecurityRateLimitPolicies.AuthSession)]
    public async Task<ActionResult<LoginResponse>> Refresh(
        RefreshRequest req,
        CancellationToken cancellationToken = default)
    {
        var result = await _refreshTokens.RotateAsync(req.RefreshToken, cancellationToken);
        if (result is null)
        {
            return Unauthorized(new
            {
                error = "invalid_refresh_token",
                message = "Tu sesión expiró. Vuelve a iniciar sesión."
            });
        }

        return Ok(BuildLoginResponseCore(
            result.Account,
            result.Account.Memberships,
            result.RefreshToken));
    }

    /// <summary>Cierra la sesión revocando el refresh token (best-effort).</summary>
    [HttpPost("logout")]
    [EnableRateLimiting(SecurityRateLimitPolicies.AuthSession)]
    public async Task<IActionResult> Logout(
        RefreshRequest req,
        CancellationToken cancellationToken = default)
    {
        await _refreshTokens.RevokeAsync(req.RefreshToken, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Elimina la cuenta autenticada. Los datos de identidad, sesiones, tokens
    /// de dispositivo, seguidores y vínculos personales se eliminan. Los
    /// registros operativos que tienen valor contable se conservan anonimizados.
    /// </summary>
    [HttpDelete("me")]
    [Authorize(Policy = AuthorizationPolicies.AuthenticatedAccount)]
    [SkipTenantResolution]
    [BypassSubscriptionLock]
    [EnableRateLimiting(SecurityRateLimitPolicies.AccountDeletion)]
    public async Task<IActionResult> DeleteMyAccount(
        CancellationToken cancellationToken = default)
    {
        var accountId = ReadAuthenticatedAccountId();
        if (accountId is null) return Unauthorized();

        var account = await _db.Accounts
            .Include(a => a.Memberships)
            .SingleOrDefaultAsync(a => a.Id == accountId.Value, cancellationToken);
        if (account is null) return NotFound(new { message = "La cuenta ya no existe." });

        // Firebase es una identidad externa. Se elimina antes de tocar la base
        // local; si el proveedor no confirma la baja, no se elimina información
        // local para que la usuaria pueda reintentar de forma consistente.
        if (!string.IsNullOrWhiteSpace(account.FirebaseUid) &&
            (_firebaseAuth is null ||
             !_firebaseAuth.IsConfigured ||
             !await _firebaseAuth.DeleteUserAsync(account.FirebaseUid, cancellationToken)))
        {
            _logger.LogError(
                "No se pudo eliminar FirebaseUid {FirebaseUid} de Account {AccountId}.",
                account.FirebaseUid,
                account.Id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "identity_deletion_unavailable",
                message = "No pudimos completar la eliminación de tu identidad. Inténtalo nuevamente más tarde."
            });
        }

        var clients = await _db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.AccountId == account.Id)
            .ToListAsync(cancellationToken);
        var clientIds = clients.Select(c => c.Id).ToArray();

        // InMemory solo se usa en pruebas unitarias y no soporta transacciones;
        // PostgreSQL siempre entra por la rama transaccional.
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction =
            string.Equals(
                _db.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.InMemory",
                StringComparison.Ordinal)
                ? null
                : await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Estas entidades contienen identidad, sesiones o identificadores
            // de dispositivo y no tienen valor contable que debamos conservar.
            _db.RefreshTokens.RemoveRange(
                await _db.RefreshTokens
                    .Where(t => t.AccountId == account.Id)
                    .ToListAsync(cancellationToken));
            _db.BuyerDeviceTokens.RemoveRange(
                await _db.BuyerDeviceTokens
                    .Where(t => t.AccountId == account.Id)
                    .ToListAsync(cancellationToken));
            _db.StoreFollowers.RemoveRange(
                await _db.StoreFollowers
                    .IgnoreQueryFilters()
                    .Where(f => f.AccountId == account.Id)
                    .ToListAsync(cancellationToken));
            _db.ClientClaimAudits.RemoveRange(
                await _db.ClientClaimAudits
                    .Where(a => a.AccountId == account.Id)
                    .ToListAsync(cancellationToken));

            var notifications = await _db.Notifications
                .IgnoreQueryFilters()
                .Where(n => n.AccountId == account.Id ||
                            (n.ClientId != null && clientIds.Contains(n.ClientId.Value)))
                .ToListAsync(cancellationToken);
            _db.Notifications.RemoveRange(notifications);

            if (clientIds.Length > 0)
            {
                _db.PushSubscriptions.RemoveRange(
                    await _db.PushSubscriptions
                        .IgnoreQueryFilters()
                        .Where(s => s.ClientId != null && clientIds.Contains(s.ClientId.Value))
                        .ToListAsync(cancellationToken));
                _db.ClientAliases.RemoveRange(
                    await _db.ClientAliases
                        .IgnoreQueryFilters()
                        .Where(a => clientIds.Contains(a.ClientId))
                        .ToListAsync(cancellationToken));

                // Los pedidos y sus pagos siguen perteneciendo al negocio. Se
                // conserva solo el cascarón operativo, sin datos de contacto.
                foreach (var client in clients)
                {
                    client.AccountId = null;
                    client.Name = $"Clienta eliminada #{client.Id}";
                    client.Phone = null;
                    client.Address = null;
                    client.Latitude = null;
                    client.Longitude = null;
                    client.DeliveryInstructions = null;
                    client.NormalizedName = string.Empty;
                    client.NormalizedPhone = null;
                    client.NormalizedAddress = null;
                    client.CurrentPoints = 0;
                    client.LifetimePoints = 0;
                }
            }

            // Memberships no contienen historial de negocio por sí mismas y se
            // eliminan para revocar el acceso del usuario a todos sus negocios.
            _db.Memberships.RemoveRange(account.Memberships);

            var hasCashHistory = await _db.CashRegisterSessions
                .IgnoreQueryFilters()
                .AnyAsync(s => s.AccountId == account.Id, cancellationToken);

            if (hasCashHistory)
            {
                // La caja es un registro contable con FK obligatoria a Account.
                // Se conserva el registro técnico, pero se elimina la identidad.
                account.DisplayName = "Cuenta eliminada";
                account.FirstName = null;
                account.LastName = null;
                account.ProfilePhotoUrl = null;
                account.Phone = null;
                account.PhoneVerifiedAt = null;
                account.FirebaseUid = null;
                account.Email = $"deleted-{account.Id}-{Guid.NewGuid():N}@deleted.local";
                account.PasswordHash = null;
                account.LegalAcceptedAtUtc = null;
                account.LegalVersion = null;
                account.BuyerOnboardingCompletedAtUtc = null;
                account.SellerOnboardingCompletedAtUtc = null;
                account.SellerTrialGrantedAtUtc = null;
                account.SellerTrialEvaluatedAtUtc = null;
                account.SellerTrialDeviceHash = null;
                account.SellerTrialRestrictionReason = null;
            }
            else
            {
                _db.Accounts.Remove(account);
            }

            await _db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            _logger.LogError(ex, "Falló la eliminación local de Account {AccountId}.", account.Id);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = "account_deletion_failed",
                message = "No pudimos completar la eliminación de tu cuenta. Inténtalo nuevamente."
            });
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }

        return NoContent();
    }

    /// <summary>
    /// Intercambia un Firebase ID token validado por el JWT y refresh token de
    /// Nenis. El teléfono nunca se recibe desde Flutter: se obtiene del
    /// Firebase user record después de validar criptográficamente el token.
    /// </summary>
    [HttpPost("firebase")]
    [EnableRateLimiting(SecurityRateLimitPolicies.FirebaseAuth)]
    public async Task<ActionResult<LoginResponse>> FirebaseLogin(
        FirebaseLoginRequest req,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(req.IdToken) ||
            req.IdToken.Length > MaxFirebaseIdTokenLength)
        {
            return BadRequest(new { message = "La sesión de Firebase no es válida." });
        }

        if (_firebaseAuth is null || !_firebaseAuth.IsConfigured)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "firebase_auth_not_configured",
                message = "La autenticación no está disponible por el momento."
            });
        }

        var identity = await _firebaseAuth.VerifyIdTokenAsync(
            req.IdToken,
            cancellationToken);
        if (identity is null)
        {
            return Unauthorized(new
            {
                error = "invalid_firebase_token",
                message = "No pudimos validar tu sesión."
            });
        }

        if (string.IsNullOrWhiteSpace(identity.Uid) || identity.Uid.Length > 128)
        {
            return Unauthorized(new
            {
                error = "invalid_firebase_identity",
                message = "No pudimos validar tu sesión."
            });
        }

        if (identity.IsDisabled)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "firebase_account_disabled",
                message = "Tu cuenta está deshabilitada."
            });
        }

        var phone = PhoneNumberNormalizer.NormalizeE164(identity.PhoneNumber);
        if (phone is null)
        {
            return Unauthorized(new
            {
                error = "firebase_phone_missing",
                message = "No pudimos confirmar el teléfono de tu cuenta."
            });
        }

        var accountType = NormalizeAccountType(req.AccountType);
        if (accountType is null)
        {
            return BadRequest(new { message = "El tipo de cuenta debe ser client o seller." });
        }

        var accountByFirebaseUid = await _db.Accounts
            .Include(a => a.Memberships)
                .ThenInclude(m => m.Business)
            .FirstOrDefaultAsync(a => a.FirebaseUid == identity.Uid, cancellationToken);

        var nationalPhone = PhoneNumberNormalizer.ToMexicanNational(phone);
        var phoneCandidates = await _db.Accounts
            .Include(a => a.Memberships)
                .ThenInclude(m => m.Business)
            .Where(a => a.Phone == phone || a.Phone == nationalPhone)
            .ToListAsync(cancellationToken);

        if (phoneCandidates.Count > 1)
        {
            _logger.LogError(
                "Hay más de una cuenta local para el teléfono verificado de Firebase; se bloqueó el canje.");
            return Conflict(new
            {
                error = "phone_identity_conflict",
                message = "No pudimos vincular tu teléfono con una sola cuenta."
            });
        }

        var accountByPhone = phoneCandidates.SingleOrDefault();
        if (accountByFirebaseUid is not null &&
            accountByPhone is not null &&
            accountByFirebaseUid.Id != accountByPhone.Id)
        {
            return Conflict(new
            {
                error = "firebase_identity_conflict",
                message = "No pudimos vincular tu teléfono con esa cuenta."
            });
        }

        var account = accountByFirebaseUid ?? accountByPhone;
        var isNewAccount = account is null;

        if (accountType == AccountTypeSeller &&
            (account?.Memberships.Count ?? 0) == 0)
        {
            var sellerData = ValidateSellerBusiness(req.BusinessName, req.City);
            if (sellerData.Error is not null)
            {
                return Conflict(new
                {
                    error = "firebase_profile_required",
                    needsProfile = true,
                    message = sellerData.Error
                });
            }
        }

        if (isNewAccount)
        {
            var legalError = ValidateLegalAcceptance(req.AcceptedLegal);
            if (legalError is not null) return legalError;
        }

        var email = NormalizeOptionalEmail(req.Email);
        if (req.Email is not null && email is null)
        {
            return BadRequest(new { message = "Escribe un correo válido." });
        }

        if (email is not null)
        {
            var emailOwner = await _db.Accounts
                .FirstOrDefaultAsync(a => a.Email == email, cancellationToken);
            if (emailOwner is not null && emailOwner.Id != (account?.Id ?? 0))
            {
                return Conflict(new { message = "Ese correo ya está registrado con otra cuenta." });
            }
        }

        if (!string.IsNullOrWhiteSpace(req.Password) &&
            req.Password.Length is < 8 or > 128)
        {
            return BadRequest(new { message = "La contraseña debe tener entre 8 y 128 caracteres." });
        }

        if (account is null)
        {
            var firstName = NormalizeOptional(req.FirstName, 100);
            var lastName = NormalizeOptional(req.LastName, 100);
            var displayName = ComposeDisplayName(firstName, lastName);
            account = new Account
            {
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Clienta" : displayName,
                FirstName = firstName,
                LastName = lastName,
                Phone = phone,
                FirebaseUid = identity.Uid,
                PhoneVerifiedAt = DateTime.UtcNow,
                Email = email,
                PasswordHash = BuildOptionalPasswordHash(req.Password)
            };
            MarkLegalAccepted(account, req.LegalVersion);
            _db.Accounts.Add(account);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(account.FirebaseUid) &&
                !string.Equals(account.FirebaseUid, identity.Uid, StringComparison.Ordinal))
            {
                return Conflict(new
                {
                    error = "firebase_identity_conflict",
                    message = "No pudimos vincular tu teléfono con esa cuenta."
                });
            }

            if (!PhoneNumberNormalizer.MatchesStoredPhone(account.Phone, phone))
            {
                return Conflict(new
                {
                    error = "firebase_phone_conflict",
                    message = "El teléfono verificado no coincide con esa cuenta."
                });
            }

            account.FirebaseUid = identity.Uid;
            account.Phone = phone;
            account.PhoneVerifiedAt ??= DateTime.UtcNow;
            if (string.IsNullOrWhiteSpace(account.FirstName))
                account.FirstName = NormalizeOptional(req.FirstName, 100);
            if (string.IsNullOrWhiteSpace(account.LastName))
                account.LastName = NormalizeOptional(req.LastName, 100);
            if (string.IsNullOrWhiteSpace(account.DisplayName))
                account.DisplayName = ComposeDisplayName(account.FirstName, account.LastName);
            if (account.Email is null) account.Email = email;
            if (account.PasswordHash is null)
                account.PasswordHash = BuildOptionalPasswordHash(req.Password);
        }

        if (accountType == AccountTypeSeller && account.Memberships.Count == 0)
        {
            if (account.LegalAcceptedAtUtc is null)
            {
                var legalError = ValidateLegalAcceptance(req.AcceptedLegal);
                if (legalError is not null) return legalError;
                MarkLegalAccepted(account, req.LegalVersion);
            }

            var sellerData = ValidateSellerBusiness(req.BusinessName, req.City);
            if (sellerData.Error is not null)
                return Conflict(new { error = "firebase_profile_required", needsProfile = true, message = sellerData.Error });

            await AddSellerBusinessAsync(
                account,
                sellerData.Name!,
                sellerData.City,
                cancellationToken);
        }

        await SaveChangesHandlingTrialRaceAsync(account, cancellationToken);
        return Ok(await BuildLoginResponseAsync(account, account.Memberships, cancellationToken));
    }

    // ── Compradora: registro por teléfono + contraseña (confirmación por WhatsApp) ──

    /// <summary>
    /// Alta de una compradora: nombre, apellido, correo, teléfono y contraseña.
    /// Crea (o refresca, si aún no verificaba) la cuenta y envía un código por
    /// WhatsApp. La cuenta queda inactiva para login hasta confirmar en phone/confirm.
    /// </summary>
    [HttpPost("phone/register")]
    [EnableRateLimiting("otp-send")]
    public async Task<ActionResult> RegisterPhone(
        PhoneRegisterRequest req,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(req.FirstName) || string.IsNullOrWhiteSpace(req.LastName))
        {
            return BadRequest(new { message = "Escribe tu nombre y tu apellido." });
        }

        if (string.IsNullOrWhiteSpace(req.Password) ||
            req.Password.Length is < 8 or > 128)
        {
            return BadRequest(new { message = "La contraseña debe tener entre 8 y 128 caracteres." });
        }

        var phone = _phoneVerification.NormalizePhone(req.Phone);
        if (string.IsNullOrWhiteSpace(phone))
        {
            return BadRequest(new { message = "Escribe un teléfono mexicano de 10 dígitos con lada." });
        }

        if (string.IsNullOrWhiteSpace(req.Email))
        {
            return BadRequest(new { message = "Escribe tu correo." });
        }

        var email = NormalizeEmail(req.Email);
        if (!LooksLikeEmail(email))
        {
            return BadRequest(new { message = "Escribe un correo válido." });
        }

        var accountType = NormalizeAccountType(req.AccountType);
        if (accountType is null)
        {
            return BadRequest(new { message = "El tipo de cuenta debe ser client o seller." });
        }

        var legalError = ValidateLegalAcceptance(req.AcceptedLegal);
        if (legalError is not null) return legalError;

        if (accountType == AccountTypeSeller)
        {
            var sellerData = ValidateSellerBusiness(req.BusinessName, req.City);
            if (sellerData.Error is not null)
            {
                return BadRequest(new { message = sellerData.Error });
            }
        }

        var existing = await LoadAccountByPhoneAsync(phone, cancellationToken);

        if (existing is not null && existing.PhoneVerifiedAt is not null)
        {
            return Conflict(new { message = "Ya existe una cuenta con ese teléfono. Inicia sesión." });
        }

        // Que el correo no lo tenga OTRA cuenta.
        var emailOwner = await _db.Accounts
            .FirstOrDefaultAsync(a => a.Email == email, cancellationToken);
        if (emailOwner is not null && emailOwner.Id != (existing?.Id ?? 0))
        {
            return Conflict(new { message = "Ese correo ya está registrado con otra cuenta." });
        }

        var displayName = ComposeDisplayName(req.FirstName, req.LastName);
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(req.Password);

        if (existing is null)
        {
            existing = new Account
            {
                DisplayName = displayName,
                FirstName = req.FirstName.Trim(),
                LastName = req.LastName.Trim(),
                Phone = phone,
                Email = email,
                PasswordHash = passwordHash
            };
            MarkLegalAccepted(existing, req.LegalVersion);
            _db.Accounts.Add(existing);
        }
        else
        {
            // Cuenta previa sin confirmar: refrescamos datos y dejamos reintentar.
            existing.DisplayName = displayName;
            existing.FirstName = req.FirstName.Trim();
            existing.LastName = req.LastName.Trim();
            existing.Email = email;
            existing.PasswordHash = passwordHash;
            // La nueva prueba de posesión por teléfono reemplaza cualquier
            // identidad social pendiente que nunca llegó a verificarse.
            existing.ProfilePhotoUrl = null;
            MarkLegalAccepted(existing, req.LegalVersion);
        }

        await _db.SaveChangesAsync(cancellationToken);

        return await SendVerificationCodeAsync(phone, cancellationToken);
    }

    /// <summary>
    /// Confirma el teléfono con el código recibido por WhatsApp. Marca la cuenta
    /// como verificada y devuelve la sesión (JWT). Sirve tanto para el alta como
    /// para reconfirmar un teléfono pendiente.
    /// </summary>
    [HttpPost("phone/confirm")]
    [EnableRateLimiting("otp-check")]
    public async Task<ActionResult<LoginResponse>> ConfirmPhone(
        VerifyPhoneLoginRequest req,
        CancellationToken cancellationToken = default)
    {
        var phone = _phoneVerification.NormalizePhone(req.Phone);
        if (string.IsNullOrWhiteSpace(phone))
        {
            return BadRequest(new { message = "Escribe un teléfono válido." });
        }

        var codeError = ValidateCodeFormat(req.Code);
        if (codeError is not null) return codeError;

        var codeCheck = await CheckVerificationCodeAsync(phone, req.Code.Trim(), cancellationToken);
        if (codeCheck is not null) return codeCheck;

        var account = await LoadAccountByPhoneAsync(phone, cancellationToken);

        if (account is null)
        {
            return NotFound(new
            {
                message = "No encontramos un registro con ese teléfono. Regístrate primero."
            });
        }

        var accountType = NormalizeAccountType(req.AccountType);
        if (!string.IsNullOrWhiteSpace(req.AccountType) && accountType is null)
        {
            return BadRequest(new { message = "El tipo de cuenta debe ser client o seller." });
        }

        if (account.PhoneVerifiedAt is null)
        {
            account.PhoneVerifiedAt = DateTime.UtcNow;
        }

        if (accountType == AccountTypeSeller && account.Memberships.Count == 0)
        {
            if (account.LegalAcceptedAtUtc is null)
            {
                var legalError = ValidateLegalAcceptance(req.AcceptedLegal);
                if (legalError is not null) return legalError;
                MarkLegalAccepted(account, req.LegalVersion);
            }

            var sellerData = ValidateSellerBusiness(req.BusinessName, req.City);
            if (sellerData.Error is not null)
            {
                return BadRequest(new { message = sellerData.Error });
            }

            await AddSellerBusinessAsync(
                account,
                sellerData.Name!,
                sellerData.City,
                cancellationToken);
        }

        await SaveChangesHandlingTrialRaceAsync(account, cancellationToken);
        return Ok(await BuildLoginResponseAsync(account, account.Memberships, cancellationToken));
    }

    /// <summary>
    /// Acceso de la compradora ya registrada: teléfono + contraseña. Si la cuenta
    /// existe pero nunca confirmó su teléfono, reenvía el código y responde 403
    /// para que la app la mande a la pantalla de confirmación.
    /// </summary>
    [HttpPost("phone/login")]
    [EnableRateLimiting(SecurityRateLimitPolicies.AuthPassword)]
    public async Task<ActionResult<LoginResponse>> LoginPhone(
        PhonePasswordLoginRequest req,
        CancellationToken cancellationToken = default)
    {
        var phone = _phoneVerification.NormalizePhone(req.Phone);
        if (string.IsNullOrWhiteSpace(phone) || string.IsNullOrWhiteSpace(req.Password))
        {
            return Unauthorized(new { message = "Teléfono o contraseña incorrectos." });
        }

        var account = await LoadAccountByPhoneAsync(phone, cancellationToken);

        if (account?.PasswordHash is null ||
            !BCrypt.Net.BCrypt.Verify(req.Password, account.PasswordHash))
        {
            return Unauthorized(new { message = "Teléfono o contraseña incorrectos." });
        }

        if (account.PhoneVerifiedAt is null)
        {
            // Best-effort: reenviar el código para que confirme.
            await TrySendVerificationCodeAsync(phone, cancellationToken);
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "phone_not_verified",
                needsPhoneVerification = true,
                phone,
                message = "Confirma tu teléfono con el código que te enviamos por WhatsApp."
            });
        }

        return Ok(await BuildLoginResponseAsync(account, account.Memberships, cancellationToken));
    }

    /// <summary>
    /// Solicita un código por WhatsApp para restablecer la contraseña. La
    /// respuesta no confirma si el teléfono pertenece a una cuenta.
    /// </summary>
    [HttpPost("password/reset/request")]
    [EnableRateLimiting("otp-send")]
    public async Task<ActionResult> RequestPasswordReset(
        PasswordResetRequest req,
        CancellationToken cancellationToken = default)
    {
        var phone = _phoneVerification.NormalizePhone(req.Phone);
        if (string.IsNullOrWhiteSpace(phone))
        {
            return BadRequest(new
            {
                message = "Escribe un teléfono mexicano de 10 dígitos con lada."
            });
        }

        var e164Phone = PhoneNumberNormalizer.NormalizeE164(phone);
        var mexicanNationalPhone = PhoneNumberNormalizer.ToMexicanNational(e164Phone);
        var accountExists = await _db.Accounts
            .AsNoTracking()
            .AnyAsync(
                account => account.Phone == phone ||
                           account.Phone == e164Phone ||
                           account.Phone == mexicanNationalPhone,
                cancellationToken);

        if (IsDevOtpEnabled)
        {
            return Accepted(new
            {
                phone,
                otpRequired = true,
                channel = "whatsapp",
                providerConfigured = _phoneVerification.IsConfigured,
                devMode = true,
                message = $"Modo DEV: usa el código {DevOtpCode} para restablecer la contraseña."
            });
        }

        if (!_phoneVerification.IsConfigured)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "otp_provider_not_configured",
                message = "El servicio de WhatsApp aún no está configurado."
            });
        }

        if (accountExists)
        {
            try
            {
                var outcome = await _phoneVerification.SendCodeAsync(
                    phone,
                    cancellationToken);
                if (outcome != PhoneVerificationOutcome.Sent)
                {
                    _logger.LogWarning(
                        "No se pudo enviar el OTP de restablecimiento a un teléfono terminado en {PhoneSuffix}",
                        phone[^4..]);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Falló el proveedor de OTP durante un restablecimiento de contraseña");
            }
        }

        return Accepted(new
        {
            phone,
            otpRequired = true,
            channel = "whatsapp",
            providerConfigured = true,
            devMode = false,
            message = "Si el teléfono corresponde a una cuenta verificada, enviaremos un código por WhatsApp."
        });
    }

    /// <summary>
    /// Valida el código enviado al teléfono verificado y reemplaza la
    /// contraseña con un hash BCrypt.
    /// </summary>
    [HttpPost("password/reset/confirm")]
    [EnableRateLimiting("otp-check")]
    public async Task<ActionResult> ConfirmPasswordReset(
        ConfirmPasswordResetRequest req,
        CancellationToken cancellationToken = default)
    {
        var phone = _phoneVerification.NormalizePhone(req.Phone);
        if (string.IsNullOrWhiteSpace(phone))
        {
            return BadRequest(new
            {
                message = "Escribe un teléfono mexicano de 10 dígitos con lada."
            });
        }

        if (string.IsNullOrWhiteSpace(req.NewPassword) ||
            req.NewPassword.Length is < 8 or > 128)
        {
            return BadRequest(new
            {
                message = "La contraseña debe tener entre 8 y 128 caracteres."
            });
        }

        var codeError = ValidateCodeFormat(req.Code);
        if (codeError is not null) return codeError;

        var codeCheck = await CheckVerificationCodeAsync(
            phone,
            req.Code.Trim(),
            cancellationToken);
        if (codeCheck is not null)
        {
            _logger.LogWarning(
                "Falló la verificación de un restablecimiento para un teléfono terminado en {PhoneSuffix}",
                phone[^4..]);
            return codeCheck;
        }

        var account = await _db.Accounts
            .FirstOrDefaultAsync(
                candidate => candidate.Phone == phone,
                cancellationToken);
        if (account is null)
        {
            return Unauthorized(new
            {
                message = "No pudimos restablecer la contraseña."
            });
        }

        if (account.PhoneVerifiedAt is null)
        {
            account.PhoneVerifiedAt = DateTime.UtcNow;
        }
        account.PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Contraseña restablecida para la cuenta {AccountId}",
            account.Id);

        return Ok(new
        {
            message = "Contraseña actualizada. Ya puedes continuar con tu cuenta."
        });
    }

    // ── Reenvío / flujo OTP legacy (compatibilidad) ──

    [HttpPost("phone/request-otp")]
    [EnableRateLimiting("otp-send")]
    public async Task<ActionResult> RequestPhoneOtp(
        PhoneLoginRequest req,
        CancellationToken cancellationToken = default)
    {
        var phone = _phoneVerification.NormalizePhone(req.Phone);
        if (string.IsNullOrWhiteSpace(phone))
        {
            return BadRequest(new
            {
                message = "Escribe un teléfono mexicano de 10 dígitos con lada."
            });
        }

        return await SendVerificationCodeAsync(phone, cancellationToken);
    }

    [HttpPost("phone/verify")]
    [EnableRateLimiting("otp-check")]
    public async Task<ActionResult<LoginResponse>> VerifyPhoneOtp(
        VerifyPhoneLoginRequest req,
        CancellationToken cancellationToken = default)
    {
        var phone = _phoneVerification.NormalizePhone(req.Phone);
        if (string.IsNullOrWhiteSpace(phone))
        {
            return BadRequest(new
            {
                message = "Escribe un teléfono mexicano de 10 dígitos con lada."
            });
        }

        var codeError = ValidateCodeFormat(req.Code);
        if (codeError is not null) return codeError;

        var codeCheck = await CheckVerificationCodeAsync(phone, req.Code.Trim(), cancellationToken);
        if (codeCheck is not null) return codeCheck;

        var account = await LoadAccountByPhoneAsync(phone, cancellationToken);

        if (account is null)
        {
            var legalError = ValidateLegalAcceptance(req.AcceptedLegal);
            if (legalError is not null) return legalError;

            var displayName = ComposeDisplayName(req.FirstName, req.LastName);
            account = new Account
            {
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Clienta" : displayName,
                FirstName = string.IsNullOrWhiteSpace(req.FirstName) ? null : req.FirstName.Trim(),
                LastName = string.IsNullOrWhiteSpace(req.LastName) ? null : req.LastName.Trim(),
                Phone = phone,
                PhoneVerifiedAt = DateTime.UtcNow
            };
            MarkLegalAccepted(account, req.LegalVersion);
            _db.Accounts.Add(account);
        }
        else if (account.PhoneVerifiedAt is null)
        {
            account.PhoneVerifiedAt = DateTime.UtcNow;
        }

        var accountType = NormalizeAccountType(req.AccountType);
        if (!string.IsNullOrWhiteSpace(req.AccountType) && accountType is null)
        {
            return BadRequest(new { message = "El tipo de cuenta debe ser client o seller." });
        }

        if (accountType == AccountTypeSeller && account.Memberships.Count == 0)
        {
            if (account.LegalAcceptedAtUtc is null)
            {
                var legalError = ValidateLegalAcceptance(req.AcceptedLegal);
                if (legalError is not null) return legalError;
                MarkLegalAccepted(account, req.LegalVersion);
            }

            var sellerData = ValidateSellerBusiness(req.BusinessName, req.City);
            if (sellerData.Error is not null)
            {
                return BadRequest(new { message = sellerData.Error });
            }

            await AddSellerBusinessAsync(
                account,
                sellerData.Name!,
                sellerData.City,
                cancellationToken);
        }

        await SaveChangesHandlingTrialRaceAsync(account, cancellationToken);

        return Ok(await BuildLoginResponseAsync(account, account.Memberships, cancellationToken));
    }

    // ── Helpers ──

    /// <summary>Ensambla la respuesta de sesión (JWT) con un refresh token ya conocido.</summary>
    private LoginResponse BuildLoginResponseCore(
        Account account,
        IEnumerable<Membership> memberships,
        string? refreshToken)
    {
        var membershipList = memberships
            .OrderBy(m => m.BusinessId)
            .Select(m => new AuthMembershipDto(
                m.BusinessId,
                m.Business?.Name ?? "",
                m.Role.ToString()))
            .ToList();

        var role = membershipList.FirstOrDefault()?.Role ?? "None";
        var token = _tokenService.GenerateJwt(account, memberships);
        return new LoginResponse(
            token,
            account.DisplayName,
            role,
            DateTime.UtcNow.AddDays(7),
            account.Id,
            membershipList,
            refreshToken,
            new AccountOnboardingDto(
                account.BuyerOnboardingCompletedAtUtc is not null,
                account.SellerOnboardingCompletedAtUtc is not null,
                account.PhoneVerifiedAt is not null));
    }

    /// <summary>Emite un refresh token nuevo y devuelve la sesión completa.</summary>
    private async Task<LoginResponse> BuildLoginResponseAsync(
        Account account,
        IEnumerable<Membership> memberships,
        CancellationToken cancellationToken)
    {
        var refreshToken = await _refreshTokens.IssueAsync(account.Id, cancellationToken);
        return BuildLoginResponseCore(account, memberships, refreshToken);
    }

    private static string? NormalizeAccountType(string? accountType)
    {
        var normalized = accountType?.Trim().ToLowerInvariant();
        return normalized switch
        {
            AccountTypeClient => AccountTypeClient,
            AccountTypeSeller => AccountTypeSeller,
            _ => null
        };
    }

    private async Task<Account?> LoadAccountByPhoneAsync(
        string phone,
        CancellationToken cancellationToken)
    {
        return await _db.Accounts
            .Include(a => a.Memberships)
                .ThenInclude(m => m.Business)
            .FirstOrDefaultAsync(a => a.Phone == phone, cancellationToken);
    }

    private async Task<Account?> LoadAccountByEmailAsync(
        string email,
        CancellationToken cancellationToken)
    {
        return await _db.Accounts
            .Include(a => a.Memberships)
                .ThenInclude(m => m.Business)
            .FirstOrDefaultAsync(a => a.Email == email, cancellationToken);
    }

    private static (string? Name, string? City, string? Error) ValidateSellerBusiness(
        string? businessName,
        string? city)
    {
        var name = businessName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, null, "Escribe el nombre de tu negocio.");
        }

        if (name.Length > 150)
        {
            return (null, null, "El nombre del negocio no puede exceder 150 caracteres.");
        }

        var normalizedCity = NormalizeOptional(city, 120);
        return (name, normalizedCity, null);
    }

    private static BadRequestObjectResult? ValidateLegalAcceptance(bool acceptedLegal)
    {
        return acceptedLegal
            ? null
            : new BadRequestObjectResult(new
            {
                message = "Acepta los Términos y el Aviso de privacidad para continuar."
            });
    }

    private static void MarkLegalAccepted(Account account, string? legalVersion)
    {
        account.LegalAcceptedAtUtc ??= DateTime.UtcNow;
        account.LegalVersion = NormalizeOptional(legalVersion, 32) ?? CurrentLegalVersion;
    }

    private async Task AddSellerBusinessAsync(
        Account account,
        string businessName,
        string? city,
        CancellationToken cancellationToken)
    {
        if (account.Memberships.Count > 0) return;

        var now = DateTime.UtcNow;
        var deviceId = ControllerContext.HttpContext?.Request.Headers[
            SellerTrialPolicy.DeviceHeaderName].FirstOrDefault();
        var trialDecision = await _sellerTrialPolicy.EvaluateAsync(
            account,
            deviceId,
            cancellationToken);
        var slug = await GenerateUniqueBusinessSlugAsync(
            Slugify(businessName),
            cancellationToken);
        var business = new Business
        {
            Name = businessName,
            Slug = slug,
            City = city,
            DepotLat = _config.GetValue<double?>("Cami:RouteCenterLat") ?? DefaultDepotLat,
            DepotLng = _config.GetValue<double?>("Cami:RouteCenterLng") ?? DefaultDepotLng,
            GeocodingRegion = DefaultGeocodingRegion,
            GeminiBusinessName = businessName,
            PlanTier = PlanTiers.Pro,
            SubscriptionStatus = trialDecision.Granted
                ? SubscriptionStatus.Trialing
                : SubscriptionStatus.Expired,
            TrialEndsAt = trialDecision.Granted ? now.AddDays(14) : null,
            IsActive = true,
            CreatedAt = now
        };
        var membership = new Membership
        {
            Account = account,
            Business = business,
            Role = MembershipRole.Owner,
            CreatedAt = now
        };

        account.Memberships.Add(membership);
        _db.Businesses.Add(business);

        if (!trialDecision.Granted)
        {
            _logger.LogWarning(
                "Seller trial restricted for account {AccountId}. Reason: {Reason}",
                account.Id,
                trialDecision.RestrictionReason);
        }
    }

    private async Task SaveChangesHandlingTrialRaceAsync(
        Account account,
        CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (
            SellerTrialPolicy.IsConcurrentDeviceConflict(exception))
        {
            SellerTrialPolicy.ApplyConcurrentDeviceRestriction(account);
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogWarning(
                "Concurrent seller trial attempt restricted for account {AccountId}",
                account.Id);
        }
    }

    private async Task<string> GenerateUniqueBusinessSlugAsync(
        string baseSlug,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseSlug))
        {
            baseSlug = "tienda";
        }

        baseSlug = baseSlug.Length > 50 ? baseSlug[..50].Trim('-') : baseSlug;
        var slug = baseSlug;
        var suffix = 2;

        while (await _db.Businesses.AnyAsync(b => b.Slug == slug, cancellationToken))
        {
            var suffixText = $"-{suffix++}";
            var maxBaseLength = Math.Max(1, 60 - suffixText.Length);
            var trimmedBase = baseSlug.Length > maxBaseLength
                ? baseSlug[..maxBaseLength].Trim('-')
                : baseSlug;
            slug = $"{trimmedBase}{suffixText}";
        }

        return slug;
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }

    private int? ReadAuthenticatedAccountId()
    {
        var raw = User.FindFirstValue("account_id")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue("sub");

        return int.TryParse(raw, out var accountId) ? accountId : null;
    }

    private static string? NormalizeOptionalEmail(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? BuildOptionalPasswordHash(string? password)
    {
        return string.IsNullOrWhiteSpace(password)
            ? null
            : BCrypt.Net.BCrypt.HashPassword(password);
    }

    private static string Slugify(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var slug = new StringBuilder(normalized.Length);
        var lastWasSeparator = false;

        foreach (var rawChar in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(rawChar);
            if (category == UnicodeCategory.NonSpacingMark) continue;

            var c = char.ToLowerInvariant(rawChar);
            if (c <= 127 && char.IsLetterOrDigit(c))
            {
                slug.Append(c);
                lastWasSeparator = false;
                continue;
            }

            if (char.IsWhiteSpace(c) || c is '-' or '_')
            {
                if (!lastWasSeparator && slug.Length > 0)
                {
                    slug.Append('-');
                    lastWasSeparator = true;
                }
            }
        }

        return slug.ToString().Trim('-');
    }

    private async Task<ActionResult> SendVerificationCodeAsync(
        string phone,
        CancellationToken cancellationToken)
    {
        if (IsDevOtpEnabled)
        {
            return Accepted(new
            {
                phone,
                otpRequired = true,
                channel = "whatsapp",
                providerConfigured = _phoneVerification.IsConfigured,
                devMode = true,
                message = $"Modo DEV: usa el código {DevOtpCode} para confirmar."
            });
        }

        if (!_phoneVerification.IsConfigured)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "otp_provider_not_configured",
                message = "El servicio de WhatsApp aún no está configurado."
            });
        }

        var outcome = await _phoneVerification.SendCodeAsync(phone, cancellationToken);
        if (outcome != PhoneVerificationOutcome.Sent)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "otp_send_failed",
                message = "No pudimos enviar el código por WhatsApp. Intenta de nuevo."
            });
        }

        return Accepted(new
        {
            phone,
            otpRequired = true,
            channel = "whatsapp",
            providerConfigured = true,
            devMode = false,
            message = "Código enviado por WhatsApp."
        });
    }

    private async Task TrySendVerificationCodeAsync(
        string phone,
        CancellationToken cancellationToken)
    {
        if (IsDevOtpEnabled || !_phoneVerification.IsConfigured) return;
        try
        {
            await _phoneVerification.SendCodeAsync(phone, cancellationToken);
        }
        catch
        {
            // Silencioso: el login ya devolvió 403 needsPhoneVerification.
        }
    }

    private ActionResult? ValidateCodeFormat(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 6 || !code.All(char.IsDigit))
        {
            return BadRequest(new
            {
                error = "invalid_code_format",
                message = "El código debe tener 6 dígitos."
            });
        }

        return null;
    }

    private async Task<ActionResult?> CheckVerificationCodeAsync(
        string phone,
        string code,
        CancellationToken cancellationToken)
    {
        if (IsDevOtpEnabled)
        {
            return string.Equals(code, DevOtpCode, StringComparison.Ordinal)
                ? null
                : Unauthorized(new { error = "invalid_code", message = "Código incorrecto." });
        }

        if (!_phoneVerification.IsConfigured)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "otp_provider_not_configured",
                message = "El servicio de WhatsApp aún no está configurado."
            });
        }

        var outcome = await _phoneVerification.CheckCodeAsync(phone, code, cancellationToken);
        if (outcome == PhoneVerificationOutcome.Invalid)
        {
            return Unauthorized(new { error = "invalid_code", message = "Código incorrecto o expirado." });
        }

        if (outcome != PhoneVerificationOutcome.Approved)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "otp_verification_failed",
                message = "No pudimos validar el código. Intenta de nuevo."
            });
        }

        return null;
    }

    private static string ComposeDisplayName(string? firstName, string? lastName)
    {
        return $"{firstName?.Trim()} {lastName?.Trim()}".Trim();
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
    }

    private static bool LooksLikeEmail(string email)
    {
        var at = email.IndexOf('@');
        return at > 0 && email.IndexOf('.', at) > at + 1 && !email.EndsWith('.');
    }

    private static string NormalizeEmail(string email)
    {
        return email.Trim().ToLowerInvariant();
    }


}
