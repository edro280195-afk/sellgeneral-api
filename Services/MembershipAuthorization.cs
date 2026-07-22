using System.Security.Claims;
using EntregasApi.Data;
using EntregasApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public static class AuthorizationPolicies
{
    public const string AuthenticatedAccount = "AuthenticatedAccount";
    public const string BusinessMember = "BusinessMember";
    public const string Owner = "Owner";
    public const string Admin = "Admin";
    public const string Driver = "Driver";
    public const string Scaner = "Scaner";
    public const string PosAccess = "PosAccess";
    public const string RoutesAccess = "RoutesAccess";
    public const string InventoryAccess = "InventoryAccess";
}

public sealed class MembershipRequirement : IAuthorizationRequirement
{
    public MembershipRequirement(params MembershipRole[] allowedRoles)
    {
        AllowedRoles = allowedRoles.ToHashSet();
    }

    public IReadOnlySet<MembershipRole> AllowedRoles { get; }
}

public class MembershipAuthorizationHandler : AuthorizationHandler<MembershipRequirement>
{
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;

    public MembershipAuthorizationHandler(AppDbContext db, ICurrentTenant tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        MembershipRequirement requirement)
    {
        var accountId = ReadAccountId(context.User);
        if (accountId is null)
        {
            return;
        }

        var query = _db.Memberships
            .AsNoTracking()
            .Where(m => m.AccountId == accountId.Value && m.BusinessId == _tenant.ActiveBusinessId);

        if (requirement.AllowedRoles.Count > 0)
        {
            var allowedRoles = requirement.AllowedRoles.ToArray();
            query = query.Where(m => allowedRoles.Contains(m.Role));
        }

        if (await query.AnyAsync())
        {
            context.Succeed(requirement);
        }
    }

    private static int? ReadAccountId(ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue("account_id")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub");

        return int.TryParse(raw, out var accountId) ? accountId : null;
    }
}
