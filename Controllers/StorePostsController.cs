using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers;

/// <summary>Lado vendedora: publicar/gestionar novedades de la tienda.</summary>
[ApiController]
[Route("api/business/posts")]
[Authorize(Policy = AuthorizationPolicies.Admin)]
public class StorePostsController : ControllerBase
{
    private const long MaxImageBytes = 2L * 1024 * 1024;
    private static readonly HashSet<string> AllowedImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/jpg", "image/webp",
    };

    private readonly IStorePostsService _service;
    private readonly ICloudinaryService _cloudinary;
    private readonly AppDbContext _db;
    private readonly ICurrentTenant _tenant;
    private readonly ILogger<StorePostsController> _logger;

    public StorePostsController(
        IStorePostsService service,
        ICloudinaryService cloudinary,
        AppDbContext db,
        ICurrentTenant tenant,
        ILogger<StorePostsController> logger)
    {
        _service = service;
        _cloudinary = cloudinary;
        _db = db;
        _tenant = tenant;
        _logger = logger;
    }

    [HttpPost]
    public async Task<ActionResult<StorePostDto>> Create(
        [FromBody] CreateStorePostRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
        {
            return BadRequest(new { message = "Escribe algo para publicar." });
        }

        try
        {
            var post = await _service.CreateAsync(request, cancellationToken);
            return Ok(post);
        }
        catch (StorePostVipNotAllowedException ex)
        {
            return StatusCode(StatusCodes.Status402PaymentRequired, new
            {
                error = "feature_locked",
                feature = "VipDrops",
                requiredPlan = "Pro",
                message = ex.Message,
            });
        }
    }

    [HttpGet]
    public async Task<ActionResult<List<StorePostDto>>> GetMine(
        [FromQuery] int page, [FromQuery] int pageSize, CancellationToken cancellationToken)
    {
        var posts = await _service.GetMineAsync(page == 0 ? 1 : page, pageSize == 0 ? 20 : pageSize, cancellationToken);
        return Ok(posts);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        try
        {
            await _service.DeleteAsync(id, cancellationToken);
            return NoContent();
        }
        catch (StorePostNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    /// <summary>Sube la foto de una novedad a Cloudinary (carpeta "{slug}/posts").</summary>
    [HttpPost("image")]
    [RequestSizeLimit(MaxImageBytes)]
    public async Task<ActionResult<object>> UploadImage(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { message = "No se recibio el archivo." });
        }
        if (file.Length > MaxImageBytes)
        {
            return BadRequest(new { message = "La imagen excede 2MB." });
        }
        if (!AllowedImageContentTypes.Contains(file.ContentType ?? string.Empty))
        {
            return BadRequest(new { message = "Tipo de archivo invalido. Solo png, jpg o webp." });
        }

        var businessExists = await _db.Businesses.AsNoTracking()
            .AnyAsync(b => b.Id == _tenant.ActiveBusinessId, cancellationToken);
        if (!businessExists)
        {
            return NotFound(new { message = "Negocio no encontrado." });
        }

        try
        {
            using var stream = file.OpenReadStream();
            var url = await _cloudinary.UploadAsync(stream, file.FileName, "posts");
            return Ok(new { url });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StorePosts] Fallo la subida de imagen para Business {Id}", _tenant.ActiveBusinessId);
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = "No pudimos subir la imagen. Intenta de nuevo.",
            });
        }
    }
}
