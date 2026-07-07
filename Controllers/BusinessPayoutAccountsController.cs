using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/business/payout-accounts")]
[Authorize(Policy = AuthorizationPolicies.Admin)]
public class BusinessPayoutAccountsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _currentTenant;

    public BusinessPayoutAccountsController(
        AppDbContext db,
        ICurrentTenant currentTenant)
    {
        _db = db;
        _currentTenant = currentTenant;
    }

    [HttpGet]
    [BypassSubscriptionLock]
    public async Task<ActionResult<List<PayoutAccountDto>>> List(
        CancellationToken cancellationToken)
    {
        if (!HasBusiness())
        {
            return NotFound(new { message = "Negocio no encontrado." });
        }

        var accounts = await _db.PayoutAccounts.AsNoTracking()
            .Where(account => account.IsActive)
            .OrderByDescending(account => account.IsDefault)
            .ThenByDescending(account => account.UpdatedAt)
            .ToListAsync(cancellationToken);

        return Ok(accounts.Select(ToDto).ToList());
    }

    [HttpPost]
    [BypassSubscriptionLock]
    public async Task<ActionResult<PayoutAccountDto>> Create(
        [FromBody] CreatePayoutAccountRequest request,
        CancellationToken cancellationToken)
    {
        if (!HasBusiness())
        {
            return NotFound(new { message = "Negocio no encontrado." });
        }

        if (!TryParseKind(request.Kind, out var kind))
        {
            return BadRequest(new { message = "Selecciona un tipo de cuenta valido." });
        }

        if (!TryCleanRequired(request.HolderName, 120, "El titular", out var holderName, out var message))
        {
            return BadRequest(new { message });
        }

        if (!TryCleanOptional(request.BankName, 80, "El banco", out var bankName, out message) ||
            !TryCleanOptional(request.Alias, 80, "El alias", out var alias, out message) ||
            !TryCleanOptional(request.Notes, 300, "La nota", out var notes, out message))
        {
            return BadRequest(new { message });
        }

        if (!TryNormalizeAccountNumber(request.AccountNumber, kind, out var accountNumber, out message))
        {
            return BadRequest(new { message });
        }

        var hasAccounts = await _db.PayoutAccounts
            .AnyAsync(account => account.IsActive, cancellationToken);
        var shouldBeDefault = request.IsDefault || !hasAccounts;

        if (shouldBeDefault)
        {
            await ClearDefaultAsync(cancellationToken);
        }

        var now = DateTime.UtcNow;
        var account = new PayoutAccount
        {
            Kind = kind,
            HolderName = holderName,
            BankName = bankName,
            Alias = alias,
            AccountNumber = accountNumber,
            MaskedNumber = Mask(accountNumber),
            NumberLength = accountNumber.Length,
            Notes = notes,
            IsDefault = shouldBeDefault,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.PayoutAccounts.Add(account);
        await _db.SaveChangesAsync(cancellationToken);

        return Created($"/api/business/payout-accounts/{account.Id}", ToDto(account));
    }

    [HttpPut("{id:int}")]
    [BypassSubscriptionLock]
    public async Task<ActionResult<PayoutAccountDto>> Update(
        int id,
        [FromBody] UpdatePayoutAccountRequest request,
        CancellationToken cancellationToken)
    {
        if (!HasBusiness())
        {
            return NotFound(new { message = "Negocio no encontrado." });
        }

        var account = await _db.PayoutAccounts
            .FirstOrDefaultAsync(a => a.Id == id && a.IsActive, cancellationToken);
        if (account is null)
        {
            return NotFound(new { message = "Cuenta no encontrada." });
        }

        var message = string.Empty;
        var kind = account.Kind;
        if (request.Kind is not null && !TryParseKind(request.Kind, out kind))
        {
            return BadRequest(new { message = "Selecciona un tipo de cuenta valido." });
        }

        if (request.HolderName is not null)
        {
            if (!TryCleanRequired(request.HolderName, 120, "El titular", out var holderName, out message))
            {
                return BadRequest(new { message });
            }

            account.HolderName = holderName;
        }

        if (!TryCleanOptional(request.BankName, 80, "El banco", out var bankName, out message) ||
            !TryCleanOptional(request.Alias, 80, "El alias", out var alias, out message) ||
            !TryCleanOptional(request.Notes, 300, "La nota", out var notes, out message))
        {
            return BadRequest(new { message });
        }

        if (request.BankName is not null) account.BankName = bankName;
        if (request.Alias is not null) account.Alias = alias;
        if (request.Notes is not null) account.Notes = notes;

        var numberToValidate = account.AccountNumber;
        if (request.AccountNumber is not null)
        {
            if (!TryNormalizeAccountNumber(request.AccountNumber, kind, out numberToValidate, out message))
            {
                return BadRequest(new { message });
            }

            account.AccountNumber = numberToValidate;
            account.MaskedNumber = Mask(numberToValidate);
            account.NumberLength = numberToValidate.Length;
        }
        else if (!TryNormalizeAccountNumber(numberToValidate, kind, out _, out message))
        {
            return BadRequest(new
            {
                message = "Captura el numero de cuenta cuando cambies el tipo."
            });
        }

        account.Kind = kind;
        account.UpdatedAt = DateTime.UtcNow;

        if (request.IsDefault == true)
        {
            await ClearDefaultAsync(cancellationToken);
            account.IsDefault = true;
        }
        else if (request.IsDefault == false)
        {
            account.IsDefault = false;
        }

        await _db.SaveChangesAsync(cancellationToken);
        await EnsureDefaultAsync(cancellationToken);

        return Ok(ToDto(account));
    }

    [HttpDelete("{id:int}")]
    [BypassSubscriptionLock]
    public async Task<IActionResult> Delete(
        int id,
        CancellationToken cancellationToken)
    {
        if (!HasBusiness())
        {
            return NotFound(new { message = "Negocio no encontrado." });
        }

        var account = await _db.PayoutAccounts
            .FirstOrDefaultAsync(a => a.Id == id && a.IsActive, cancellationToken);
        if (account is null)
        {
            return NotFound(new { message = "Cuenta no encontrada." });
        }

        account.IsActive = false;
        account.IsDefault = false;
        account.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await EnsureDefaultAsync(cancellationToken);

        return NoContent();
    }

    private bool HasBusiness() => _currentTenant.ActiveBusinessId > 0;

    private async Task ClearDefaultAsync(CancellationToken cancellationToken)
    {
        var defaults = await _db.PayoutAccounts
            .Where(account => account.IsActive && account.IsDefault)
            .ToListAsync(cancellationToken);

        foreach (var account in defaults)
        {
            account.IsDefault = false;
            account.UpdatedAt = DateTime.UtcNow;
        }
    }

    private async Task EnsureDefaultAsync(CancellationToken cancellationToken)
    {
        var hasDefault = await _db.PayoutAccounts
            .AnyAsync(account => account.IsActive && account.IsDefault, cancellationToken);
        if (hasDefault)
        {
            return;
        }

        var next = await _db.PayoutAccounts
            .Where(account => account.IsActive)
            .OrderByDescending(account => account.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (next is null)
        {
            return;
        }

        next.IsDefault = true;
        next.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static PayoutAccountDto ToDto(PayoutAccount account)
    {
        return new PayoutAccountDto(
            account.Id,
            ToKindCode(account.Kind),
            ToKindLabel(account.Kind),
            account.HolderName,
            account.BankName,
            account.Alias,
            account.MaskedNumber,
            account.NumberLength,
            account.Notes,
            account.IsDefault,
            account.CreatedAt,
            account.UpdatedAt);
    }

    private static bool TryParseKind(string? value, out PayoutAccountKind kind)
    {
        kind = PayoutAccountKind.Clabe;
        var normalized = (value ?? string.Empty)
            .Trim()
            .Replace("-", string.Empty)
            .Replace("_", string.Empty)
            .Replace(" ", string.Empty)
            .ToLowerInvariant();

        switch (normalized)
        {
            case "clabe":
            case "clabeinterbancaria":
                kind = PayoutAccountKind.Clabe;
                return true;
            case "debitcard":
            case "tarjeta":
            case "tarjetadebito":
            case "tarjetadedebito":
                kind = PayoutAccountKind.DebitCard;
                return true;
            case "bankaccount":
            case "cuenta":
            case "cuentabancaria":
                kind = PayoutAccountKind.BankAccount;
                return true;
            case "phone":
            case "celular":
            case "telefono":
            case "phonespei":
            case "celularspei":
                kind = PayoutAccountKind.Phone;
                return true;
            default:
                return false;
        }
    }

    private static bool TryCleanRequired(
        string? value,
        int maxLength,
        string fieldName,
        out string clean,
        out string message)
    {
        clean = (value ?? string.Empty).Trim();
        message = string.Empty;

        if (string.IsNullOrWhiteSpace(clean))
        {
            message = $"{fieldName} es obligatorio.";
            return false;
        }

        if (clean.Length > maxLength)
        {
            message = $"{fieldName} no puede exceder {maxLength} caracteres.";
            return false;
        }

        return true;
    }

    private static bool TryCleanOptional(
        string? value,
        int maxLength,
        string fieldName,
        out string? clean,
        out string message)
    {
        clean = null;
        message = string.Empty;

        if (value is null)
        {
            return true;
        }

        clean = value.Trim();
        if (string.IsNullOrWhiteSpace(clean))
        {
            clean = null;
            return true;
        }

        if (clean.Length > maxLength)
        {
            message = $"{fieldName} no puede exceder {maxLength} caracteres.";
            return false;
        }

        return true;
    }

    private static bool TryNormalizeAccountNumber(
        string? value,
        PayoutAccountKind kind,
        out string accountNumber,
        out string message)
    {
        accountNumber = string.Empty;
        message = string.Empty;

        var raw = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            message = "Captura el numero de cuenta.";
            return false;
        }

        if (raw.Any(ch => !char.IsDigit(ch) && ch != ' ' && ch != '-'))
        {
            message = "El numero solo puede llevar digitos, espacios o guiones.";
            return false;
        }

        accountNumber = new string(raw.Where(char.IsDigit).ToArray());
        var expectedMessage = kind switch
        {
            PayoutAccountKind.Clabe => "La CLABE debe tener 18 digitos.",
            PayoutAccountKind.DebitCard => "La tarjeta de debito debe tener 16 digitos.",
            PayoutAccountKind.Phone => "El celular SPEI debe tener 10 digitos.",
            _ => "La cuenta bancaria debe tener entre 6 y 20 digitos."
        };

        var lengthOk = kind switch
        {
            PayoutAccountKind.Clabe => accountNumber.Length == 18,
            PayoutAccountKind.DebitCard => accountNumber.Length == 16,
            PayoutAccountKind.Phone => accountNumber.Length == 10,
            _ => accountNumber.Length is >= 6 and <= 20
        };

        if (!lengthOk)
        {
            message = expectedMessage;
            return false;
        }

        if (kind == PayoutAccountKind.Clabe && !IsValidClabe(accountNumber))
        {
            message = "La CLABE no parece valida. Revisa los 18 digitos.";
            return false;
        }

        return true;
    }

    private static bool IsValidClabe(string clabe)
    {
        var weights = new[] { 3, 7, 1 };
        var sum = 0;
        for (var i = 0; i < 17; i++)
        {
            var digit = clabe[i] - '0';
            sum += (digit * weights[i % 3]) % 10;
        }

        var expected = (10 - (sum % 10)) % 10;
        return expected == clabe[17] - '0';
    }

    private static string Mask(string accountNumber)
    {
        var last4 = accountNumber.Length <= 4
            ? accountNumber
            : accountNumber[^4..];
        return accountNumber.Length switch
        {
            10 => $"*** *** {last4}",
            16 => $"**** **** **** {last4}",
            18 => $"**** **** **** **{last4}",
            _ => $"**** {last4}"
        };
    }

    private static string ToKindCode(PayoutAccountKind kind) => kind switch
    {
        PayoutAccountKind.Clabe => "clabe",
        PayoutAccountKind.DebitCard => "debitCard",
        PayoutAccountKind.BankAccount => "bankAccount",
        PayoutAccountKind.Phone => "phone",
        _ => "clabe"
    };

    private static string ToKindLabel(PayoutAccountKind kind) => kind switch
    {
        PayoutAccountKind.Clabe => "CLABE",
        PayoutAccountKind.DebitCard => "Tarjeta de debito",
        PayoutAccountKind.BankAccount => "Cuenta bancaria",
        PayoutAccountKind.Phone => "Celular SPEI",
        _ => "CLABE"
    };
}
