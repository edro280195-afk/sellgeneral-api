using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITokenService _tokenService;

    public AuthController(AppDbContext db, ITokenService tokenService)
    {
        _db = db;
        _tokenService = tokenService;
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

        return Accepted(new
        {
            phone,
            otpRequired = true,
            providerConfigured = false,
            message = "El proveedor SMS se conectara en una fase posterior."
        });
    }

    [HttpPost("phone/verify")]
    public ActionResult VerifyPhoneOtp(VerifyPhoneLoginRequest req)
    {
        return StatusCode(StatusCodes.Status501NotImplemented, new
        {
            error = "otp_provider_not_configured",
            message = "El login por telefono ya tiene contrato, pero aun no valida OTP."
        });
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
