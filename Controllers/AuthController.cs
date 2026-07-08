using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private const string FacebookAccountTypeClient = "client";
    private const string FacebookAccountTypeSeller = "seller";
    private const string FacebookTokenTypeClassic = "classic";
    private const string FacebookTokenTypeLimited = "limited";
    private const string CurrentLegalVersion = "2026-07-08";
    private const int MaxFacebookTokenLength = 16_384;
    private const double DefaultDepotLat = 27.4861;
    private const double DefaultDepotLng = -99.5069;
    private const string DefaultGeocodingRegion = "Nuevo Laredo, Tamaulipas, MX";
    private static readonly SemaphoreSlim FacebookJwksLock = new(1, 1);
    private static IReadOnlyCollection<SecurityKey>? _facebookSigningKeys;
    private static DateTimeOffset _facebookSigningKeysExpireAt;

    private readonly AppDbContext _db;
    private readonly ITokenService _tokenService;
    private readonly IRefreshTokenService _refreshTokens;
    private readonly IHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly IPhoneVerificationService _phoneVerification;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        AppDbContext db,
        ITokenService tokenService,
        IRefreshTokenService refreshTokens,
        IHostEnvironment env,
        IConfiguration config,
        IPhoneVerificationService phoneVerification,
        IHttpClientFactory httpClientFactory,
        ILogger<AuthController>? logger = null)
    {
        _db = db;
        _tokenService = tokenService;
        _refreshTokens = refreshTokens;
        _env = env;
        _config = config;
        _phoneVerification = phoneVerification;
        _httpClientFactory = httpClientFactory;
        _logger = logger ?? NullLogger<AuthController>.Instance;
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

        return Ok(await BuildLoginResponseAsync(account, account.Memberships, cancellationToken));
    }

    // ── Sesión: refresh token (re-autenticación silenciosa para todas las cuentas) ──

    /// <summary>
    /// Canjea un refresh token por una sesión nueva y rota el token. La app lo
    /// llama al arrancar si el JWT expiró, evitando pedir credenciales de nuevo.
    /// </summary>
    [HttpPost("refresh")]
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
    public async Task<IActionResult> Logout(
        RefreshRequest req,
        CancellationToken cancellationToken = default)
    {
        await _refreshTokens.RevokeAsync(req.RefreshToken, cancellationToken);
        return NoContent();
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

        var accountType = NormalizeFacebookAccountType(req.AccountType);
        if (accountType is null)
        {
            return BadRequest(new { message = "El tipo de cuenta debe ser client o seller." });
        }

        var legalError = ValidateLegalAcceptance(req.AcceptedLegal);
        if (legalError is not null) return legalError;

        if (accountType == FacebookAccountTypeSeller)
        {
            var sellerData = ValidateSellerBusiness(req.BusinessName, req.City);
            if (sellerData.Error is not null)
            {
                return BadRequest(new { message = sellerData.Error });
            }
        }

        var existing = await _db.Accounts
            .FirstOrDefaultAsync(a => a.Phone == phone, cancellationToken);

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
            existing.FacebookUserId = null;
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

        var account = await _db.Accounts
            .Include(a => a.Memberships)
                .ThenInclude(m => m.Business)
            .FirstOrDefaultAsync(a => a.Phone == phone, cancellationToken);

        if (account is null)
        {
            return NotFound(new
            {
                message = "No encontramos un registro con ese teléfono. Regístrate primero."
            });
        }

        var accountType = NormalizeFacebookAccountType(req.AccountType);
        if (!string.IsNullOrWhiteSpace(req.AccountType) && accountType is null)
        {
            return BadRequest(new { message = "El tipo de cuenta debe ser client o seller." });
        }

        if (account.PhoneVerifiedAt is null)
        {
            account.PhoneVerifiedAt = DateTime.UtcNow;
        }

        if (accountType == FacebookAccountTypeSeller && account.Memberships.Count == 0)
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

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(await BuildLoginResponseAsync(account, account.Memberships, cancellationToken));
    }

    /// <summary>
    /// Acceso de la compradora ya registrada: teléfono + contraseña. Si la cuenta
    /// existe pero nunca confirmó su teléfono, reenvía el código y responde 403
    /// para que la app la mande a la pantalla de confirmación.
    /// </summary>
    [HttpPost("phone/login")]
    public async Task<ActionResult<LoginResponse>> LoginPhone(
        PhonePasswordLoginRequest req,
        CancellationToken cancellationToken = default)
    {
        var phone = _phoneVerification.NormalizePhone(req.Phone);
        if (string.IsNullOrWhiteSpace(phone) || string.IsNullOrWhiteSpace(req.Password))
        {
            return Unauthorized(new { message = "Teléfono o contraseña incorrectos." });
        }

        var account = await _db.Accounts
            .Include(a => a.Memberships)
                .ThenInclude(m => m.Business)
            .FirstOrDefaultAsync(a => a.Phone == phone, cancellationToken);

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

        var accountExists = await _db.Accounts
            .AsNoTracking()
            .AnyAsync(
                account => account.Phone == phone,
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
            message = "Contraseña actualizada. Ya puedes continuar con Facebook."
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

        var account = await _db.Accounts
            .Include(a => a.Memberships)
                .ThenInclude(m => m.Business)
            .FirstOrDefaultAsync(a => a.Phone == phone, cancellationToken);

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

        var accountType = NormalizeFacebookAccountType(req.AccountType);
        if (!string.IsNullOrWhiteSpace(req.AccountType) && accountType is null)
        {
            return BadRequest(new { message = "El tipo de cuenta debe ser client o seller." });
        }

        if (accountType == FacebookAccountTypeSeller && account.Memberships.Count == 0)
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

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(await BuildLoginResponseAsync(account, account.Memberships, cancellationToken));
    }

    // ── Facebook Login ──

    /// <summary>
    /// Valida Facebook y devuelve sesión solo cuando la identidad ya está
    /// vinculada y el teléfono fue confirmado. Las altas o enlaces incompletos
    /// responden 409 con los datos que la app debe solicitar.
    /// </summary>
    [HttpPost("facebook")]
    [EnableRateLimiting("facebook-auth")]
    public async Task<ActionResult<LoginResponse>> FacebookLogin(
        FacebookLoginRequest req,
        CancellationToken cancellationToken = default)
    {
        var accountType = NormalizeFacebookAccountType(req.AccountType);
        if (accountType is null)
        {
            return BadRequest(new { message = "El tipo de cuenta debe ser client o seller." });
        }

        var tokenType = NormalizeFacebookTokenType(req.TokenType);
        if (tokenType is null)
        {
            return BadRequest(new { message = "El tipo de token de Facebook no es válido." });
        }

        var providerError = ValidateFacebookProviderConfiguration(tokenType);
        if (providerError is not null) return providerError;

        if (string.IsNullOrWhiteSpace(req.AccessToken) ||
            req.AccessToken.Length > MaxFacebookTokenLength)
        {
            return BadRequest(new { message = "El token de Facebook no es válido." });
        }

        var profile = await ValidateFacebookProfileAsync(
            req.AccessToken,
            tokenType,
            cancellationToken);
        if (profile is null)
        {
            return Unauthorized(new
            {
                error = "invalid_fb_token",
                message = "No pudimos validar tu Facebook. Intenta de nuevo."
            });
        }

        var account = await LoadAccountByFacebookIdAsync(profile.Id, cancellationToken);
        if (account is null)
        {
            return Conflict(BuildFacebookContinuation(
                profile,
                accountType,
                account: null,
                requiresExistingPassword: false));
        }

        var needsSellerBusiness =
            accountType == FacebookAccountTypeSeller &&
            account.Memberships.Count == 0;
        if (account.PhoneVerifiedAt is null || needsSellerBusiness)
        {
            return Conflict(BuildFacebookContinuation(
                profile,
                accountType,
                account,
                requiresExistingPassword: false));
        }

        return Ok(await BuildLoginResponseAsync(account, account.Memberships, cancellationToken));
    }

    /// <summary>
    /// Completa los datos de una identidad de Facebook. Si coincide con una
    /// cuenta existente, exige la contraseña actual antes de vincularla. Una
    /// cuenta con teléfono pendiente recibe OTP y no obtiene JWT todavía.
    /// </summary>
    [HttpPost("facebook/complete")]
    [EnableRateLimiting("facebook-auth")]
    public async Task<ActionResult<LoginResponse>> CompleteFacebookProfile(
        FacebookCompleteProfileRequest req,
        CancellationToken cancellationToken = default)
    {
        var accountType = NormalizeFacebookAccountType(req.AccountType);
        if (accountType is null)
        {
            return BadRequest(new { message = "El tipo de cuenta debe ser client o seller." });
        }

        var tokenType = NormalizeFacebookTokenType(req.TokenType);
        if (tokenType is null)
        {
            return BadRequest(new { message = "El tipo de token de Facebook no es válido." });
        }

        var providerError = ValidateFacebookProviderConfiguration(tokenType);
        if (providerError is not null) return providerError;

        if (string.IsNullOrWhiteSpace(req.AccessToken) ||
            req.AccessToken.Length > MaxFacebookTokenLength)
        {
            return BadRequest(new { message = "El token de Facebook no es válido." });
        }

        var firstName = req.FirstName?.Trim();
        var lastName = req.LastName?.Trim();
        if (string.IsNullOrWhiteSpace(firstName) || firstName.Length > 100 ||
            string.IsNullOrWhiteSpace(lastName) || lastName.Length > 100)
        {
            return BadRequest(new { message = "Escribe nombre y apellido válidos." });
        }

        var email = NormalizeEmail(req.Email ?? "");
        if (email.Length > 150 || !LooksLikeEmail(email))
        {
            return BadRequest(new { message = "Escribe un correo válido." });
        }

        var phone = _phoneVerification.NormalizePhone(req.Phone);
        if (string.IsNullOrWhiteSpace(phone))
        {
            return BadRequest(new { message = "Escribe un teléfono mexicano de 10 dígitos con lada." });
        }

        var sellerData = ValidateSellerBusiness(req.BusinessName, req.City);
        if (accountType == FacebookAccountTypeSeller && sellerData.Error is not null)
        {
            return BadRequest(new { message = sellerData.Error });
        }

        var profile = await ValidateFacebookProfileAsync(
            req.AccessToken,
            tokenType,
            cancellationToken);
        if (profile is null)
        {
            return Unauthorized(new
            {
                error = "invalid_fb_token",
                message = "No pudimos validar tu Facebook. Intenta de nuevo."
            });
        }

        var facebookAccount = await LoadAccountByFacebookIdAsync(profile.Id, cancellationToken);
        var phoneOwner = await LoadAccountByPhoneAsync(phone, cancellationToken);
        var emailOwner = await LoadAccountByEmailAsync(email, cancellationToken);

        if (phoneOwner is not null &&
            emailOwner is not null &&
            phoneOwner.Id != emailOwner.Id)
        {
            return Conflict(new
            {
                error = "identity_conflict",
                message = "El correo y el teléfono pertenecen a cuentas distintas. Entra con tu método habitual."
            });
        }

        var account = facebookAccount;
        var willCreateAccount = account is null && phoneOwner is null && emailOwner is null;
        var candidateAccount = account ?? phoneOwner ?? emailOwner;
        var willCreateSellerBusiness =
            accountType == FacebookAccountTypeSeller &&
            (candidateAccount is null || candidateAccount.Memberships.Count == 0);
        if ((willCreateAccount || willCreateSellerBusiness) &&
            candidateAccount?.LegalAcceptedAtUtc is null)
        {
            var legalError = ValidateLegalAcceptance(req.AcceptedLegal);
            if (legalError is not null) return legalError;
        }

        if (account is null)
        {
            account = phoneOwner ?? emailOwner;
            if (account is not null)
            {
                if (!string.IsNullOrWhiteSpace(account.FacebookUserId) &&
                    !string.Equals(account.FacebookUserId, profile.Id, StringComparison.Ordinal))
                {
                    return Conflict(new
                    {
                        error = "identity_conflict",
                        message = "Esa cuenta ya tiene otro Facebook vinculado."
                    });
                }

                if (account.PasswordHash is null ||
                    string.IsNullOrWhiteSpace(req.ExistingPassword) ||
                    !BCrypt.Net.BCrypt.Verify(req.ExistingPassword, account.PasswordHash))
                {
                    return Conflict(BuildFacebookContinuation(
                        profile,
                        accountType,
                        account,
                        requiresExistingPassword: true,
                        phoneOverride: phone,
                        emailOverride: email));
                }

                account.FacebookUserId = profile.Id;
            }
            else
            {
                account = new Account
                {
                    DisplayName = ComposeDisplayName(firstName, lastName),
                    FirstName = firstName,
                    LastName = lastName,
                    FacebookUserId = profile.Id,
                    Phone = phone,
                    Email = email,
                    ProfilePhotoUrl = profile.PictureUrl
                };
                MarkLegalAccepted(account, req.LegalVersion);
                _db.Accounts.Add(account);
            }
        }

        if (req.AcceptedLegal && account.LegalAcceptedAtUtc is null)
        {
            MarkLegalAccepted(account, req.LegalVersion);
        }

        if ((phoneOwner is not null && phoneOwner.Id != account.Id) ||
            (emailOwner is not null && emailOwner.Id != account.Id))
        {
            return Conflict(new
            {
                error = "identity_conflict",
                message = "No pudimos unir esos datos en una sola cuenta."
            });
        }

        if (!string.IsNullOrWhiteSpace(account.Phone) &&
            account.PhoneVerifiedAt is not null &&
            !string.Equals(account.Phone, phone, StringComparison.Ordinal))
        {
            return Conflict(new
            {
                error = "verified_phone_change_not_allowed",
                message = "Para cambiar tu teléfono verificado, entra primero con tu método habitual."
            });
        }

        account.Phone = phone;
        if (string.IsNullOrWhiteSpace(account.Email)) account.Email = email;
        if (string.IsNullOrWhiteSpace(account.FirstName)) account.FirstName = firstName;
        if (string.IsNullOrWhiteSpace(account.LastName)) account.LastName = lastName;
        if (string.IsNullOrWhiteSpace(account.ProfilePhotoUrl))
        {
            account.ProfilePhotoUrl = profile.PictureUrl;
        }
        if (string.IsNullOrWhiteSpace(account.DisplayName) ||
            string.Equals(account.DisplayName, "Clienta", StringComparison.OrdinalIgnoreCase))
        {
            account.DisplayName = ComposeDisplayName(firstName, lastName);
        }

        if (account.PhoneVerifiedAt is not null)
        {
            if (accountType == FacebookAccountTypeSeller && account.Memberships.Count == 0)
            {
                await AddSellerBusinessAsync(
                    account,
                    sellerData.Name!,
                    sellerData.City,
                    cancellationToken);
            }

            await _db.SaveChangesAsync(cancellationToken);
            return Ok(await BuildLoginResponseAsync(account, account.Memberships, cancellationToken));
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict(new
            {
                error = "identity_conflict",
                message = "Ya existe una cuenta con esos datos."
            });
        }

        return await SendFacebookVerificationCodeAsync(
            account,
            accountType,
            cancellationToken);
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
            refreshToken);
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

    private ActionResult<LoginResponse>? ValidateFacebookProviderConfiguration(
        string tokenType)
    {
        var appSecret = _config["Facebook:AppSecret"];
        var appId = _config["Facebook:AppId"];
        var hasAppId = !string.IsNullOrWhiteSpace(appId);
        var hasRequiredSecret =
            tokenType == FacebookTokenTypeLimited ||
            !string.IsNullOrWhiteSpace(appSecret);
        if (hasAppId && hasRequiredSecret)
        {
            return null;
        }

        return StatusCode(StatusCodes.Status501NotImplemented, new
        {
            error = "facebook_provider_not_configured",
            message = "Facebook aún no está disponible. Usa teléfono o correo por ahora."
        });
    }

    private static string? NormalizeFacebookAccountType(string? accountType)
    {
        var normalized = accountType?.Trim().ToLowerInvariant();
        return normalized switch
        {
            FacebookAccountTypeClient => FacebookAccountTypeClient,
            FacebookAccountTypeSeller => FacebookAccountTypeSeller,
            _ => null
        };
    }

    private static string? NormalizeFacebookTokenType(string? tokenType)
    {
        var normalized = tokenType?.Trim().ToLowerInvariant();
        return normalized switch
        {
            FacebookTokenTypeClassic => FacebookTokenTypeClassic,
            FacebookTokenTypeLimited => FacebookTokenTypeLimited,
            _ => null
        };
    }

    private FacebookContinuationResponse BuildFacebookContinuation(
        FacebookProfile profile,
        string accountType,
        Account? account,
        bool requiresExistingPassword,
        string? phoneOverride = null,
        string? emailOverride = null)
    {
        var firstName = FirstNonBlank(account?.FirstName, profile.FirstName);
        var lastName = FirstNonBlank(account?.LastName, profile.LastName);
        var email = FirstNonBlank(emailOverride, account?.Email, profile.Email);
        var phone = FirstNonBlank(phoneOverride, account?.Phone);
        var missingFields = new List<string>();

        if (string.IsNullOrWhiteSpace(firstName)) missingFields.Add("firstName");
        if (string.IsNullOrWhiteSpace(lastName)) missingFields.Add("lastName");
        if (string.IsNullOrWhiteSpace(email)) missingFields.Add("email");
        if (string.IsNullOrWhiteSpace(phone)) missingFields.Add("phone");
        if (accountType == FacebookAccountTypeSeller &&
            (account is null || account.Memberships.Count == 0))
        {
            missingFields.Add("businessName");
        }

        return new FacebookContinuationResponse(
            Error: requiresExistingPassword
                ? "facebook_account_link_required"
                : "facebook_profile_required",
            Message: requiresExistingPassword
                ? "Para proteger una cuenta que ya usa esos datos, escribe su contraseña actual."
                : "Completa tus datos para continuar con Facebook.",
            AccountType: accountType,
            NeedsProfile: true,
            NeedsPhoneVerification: account?.PhoneVerifiedAt is null,
            RequiresExistingPassword: requiresExistingPassword,
            FirstName: firstName,
            LastName: lastName,
            Email: email,
            Phone: phone,
            MissingFields: missingFields);
    }

    private async Task<Account?> LoadAccountByFacebookIdAsync(
        string facebookUserId,
        CancellationToken cancellationToken)
    {
        return await _db.Accounts
            .Include(a => a.Memberships)
                .ThenInclude(m => m.Business)
            .FirstOrDefaultAsync(
                a => a.FacebookUserId == facebookUserId,
                cancellationToken);
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

    private async Task<ActionResult<LoginResponse>> SendFacebookVerificationCodeAsync(
        Account account,
        string accountType,
        CancellationToken cancellationToken)
    {
        var phone = account.Phone!;
        if (IsDevOtpEnabled)
        {
            return Accepted(new FacebookContinuationResponse(
                Error: "phone_verification_required",
                Message: $"Modo DEV: usa el código {DevOtpCode} para confirmar.",
                AccountType: accountType,
                NeedsProfile: false,
                NeedsPhoneVerification: true,
                RequiresExistingPassword: false,
                FirstName: account.FirstName,
                LastName: account.LastName,
                Email: account.Email,
                Phone: phone,
                MissingFields: [],
                ProviderConfigured: _phoneVerification.IsConfigured,
                DevMode: true));
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

        return Accepted(new FacebookContinuationResponse(
            Error: "phone_verification_required",
            Message: "Código enviado por WhatsApp.",
            AccountType: accountType,
            NeedsProfile: false,
            NeedsPhoneVerification: true,
            RequiresExistingPassword: false,
            FirstName: account.FirstName,
            LastName: account.LastName,
            Email: account.Email,
            Phone: phone,
            MissingFields: [],
            ProviderConfigured: true,
            DevMode: false));
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
            SubscriptionStatus = SubscriptionStatus.Trialing,
            TrialEndsAt = now.AddDays(14),
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

    /// <summary>
    /// Envía el código de verificación (DEV: código fijo; PROD: Twilio Verify por
    /// WhatsApp) y devuelve el <see cref="ActionResult"/> apropiado para el cliente.
    /// </summary>
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

    /// <summary>Reenvío best-effort (no expone errores del proveedor al cliente).</summary>
    private async Task TrySendVerificationCodeAsync(string phone, CancellationToken cancellationToken)
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

    /// <summary>Valida el código; devuelve null si es correcto o un error listo para responder.</summary>
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

    private Task<FacebookProfile?> ValidateFacebookProfileAsync(
        string accessToken,
        string tokenType,
        CancellationToken cancellationToken)
    {
        return tokenType == FacebookTokenTypeLimited
            ? ValidateLimitedFacebookProfileAsync(accessToken, cancellationToken)
            : ValidateClassicFacebookProfileAsync(accessToken, cancellationToken);
    }

    private async Task<FacebookProfile?> ValidateClassicFacebookProfileAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var appId = _config["Facebook:AppId"]!.Trim();
            var appSecret = _config["Facebook:AppSecret"]!.Trim();
            var graphApiVersion = _config["Facebook:GraphApiVersion"]?.Trim();
            if (string.IsNullOrWhiteSpace(graphApiVersion) ||
                graphApiVersion[0] != 'v' ||
                graphApiVersion.Skip(1).Any(c => !char.IsDigit(c) && c != '.'))
            {
                graphApiVersion = "v25.0";
            }

            var client = _httpClientFactory.CreateClient("facebook");
            var debugUrl =
                $"https://graph.facebook.com/{graphApiVersion}/debug_token" +
                $"?input_token={Uri.EscapeDataString(accessToken)}" +
                $"&access_token={Uri.EscapeDataString($"{appId}|{appSecret}")}";
            using var debugResponse = await client.GetAsync(debugUrl, cancellationToken);
            if (!debugResponse.IsSuccessStatusCode) return null;

            await using var debugStream =
                await debugResponse.Content.ReadAsStreamAsync(cancellationToken);
            var debug = await JsonSerializer.DeserializeAsync<FacebookTokenDebugEnvelope>(
                debugStream,
                cancellationToken: cancellationToken);
            if (debug?.Data is not
                {
                    IsValid: true,
                    UserId.Length: > 0
                } tokenData ||
                !string.Equals(tokenData.AppId, appId, StringComparison.Ordinal) ||
                tokenData.ExpiresAt > 0 &&
                DateTimeOffset.FromUnixTimeSeconds(tokenData.ExpiresAt) <= DateTimeOffset.UtcNow)
            {
                return null;
            }

            var proof = Convert.ToHexString(
                HMACSHA256.HashData(
                    Encoding.UTF8.GetBytes(appSecret),
                    Encoding.UTF8.GetBytes(accessToken)))
                .ToLowerInvariant();

            var url =
                $"https://graph.facebook.com/{graphApiVersion}/me" +
                "?fields=id,first_name,last_name,name,email,picture.type(large)" +
                $"&access_token={Uri.EscapeDataString(accessToken)}" +
                $"&appsecret_proof={proof}";

            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var profile = await JsonSerializer.DeserializeAsync<FacebookProfile>(
                stream,
                cancellationToken: cancellationToken);
            return profile is not null &&
                   string.Equals(profile.Id, tokenData.UserId, StringComparison.Ordinal)
                ? profile
                : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<FacebookProfile?> ValidateLimitedFacebookProfileAsync(
        string authenticationToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var appId = _config["Facebook:AppId"]!.Trim();
            var signingKeys = await GetFacebookSigningKeysAsync(
                forceRefresh: false,
                cancellationToken);
            if (signingKeys is null) return null;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var handler = new JwtSecurityTokenHandler
                    {
                        MapInboundClaims = false
                    };
                    var principal = handler.ValidateToken(
                        authenticationToken,
                        new TokenValidationParameters
                        {
                            RequireSignedTokens = true,
                            ValidateIssuerSigningKey = true,
                            IssuerSigningKeys = signingKeys,
                            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                            ValidateIssuer = true,
                            ValidIssuer = "https://www.facebook.com",
                            ValidateAudience = true,
                            ValidAudience = appId,
                            RequireAudience = true,
                            RequireExpirationTime = true,
                            ValidateLifetime = true,
                            ClockSkew = TimeSpan.FromMinutes(2)
                        },
                        out var validatedToken);

                    if (validatedToken is not JwtSecurityToken jwt ||
                        !string.Equals(
                            jwt.Header.Alg,
                            SecurityAlgorithms.RsaSha256,
                            StringComparison.Ordinal))
                    {
                        return null;
                    }

                    var userId = principal.FindFirst("sub")?.Value;
                    if (string.IsNullOrWhiteSpace(userId)) return null;

                    return new FacebookProfile(
                        Id: userId,
                        FirstName: principal.FindFirst("given_name")?.Value,
                        LastName: principal.FindFirst("family_name")?.Value,
                        Name: principal.FindFirst("name")?.Value,
                        Email: principal.FindFirst("email")?.Value,
                        Picture: null,
                        LimitedPictureUrl: principal.FindFirst("picture")?.Value);
                }
                catch (SecurityTokenSignatureKeyNotFoundException) when (attempt == 0)
                {
                    signingKeys = await GetFacebookSigningKeysAsync(
                        forceRefresh: true,
                        cancellationToken);
                    if (signingKeys is null) return null;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyCollection<SecurityKey>?> GetFacebookSigningKeysAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (!forceRefresh &&
            _facebookSigningKeys is not null &&
            _facebookSigningKeysExpireAt > now)
        {
            return _facebookSigningKeys;
        }

        await FacebookJwksLock.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (!forceRefresh &&
                _facebookSigningKeys is not null &&
                _facebookSigningKeysExpireAt > now)
            {
                return _facebookSigningKeys;
            }

            var client = _httpClientFactory.CreateClient("facebook");
            using var response = await client.GetAsync(
                "https://www.facebook.com/.well-known/oauth/openid/jwks/",
                cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var signingKeys = new JsonWebKeySet(json)
                .GetSigningKeys()
                .ToArray();
            if (signingKeys.Length == 0) return null;

            _facebookSigningKeys = signingKeys;
            _facebookSigningKeysExpireAt = now.AddHours(6);
            return signingKeys;
        }
        finally
        {
            FacebookJwksLock.Release();
        }
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

    private sealed record FacebookTokenDebugEnvelope(
        [property: JsonPropertyName("data")] FacebookTokenDebugData? Data);

    private sealed record FacebookTokenDebugData(
        [property: JsonPropertyName("app_id")] string AppId,
        [property: JsonPropertyName("is_valid")] bool IsValid,
        [property: JsonPropertyName("user_id")] string UserId,
        [property: JsonPropertyName("expires_at")] long ExpiresAt);

    private sealed record FacebookProfile(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("first_name")] string? FirstName,
        [property: JsonPropertyName("last_name")] string? LastName,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("picture")] FacebookPicture? Picture,
        string? LimitedPictureUrl = null)
    {
        public string? PictureUrl => Picture?.Data?.Url ?? LimitedPictureUrl;
    }

    private sealed record FacebookPicture(
        [property: JsonPropertyName("data")] FacebookPictureData? Data);

    private sealed record FacebookPictureData(
        [property: JsonPropertyName("url")] string? Url);
}
