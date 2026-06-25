using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace EntregasApi.Services;

/// <summary>
/// Expone la Account autenticada de la peticion. El JWT usa sub = AccountId.
/// </summary>
public interface ICurrentAccount
{
    int? AccountId { get; }
    bool IsAuthenticated { get; }
}

public class CurrentAccount : ICurrentAccount
{
    private readonly IHttpContextAccessor _http;

    public CurrentAccount(IHttpContextAccessor http) => _http = http;

    public int? AccountId
    {
        get
        {
            var user = _http.HttpContext?.User;
            var raw = user?.FindFirstValue("account_id")
                ?? user?.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? user?.FindFirstValue(JwtRegisteredClaimNames.Sub)
                ?? user?.FindFirstValue("sub");

            return int.TryParse(raw, out var id) ? id : null;
        }
    }

    public bool IsAuthenticated => _http.HttpContext?.User?.Identity?.IsAuthenticated ?? false;
}
