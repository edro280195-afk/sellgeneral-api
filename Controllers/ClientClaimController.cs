using EntregasApi.DTOs;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EntregasApi.Controllers;

/// <summary>
/// Endpoints de "reclamar perfil" (plan 0.3). Une la Account global de la clienta
/// con el/los Client que las vendedoras ya tienen de ella.
///
/// Todos los endpoints requieren autenticación (JWT con sub=AccountId). La
/// posesión de un Order.AccessToken o de un Account.Phone son las únicas
/// pruebas aceptadas para enlazar.
/// </summary>
[ApiController]
[Route("api/client-claims")]
[Authorize]
public class ClientClaimController : ControllerBase
{
    private readonly IClientClaimService _service;
    private readonly ICurrentAccount _currentAccount;

    public ClientClaimController(IClientClaimService service, ICurrentAccount currentAccount)
    {
        _service = service;
        _currentAccount = currentAccount;
    }

    /// <summary>
    /// POST /api/client-claims/by-order-token/{accessToken}
    /// Camino principal: la app se abrió desde el link de un pedido. La posesión
    /// del AccessToken (que la vendedora mandó por Messenger/SMS al cliente)
    /// es la prueba. Enlaza Client.AccountId = AccountId del JWT.
    /// </summary>
    [HttpPost("by-order-token/{accessToken}")]
    public async Task<ActionResult<ClientClaimResultDto>> ClaimByOrderToken(string accessToken)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        var outcome = await _service.ClaimByOrderTokenAsync(
            _currentAccount.AccountId.Value,
            accessToken);

        return ToActionResult(outcome);
    }

    /// <summary>
    /// GET /api/client-claims/candidates
    /// Camino secundario (fan-out por teléfono): devuelve los Client cross-tenant
    /// con el mismo NormalizedPhone que la Account, y que AÚN no han sido
    /// reclamados. La clienta elige uno a uno; NUNCA se auto-enlaza.
    /// </summary>
    [HttpGet("candidates")]
    public async Task<ActionResult<List<ClientClaimCandidateDto>>> Candidates()
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        var candidates = await _service.FindClaimCandidatesByPhoneAsync(
            _currentAccount.AccountId.Value);

        return Ok(candidates);
    }

    /// <summary>
    /// POST /api/client-claims/by-phone/{clientId}
    /// Reclama un Client específico elegido del fan-out por teléfono. La prueba
    /// es Account.NormalizedPhone == Client.NormalizedPhone.
    /// </summary>
    [HttpPost("by-phone/{clientId:int}")]
    public async Task<ActionResult<ClientClaimResultDto>> ClaimByPhone(int clientId)
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        var outcome = await _service.ClaimByPhoneMatchAsync(
            _currentAccount.AccountId.Value,
            clientId);

        return ToActionResult(outcome);
    }

    /// <summary>
    /// GET /api/client-claims/mine
    /// Lista los Client que esta Account ya tiene reclamados (cross-tenant).
    /// Solo identidad + nombre del negocio. NO expone teléfono, dirección ni
    /// pedidos de los otros negocios.
    /// </summary>
    [HttpGet("mine")]
    public async Task<ActionResult<List<ClaimedClientSummaryDto>>> Mine()
    {
        if (!_currentAccount.IsAuthenticated || _currentAccount.AccountId is null)
        {
            return Unauthorized(new { message = "Sesión inválida." });
        }

        var claimed = await _service.ListClaimedClientsAsync(
            _currentAccount.AccountId.Value);

        return Ok(claimed);
    }

    private ActionResult ToActionResult(ClaimOutcome outcome)
    {
        switch (outcome.Status)
        {
            case ClaimStatus.Linked:
                return Ok(outcome.Result);

            case ClaimStatus.AlreadyClaimedByOther:
                return StatusCode(StatusCodes.Status409Conflict, new
                {
                    error = "already_claimed_by_other",
                    message = outcome.Message
                });

            case ClaimStatus.NoProof:
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    error = "no_proof",
                    message = outcome.Message
                });

            case ClaimStatus.NotFound:
                return NotFound(new { message = outcome.Message });

            case ClaimStatus.Forbidden:
            default:
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    error = "forbidden",
                    message = outcome.Message
                });
        }
    }
}
