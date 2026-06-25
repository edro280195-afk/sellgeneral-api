using CloudinaryDotNet;
using CloudinaryDotNet.Actions;

namespace EntregasApi.Services;

public class CloudinaryService : ICloudinaryService
{
    private readonly Cloudinary? _cloudinary;
    private readonly ICurrentBusiness _currentBusiness;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly IHttpContextAccessor _httpContext;
    private readonly ILogger<CloudinaryService> _logger;
    private readonly bool _useCloudinary;

    public CloudinaryService(
        IConfiguration config,
        ICurrentBusiness currentBusiness,
        IWebHostEnvironment env,
        IHttpContextAccessor httpContext,
        ILogger<CloudinaryService> logger)
    {
        _currentBusiness = currentBusiness;
        _config = config;
        _env = env;
        _httpContext = httpContext;
        _logger = logger;

        var section = config.GetSection("Cloudinary");
        var name = section["CloudName"];
        var key = section["ApiKey"];
        var secret = section["ApiSecret"];
        _useCloudinary = !string.IsNullOrWhiteSpace(name) && name != "dummy"
            && !string.IsNullOrWhiteSpace(key) && key != "dummy"
            && !string.IsNullOrWhiteSpace(secret) && secret != "dummy";

        if (_useCloudinary)
        {
            var account = new Account(name, key, secret);
            _cloudinary = new Cloudinary(account) { Api = { Secure = true } };
            _logger.LogInformation("[Storage] Usando Cloudinary para uploads.");
        }
        else
        {
            _logger.LogWarning(
                "[Storage] Cloudinary deshabilitado (credenciales dummy). " +
                "Los uploads se guardan localmente en wwwroot/uploads/{{slug}}/{{folder}} y se sirven en /uploads/*.");
        }
    }

    public async Task<string> UploadAsync(Stream stream, string fileName, string folder)
    {
        var business = await _currentBusiness.GetAsync();
        var rootFolder = string.IsNullOrWhiteSpace(business.Slug) ? "tenant" : business.Slug;

        if (!_useCloudinary)
        {
            return ToAbsoluteUrl(await SaveLocalAsync(stream, fileName, rootFolder, folder));
        }

        return await UploadCloudinaryAsync(stream, fileName, rootFolder, folder);
    }

    private string ToAbsoluteUrl(string path)
    {
        // Si la URL ya es absoluta (https://...) la dejamos igual.
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        // Storage local: devolvemos https://host:puerto/uploads/... para que el FE
        // pueda cargarla desde su propio dev server (localhost:4200) sin CORS raro.
        var ctx = _httpContext.HttpContext;
        if (ctx is null)
        {
            return path;
        }
        var req = ctx.Request;
        return $"{req.Scheme}://{req.Host}{path}";
    }

    private async Task<string> UploadCloudinaryAsync(
        Stream stream, string fileName, string rootFolder, string folder)
    {
        var publicId = $"{Path.GetFileNameWithoutExtension(fileName)}_{Guid.NewGuid():N}";

        var uploadParams = new ImageUploadParams
        {
            File = new FileDescription(fileName, stream),
            Folder = $"{rootFolder}/{folder}",
            PublicId = publicId,
            Overwrite = false,
            Transformation = new Transformation().Quality("auto").FetchFormat("auto")
        };

        var result = await _cloudinary!.UploadAsync(uploadParams);

        if (result.Error != null)
        {
            throw new Exception($"Cloudinary upload error: {result.Error.Message}");
        }

        return result.SecureUrl.ToString();
    }

    private async Task<string> SaveLocalAsync(
        Stream stream, string fileName, string rootFolder, string folder)
    {
        // Raíz de uploads: wwwroot/uploads/{slug}/{folder}/
        var webRoot = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        var targetDir = Path.Combine(webRoot, "uploads", rootFolder, folder);
        Directory.CreateDirectory(targetDir);

        // Nombre: timestamp + nombre sanitizado + extension (preserva la extension real).
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(ext) || ext.Length > 5)
        {
            ext = ".bin";
        }
        var safeName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        var fullPath = Path.Combine(targetDir, safeName);

        await using (var fs = File.Create(fullPath))
        {
            await stream.CopyToAsync(fs);
        }

        // URL pública servida por UseStaticFiles: /uploads/{slug}/{folder}/{file}
        var publicUrl = $"/uploads/{rootFolder}/{folder}/{safeName}";
        _logger.LogInformation("[Storage] Archivo guardado en disco: {Path} -> {Url}", fullPath, publicUrl);
        return publicUrl;
    }
}
