using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/onboarding")]
[Authorize(Policy = AuthorizationPolicies.AuthenticatedAccount)]
[SkipTenantResolution]
[BypassSubscriptionLock]
public sealed class OnboardingController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentAccount _currentAccount;
    private readonly TimeProvider _timeProvider;

    public OnboardingController(
        AppDbContext db,
        ICurrentAccount currentAccount,
        TimeProvider timeProvider)
    {
        _db = db;
        _currentAccount = currentAccount;
        _timeProvider = timeProvider;
    }

    [HttpPut("complete")]
    public async Task<ActionResult<AccountOnboardingDto>> Complete(
        [FromBody] CompleteOnboardingRequest request,
        CancellationToken cancellationToken)
    {
        if (_currentAccount.AccountId is not int accountId)
        {
            return Unauthorized(new { message = "La sesion no es valida." });
        }

        var account = await _db.Accounts
            .Include(candidate => candidate.Memberships)
            .FirstOrDefaultAsync(candidate => candidate.Id == accountId, cancellationToken);
        if (account is null)
        {
            return Unauthorized(new { message = "La sesion no es valida." });
        }

        var role = request.Role?.Trim().ToLowerInvariant();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        switch (role)
        {
            case "client":
                account.BuyerOnboardingCompletedAtUtc ??= now;
                break;
            case "seller":
                if (!account.Memberships.Any(membership =>
                    membership.Role is MembershipRole.Owner or MembershipRole.Admin))
                {
                    return Forbid();
                }

                account.SellerOnboardingCompletedAtUtc ??= now;
                break;
            default:
                return BadRequest(new { message = "El tipo de recorrido no es valido." });
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new AccountOnboardingDto(
            account.BuyerOnboardingCompletedAtUtc is not null,
            account.SellerOnboardingCompletedAtUtc is not null,
            account.PhoneVerifiedAt is not null));
    }
}
