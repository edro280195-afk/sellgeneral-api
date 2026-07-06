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

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITokenService _tokenService;
    private readonly IHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly IPhoneVerificationService _phoneVerification;
    private readonly IHttpClientFactory _httpClientFactory;

    public AuthController(
        AppDbContext db,
        ITokenService tokenService,
        IHostEnvironment env,
        IConfiguration config,
        IPhoneVerificationService phoneVerification,
        IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _tokenService = tokenService;
        _env = env;
        _config = config;
        _phoneVerification = phoneVerification;
        _httpClientFactory = httpClientFactory;
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
    public async Task<ActionResult<LoginResponse>> Register(RegisterRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) ||
            string.IsNullOrWhiteSpace(req.Email) ||
            string.IsNullOrWhiteSpace(req.Password))
        {
            return BadRequest(new { message = "Nombre, correo y contrasena son obligatorios." });
        }

        if (req.Password.Length < 8)
        {
            return BadRequest(new { message = "La contrasena debe tener al menos 8 caracteres." });
        }

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

        _db.Accounts.Add(account);
        await _db.SaveChangesAsync();

        return Ok(BuildLoginResponse(account, []));
    }

    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest req)
    {
        var email = NormalizeEmail(req.Email);
        var account = await _db.Accounts
            .Include(a => a.Memberships)
                .ThenInclude(m => m.Business)
            .FirstOrDefaultAsync(a => a.Email == email);

        if (account?.PasswordHash is null ||
            !BCrypt.Net.BCrypt.Verify(req.Password, account.PasswordHash))
        {
            return Unauthorized(new { message = "Correo o contrasena incorrectos." });
        }

        return Ok(BuildLoginResponse(account, account.Memberships));
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

        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8)
        {
            return BadRequest(new { message = "La contraseña debe tener al menos 8 caracteres." });
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

        if (account.PhoneVerifiedAt is null)
        {
            account.PhoneVerifiedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return Ok(BuildLoginResponse(account, account.Memberships));
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

        return Ok(BuildLoginResponse(account, account.Memberships));
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
                message = "Escribe un telefono mexicano de 10 digitos con lada."
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
                message = "Escribe un telefono mexicano de 10 digitos con lada."
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
            account = new Account
            {
                DisplayName = "Clienta",
                Phone = phone,
                PhoneVerifiedAt = DateTime.UtcNow
            };
            _db.Accounts.Add(account);
            await _db.SaveChangesAsync(cancellationToken);
        }
        else if (account.PhoneVerifiedAt is null)
        {
            account.PhoneVerifiedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return Ok(BuildLoginResponse(account, account.Memberships));
    }

    // ── Facebook Login ──

    /// <summary>
    /// Login con Facebook. Requiere <c>Facebook:AppId</c> y <c>Facebook:AppSecret</c>
    /// en configuración; si no están, responde 501 (la app muestra "próximamente").
    /// La primera vez exige teléfono para completar la cuenta.
    /// </summary>
    [HttpPost("facebook")]
    public async Task<ActionResult<LoginResponse>> FacebookLogin(
        FacebookLoginRequest req,
        CancellationToken cancellationToken = default)
    {
        var appSecret = _config["Facebook:AppSecret"];
        var appId = _config["Facebook:AppId"];
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appSecret))
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new
            {
                error = "facebook_provider_not_configured",
                message = "Facebook aún no está disponible. Por ahora entra con tu teléfono."
            });
        }

        if (string.IsNullOrWhiteSpace(req.AccessToken))
        {
            return BadRequest(new { message = "Falta el token de Facebook." });
        }

        var profile = await FetchFacebookProfileAsync(req.AccessToken, appSecret, cancellationToken);
        if (profile is null || string.IsNullOrWhiteSpace(profile.Id))
        {
            return Unauthorized(new
            {
                error = "invalid_fb_token",
                message = "No pudimos validar tu Facebook. Intenta de nuevo."
            });
        }

        var account = await _db.Accounts
            .Include(a => a.Memberships)
                .ThenInclude(m => m.Business)
            .FirstOrDefaultAsync(a => a.FacebookUserId == profile.Id, cancellationToken);

        if (account is not null)
        {
            return Ok(BuildLoginResponse(account, account.Memberships));
        }

        // Cuenta nueva: necesitamos el teléfono.
        var phone = _phoneVerification.NormalizePhone(req.Phone);
        if (string.IsNullOrWhiteSpace(phone))
        {
            return Conflict(new
            {
                error = "phone_required",
                needsPhone = true,
                message = "Necesitamos tu teléfono para terminar de crear tu cuenta."
            });
        }

        var firstName = FirstNonBlank(profile.FirstName, req.FirstName);
        var lastName = FirstNonBlank(profile.LastName, req.LastName);
        var displayName = ComposeDisplayName(firstName, lastName);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = FirstNonBlank(profile.Name, "Clienta")!;
        }

        // Si ese teléfono ya es de una cuenta, enlazamos Facebook a esa cuenta.
        var phoneOwner = await _db.Accounts
            .Include(a => a.Memberships)
                .ThenInclude(m => m.Business)
            .FirstOrDefaultAsync(a => a.Phone == phone, cancellationToken);

        if (phoneOwner is not null)
        {
            phoneOwner.FacebookUserId = profile.Id;
            phoneOwner.FirstName ??= firstName;
            phoneOwner.LastName ??= lastName;
            if (string.IsNullOrWhiteSpace(phoneOwner.ProfilePhotoUrl) is false) { /* conservar foto */ }
            await _db.SaveChangesAsync(cancellationToken);
            return Ok(BuildLoginResponse(phoneOwner, phoneOwner.Memberships));
        }

        // Solo asignamos el correo de FB si no lo tiene ya otra cuenta.
        string? email = null;
        if (!string.IsNullOrWhiteSpace(profile.Email))
        {
            var normalized = NormalizeEmail(profile.Email);
            var taken = await _db.Accounts.AnyAsync(a => a.Email == normalized, cancellationToken);
            if (!taken) email = normalized;
        }

        account = new Account
        {
            DisplayName = displayName,
            FirstName = firstName,
            LastName = lastName,
            FacebookUserId = profile.Id,
            Phone = phone,
            Email = email,
            PhoneVerifiedAt = DateTime.UtcNow
        };
        _db.Accounts.Add(account);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(BuildLoginResponse(account, account.Memberships));
    }

    // ── Helpers ──

    private LoginResponse BuildLoginResponse(Account account, IEnumerable<Membership> memberships)
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
            membershipList);
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
                message = $"Modo DEV: usa el codigo {DevOtpCode} para confirmar."
            });
        }

        if (!_phoneVerification.IsConfigured)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "otp_provider_not_configured",
                message = "El servicio de WhatsApp aun no esta configurado."
            });
        }

        var outcome = await _phoneVerification.SendCodeAsync(phone, cancellationToken);
        if (outcome != PhoneVerificationOutcome.Sent)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "otp_send_failed",
                message = "No pudimos enviar el codigo por WhatsApp. Intenta de nuevo."
            });
        }

        return Accepted(new
        {
            phone,
            otpRequired = true,
            channel = "whatsapp",
            providerConfigured = true,
            devMode = false,
            message = "Codigo enviado por WhatsApp."
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
                message = "El codigo debe tener 6 digitos."
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
                : Unauthorized(new { error = "invalid_code", message = "Codigo incorrecto." });
        }

        if (!_phoneVerification.IsConfigured)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "otp_provider_not_configured",
                message = "El servicio de WhatsApp aun no esta configurado."
            });
        }

        var outcome = await _phoneVerification.CheckCodeAsync(phone, code, cancellationToken);
        if (outcome == PhoneVerificationOutcome.Invalid)
        {
            return Unauthorized(new { error = "invalid_code", message = "Codigo incorrecto o expirado." });
        }

        if (outcome != PhoneVerificationOutcome.Approved)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "otp_verification_failed",
                message = "No pudimos validar el codigo. Intenta de nuevo."
            });
        }

        return null;
    }

    private async Task<FacebookProfile?> FetchFacebookProfileAsync(
        string accessToken,
        string appSecret,
        CancellationToken cancellationToken)
    {
        try
        {
            var proof = Convert.ToHexString(
                HMACSHA256.HashData(
                    Encoding.UTF8.GetBytes(appSecret),
                    Encoding.UTF8.GetBytes(accessToken)))
                .ToLowerInvariant();

            var url =
                "https://graph.facebook.com/v19.0/me" +
                "?fields=id,first_name,last_name,name,email" +
                $"&access_token={Uri.EscapeDataString(accessToken)}" +
                $"&appsecret_proof={proof}";

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<FacebookProfile>(
                stream,
                cancellationToken: cancellationToken);
        }
        catch
        {
            return null;
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

    private sealed record FacebookProfile(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("first_name")] string? FirstName,
        [property: JsonPropertyName("last_name")] string? LastName,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("email")] string? Email);
}
