using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers;

/// <summary>
/// Lado vendedora: catálogo de productos activos de la tienda activa, para
/// selectores simples (ej. el picker de "anunciar producto en vivo" de
/// LiveHub). Tenant-scoped por el query filter automático de Product.
/// </summary>
[ApiController]
[Route("api/business/products")]
[Authorize(Policy = AuthorizationPolicies.Admin)]
public class SellerProductsController : ControllerBase
{
    private readonly AppDbContext _db;

    public SellerProductsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<List<SellerProductDto>>> GetProducts(CancellationToken cancellationToken)
    {
        var products = await _db.Products.AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .Select(p => new SellerProductDto(p.Id, p.Name, p.Price, p.Stock))
            .ToListAsync(cancellationToken);

        return Ok(products);
    }
}
