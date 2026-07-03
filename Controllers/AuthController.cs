using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
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

    public AuthController(
        AppDbContext db,
        ITokenService tokenService,
        IHostEnvironment env,
        IConfiguration config)
    {
        _db = db;
        _tokenService = tokenService;
        _env = env;
        _config = config;
    }

    /// <summary>
    /// El OTP por SMS aún no tiene proveedor. En Development (o con
    /// Auth:DevOtpEnabled=true) habilitamos un código fijo para poder
    /// construir y probar el flujo de la app. NUNCA se activa en producción
    /// salvo que se prenda el flag explícitamente.
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
    public ActionResult RequestPhoneOtp(PhoneLoginRequest req)
    {
        var phone = TextNormalizer.NormalizePhone(req.Phone);
        if (string.IsNullOrWhiteSpace(phone))
        {
            return BadRequest(new { message = "Telefono invalido." });
        }

        var devMode = IsDevOtpEnabled;
        return Accepted(new
        {
            phone,
            otpRequired = true,
            providerConfigured = false,
            devMode,
            message = devMode
                ? $"Modo DEV: usa el codigo {DevOtpCode} para entrar."
                : "El proveedor SMS se conectara en una fase posterior."
        });
    }

    [HttpPost("phone/verify")]
    public async Task<ActionResult<LoginResponse>> VerifyPhoneOtp(VerifyPhoneLoginRequest req)
    {
        var phone = TextNormalizer.NormalizePhone(req.Phone);
        if (string.IsNullOrWhiteSpace(phone))
        {
            return BadRequest(new { message = "Telefono invalido." });
        }

        if (!IsDevOtpEnabled)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new
            {
                error = "otp_provider_not_configured",
                message = "El login por telefono ya tiene contrato, pero aun no valida OTP."
            });
        }

        if (!string.Equals(req.Code?.Trim(), DevOtpCode, StringComparison.Ordinal))
        {
            return Unauthorized(new { error = "invalid_code", message = "Codigo incorrecto." });
        }

        var account = await _db.Accounts
            .Include(a => a.Memberships)
                .ThenInclude(m => m.Business)
            .FirstOrDefaultAsync(a => a.Phone == phone);

        if (account is null)
        {
            account = new Account
            {
                DisplayName = "Clienta",
                Phone = phone
            };
            _db.Accounts.Add(account);
            await _db.SaveChangesAsync();
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
