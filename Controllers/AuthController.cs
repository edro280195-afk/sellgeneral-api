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

    public AuthController(
        AppDbContext db,
        ITokenService tokenService,
        IHostEnvironment env,
        IConfiguration config,
        IPhoneVerificationService phoneVerification)
    {
        _db = db;
        _tokenService = tokenService;
        _env = env;
        _config = config;
        _phoneVerification = phoneVerification;
    }

    /// <summary>
    /// En Development (o con Auth:DevOtpEnabled=true) se usa un código fijo.
    /// En producción el flujo se delega a Twilio Verify.
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

        var devMode = IsDevOtpEnabled;
        if (devMode)
        {
            return Accepted(new
            {
                phone,
                otpRequired = true,
                providerConfigured = _phoneVerification.IsConfigured,
                devMode = true,
                message = $"Modo DEV: usa el codigo {DevOtpCode} para entrar."
            });
        }

        if (!_phoneVerification.IsConfigured)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "otp_provider_not_configured",
                message = "El servicio de SMS aun no esta configurado."
            });
        }

        var outcome = await _phoneVerification.SendCodeAsync(phone, cancellationToken);
        if (outcome != PhoneVerificationOutcome.Sent)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "otp_send_failed",
                message = "No pudimos enviar el codigo por SMS. Intenta de nuevo."
            });
        }

        return Accepted(new
        {
            phone,
            otpRequired = true,
            providerConfigured = true,
            devMode = false,
            message = "Codigo enviado por SMS."
        });
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

        if (string.IsNullOrWhiteSpace(req.Code) ||
            req.Code.Length != 6 ||
            !req.Code.All(char.IsDigit))
        {
            return BadRequest(new
            {
                error = "invalid_code_format",
                message = "El codigo debe tener 6 digitos."
            });
        }

        if (IsDevOtpEnabled)
        {
            if (!string.Equals(req.Code.Trim(), DevOtpCode, StringComparison.Ordinal))
            {
                return Unauthorized(new
                {
                    error = "invalid_code",
                    message = "Codigo incorrecto."
                });
            }
        }
        else
        {
            if (!_phoneVerification.IsConfigured)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    error = "otp_provider_not_configured",
                    message = "El servicio de SMS aun no esta configurado."
                });
            }

            var outcome = await _phoneVerification.CheckCodeAsync(
                phone,
                req.Code.Trim(),
                cancellationToken);
            if (outcome == PhoneVerificationOutcome.Invalid)
            {
                return Unauthorized(new
                {
                    error = "invalid_code",
                    message = "Codigo incorrecto o expirado."
                });
            }

            if (outcome != PhoneVerificationOutcome.Approved)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    error = "otp_verification_failed",
                    message = "No pudimos validar el codigo. Intenta de nuevo."
                });
            }
        }

        var account = await _db.Accounts
            .Include(a => a.Memberships)
                .ThenInclude(m => m.Business)
            .FirstOrDefaultAsync(a => a.Phone == phone, cancellationToken);

        if (account is null)
        {
            account = new Account
            {
                DisplayName = "Clienta",
                Phone = phone
            };
            _db.Accounts.Add(account);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return Ok(BuildLoginResponse(account, account.Memberships));
    }

    [HttpPost("facebook")]
    public ActionResult FacebookLogin(FacebookLoginRequest req)
    {
        return StatusCode(StatusCodes.Status501NotImplemented, new
        {
            error = "facebook_provider_not_configured",
            message = "Facebook Login requiere validar el access token antes de enlazar una cuenta."
        });
    }

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

    private static string NormalizeEmail(string email)
    {
        return email.Trim().ToLowerInvariant();
    }
}
