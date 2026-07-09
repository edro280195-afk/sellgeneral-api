using EntregasApi.Data;
using EntregasApi.Hubs;
using EntregasApi.Models;
using EntregasApi.Services;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using OfficeOpenXml;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// ── 1. Credenciales de Google Cloud (C.A.M.I. TTS) ──
// Usamos ContentRootPath para que la ruta sea dinámica y no rompa en el servidor Linux de producción
var camiCredPath = Path.Combine(builder.Environment.ContentRootPath, "cami-voz-v2.json");
if (File.Exists(camiCredPath))
{
    Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", camiCredPath);
    Console.WriteLine("✅ Credenciales de C.A.M.I. cargadas correctamente.");
}
else
{
    Console.WriteLine("⚠️ CUIDADO: No se encontró cami-voz-v2.json en la raíz.");
}

// ── 2. Firebase Admin SDK (FCM para Notificaciones Push Android) ──
try
{
    // Primero intentamos leer la ruta desde tu appsettings.json
    var firebaseCredPath = builder.Configuration["Firebase:ServiceAccountPath"];

    // Si no está configurada ahí, buscamos el archivo "firebase-adminsdk.json" directo en la raíz
    if (string.IsNullOrEmpty(firebaseCredPath))
    {
        firebaseCredPath = Path.Combine(builder.Environment.ContentRootPath, "firebase-adminsdk.json");
    }

    if (File.Exists(firebaseCredPath))
    {
        FirebaseApp.Create(new AppOptions
        {
            Credential = GoogleCredential.FromFile(firebaseCredPath)
        });
        Console.WriteLine("🔥 Motor de Firebase (Push) conectado con éxito.");
    }
    else
    {
        // Intentar con GOOGLE_APPLICATION_CREDENTIALS como fallback
        FirebaseApp.Create(new AppOptions
        {
            Credential = GoogleCredential.GetApplicationDefault()
        });
        Console.WriteLine("🔥 Motor de Firebase conectado (Fallback default).");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[Firebase] No se pudo inicializar Firebase Admin SDK: {ex.Message}");
    Console.WriteLine("[Firebase] Las notificaciones FCM estarán deshabilitadas.");
}

// EPPlus license (NonCommercial)
ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

// ── HTTP Client (Mercado Pago y otras llamadas externas) ──
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("facebook", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
}).RemoveAllLoggers();
builder.Services.AddHttpContextAccessor();
builder.Services.AddDataProtection();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.Configure<SmsOptions>(builder.Configuration.GetSection("Sms"));
builder.Services.AddHttpClient<IPhoneVerificationService, TwilioVerifyService>(client =>
{
    client.BaseAddress = new Uri("https://verify.twilio.com/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        FixedWindow(context, "global", 600, TimeSpan.FromMinutes(1)));
    options.AddPolicy("otp-send", context =>
        FixedWindow(context, "otp-send", 5, TimeSpan.FromMinutes(1), "phone"));
    options.AddPolicy("otp-check", context =>
        FixedWindow(context, "otp-check", 10, TimeSpan.FromMinutes(1), "phone"));
    options.AddPolicy("facebook-auth", context =>
        FixedWindow(context, "facebook-auth", 5, TimeSpan.FromMinutes(1)));
    options.AddPolicy(SecurityRateLimitPolicies.AuthPassword, context =>
        FixedWindow(context, SecurityRateLimitPolicies.AuthPassword, 8, TimeSpan.FromMinutes(5)));
    options.AddPolicy(SecurityRateLimitPolicies.AuthSession, context =>
        FixedWindow(context, SecurityRateLimitPolicies.AuthSession, 30, TimeSpan.FromMinutes(5)));
    options.AddPolicy(SecurityRateLimitPolicies.PublicTokenRead, context =>
        FixedWindow(context, SecurityRateLimitPolicies.PublicTokenRead, 120, TimeSpan.FromMinutes(1)));
    options.AddPolicy(SecurityRateLimitPolicies.PublicTokenWrite, context =>
        FixedWindow(context, SecurityRateLimitPolicies.PublicTokenWrite, 20, TimeSpan.FromMinutes(1)));
    options.AddPolicy(SecurityRateLimitPolicies.DriverTokenRead, context =>
        FixedWindow(context, SecurityRateLimitPolicies.DriverTokenRead, 120, TimeSpan.FromMinutes(1)));
    options.AddPolicy(SecurityRateLimitPolicies.DriverTokenWrite, context =>
        FixedWindow(context, SecurityRateLimitPolicies.DriverTokenWrite, 45, TimeSpan.FromMinutes(1)));
    options.AddPolicy(SecurityRateLimitPolicies.DriverTokenHighFrequency, context =>
        FixedWindow(context, SecurityRateLimitPolicies.DriverTokenHighFrequency, 180, TimeSpan.FromMinutes(1)));
    options.AddPolicy(SecurityRateLimitPolicies.PushSubscribe, context =>
        FixedWindow(context, SecurityRateLimitPolicies.PushSubscribe, 30, TimeSpan.FromMinutes(1)));
    options.AddPolicy(SecurityRateLimitPolicies.LinkEvents, context =>
        FixedWindow(context, SecurityRateLimitPolicies.LinkEvents, 120, TimeSpan.FromMinutes(1)));
    options.AddPolicy(SecurityRateLimitPolicies.Webhook, context =>
        FixedWindow(context, SecurityRateLimitPolicies.Webhook, 240, TimeSpan.FromMinutes(1)));
});

// ── Plataforma MP: suscripciones (Fase 1.3) ──
// Credenciales de PLATAFORMA (cobro de la suscripcion de la vendedora).
// Distintas de Business.MercadoPagoAccessToken (que cobra a las clientas).
builder.Services.Configure<MercadoPagoSubscriptionOptions>(builder.Configuration.GetSection("Platform:MercadoPago"));
builder.Services.AddScoped<IMercadoPagoSubscriptionService, MercadoPagoSubscriptionService>();

// ── Database ──
var connectionString = builder.Configuration.GetConnectionString("Default");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

// ── Authentication ──
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = JwtSigningKey.FromConfiguration(builder.Configuration)
        };

        // Permitir JWT via query string para SignalR
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .AddRequirements(new MembershipRequirement(MembershipRole.Owner, MembershipRole.Admin))
        .Build();

    options.AddPolicy(AuthorizationPolicies.AuthenticatedAccount, policy =>
        policy.RequireAuthenticatedUser());

    options.AddPolicy(AuthorizationPolicies.BusinessMember, policy =>
        policy.RequireAuthenticatedUser()
            .AddRequirements(new MembershipRequirement()));

    options.AddPolicy(AuthorizationPolicies.Owner, policy =>
        policy.RequireAuthenticatedUser()
            .AddRequirements(new MembershipRequirement(MembershipRole.Owner)));

    options.AddPolicy(AuthorizationPolicies.Admin, policy =>
        policy.RequireAuthenticatedUser()
            .AddRequirements(new MembershipRequirement(MembershipRole.Owner, MembershipRole.Admin)));

    options.AddPolicy(AuthorizationPolicies.Driver, policy =>
        policy.RequireAuthenticatedUser()
            .AddRequirements(new MembershipRequirement(MembershipRole.Driver)));

    options.AddPolicy(AuthorizationPolicies.Scaner, policy =>
        policy.RequireAuthenticatedUser()
            .AddRequirements(new MembershipRequirement(MembershipRole.Scaner)));

    options.AddPolicy(AuthorizationPolicies.PosAccess, policy =>
        policy.RequireAuthenticatedUser()
            .AddRequirements(new MembershipRequirement(MembershipRole.Owner, MembershipRole.Admin, MembershipRole.Scaner)));

    options.AddPolicy(AuthorizationPolicies.RoutesAccess, policy =>
        policy.RequireAuthenticatedUser()
            .AddRequirements(new MembershipRequirement(MembershipRole.Owner, MembershipRole.Admin, MembershipRole.Driver)));
});

// ── Services ──
builder.Services.AddScoped<ICurrentTenant, CurrentTenant>();
builder.Services.AddScoped<ICurrentBusiness, CurrentBusiness>();
builder.Services.AddScoped<ICurrentAccount, CurrentAccount>();
builder.Services.AddScoped<IAuthorizationHandler, MembershipAuthorizationHandler>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IRefreshTokenService, RefreshTokenService>();
builder.Services.AddScoped<IExcelService, ExcelService>();
builder.Services.AddScoped<ISuppliersService, SuppliersService>();
builder.Services.AddScoped<IExpenseService, ExpenseService>();
builder.Services.AddScoped<IPushNotificationService, PushNotificationService>();
// 🔥 Aquí está el oro que te decía. Ya tienes la inyección lista.
builder.Services.AddSingleton<IFcmService, FcmService>();
builder.Services.AddScoped<ISalesPeriodService, SalesPeriodService>();
builder.Services.AddScoped<IGeminiService, GeminiService>();
builder.Services.AddScoped<ICamiService, CamiService>();
builder.Services.AddScoped<IGoogleTtsService, GoogleTtsService>();
// ElevenLabs reemplaza a Google TTS como motor principal de CAMI. Google se
// mantiene registrado para que ElevenLabs lo use como fallback automático
// si la API key no está configurada o ElevenLabs falla.
builder.Services.AddScoped<IElevenLabsTtsService, ElevenLabsTtsService>();
builder.Services.AddScoped<IRouteOptimizerService, RouteOptimizerService>();
builder.Services.AddScoped<IGeocodingService, GeocodingService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IPosService, PosService>();
builder.Services.AddScoped<ITandaService, TandaService>();
builder.Services.AddScoped<IRaffleService, RaffleService>();
// Scoped (antes Singleton): ahora resuelve la carpeta de subida por tenant ({slug}/...)
// vía ICurrentBusiness, que es scoped.
builder.Services.AddScoped<ICloudinaryService, CloudinaryService>();
builder.Services.AddScoped<IClientResolverService, ClientResolverService>();
builder.Services.AddScoped<IClientClaimService, ClientClaimService>();
builder.Services.AddScoped<IBuyerFeedService, BuyerFeedService>();
builder.Services.AddScoped<IBuyerOrdersService, BuyerOrdersService>();
builder.Services.AddScoped<IBuyerRewardsService, BuyerRewardsService>();
builder.Services.AddScoped<IBuyerTandasService, BuyerTandasService>();
builder.Services.AddScoped<IBuyerRafflesService, BuyerRafflesService>();
builder.Services.AddScoped<IBuyerStoreService, BuyerStoreService>();
builder.Services.AddScoped<IBuyerReserveService, BuyerReserveService>();
builder.Services.AddScoped<IBuyerPaymentService, BuyerPaymentService>();
builder.Services.AddScoped<IBuyerAddressService, BuyerAddressService>();
builder.Services.AddScoped<IBuyerNotificationService, BuyerNotificationService>();
builder.Services.AddScoped<IBuyerFollowService, BuyerFollowService>();
builder.Services.AddScoped<IBuyerDeviceService, BuyerDeviceService>();
builder.Services.AddScoped<ILiveAnnouncementService, LiveAnnouncementService>();
builder.Services.AddScoped<IStorePostsService, StorePostsService>();
builder.Services.AddScoped<IBuyerFeedPostsService, BuyerFeedPostsService>();
builder.Services.AddScoped<IEntitlementService, EntitlementService>();

// ── SignalR ──
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 32 * 1024;
    options.StreamBufferCapacity = 10;
});

// ── CORS multi-tenant ──
// Los orígenes ya no están hardcodeados a regibazar.com: se aceptan los dominios
// registrados en cada Business.FrontendUrl (vía TenantCorsOriginStore, cacheado) más
// los orígenes fijos de desarrollo y Capacitor. corsOriginStore se asigna justo después
// de build(); el lambda de SetIsOriginAllowed solo se ejecuta en cada request (ya con
// el store resuelto), no durante el arranque.
builder.Services.AddSingleton<TenantCorsOriginStore>();
TenantCorsOriginStore? corsOriginStore = null;
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy
            .SetIsOriginAllowed(origin => corsOriginStore?.IsAllowed(origin) ?? false)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials(); // Necesario para SignalR
    });
});

// ── Controllers + Swagger ──
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Entregas API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        In = ParameterLocation.Header,
        Description = "JWT token",
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        BearerFormat = "JWT",
        Scheme = "bearer"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// El store de orígenes CORS se resuelve aquí; el lambda de la política lo usa por request.
corsOriginStore = app.Services.GetRequiredService<TenantCorsOriginStore>();

// Enlace corto compartible (dominio compartido) para /o/{token}. Es un valor
// global de despliegue; lo consume ExcelService.MapToSummary (estático).
ExcelService.ShareLinkBaseUrl = app.Configuration["App:ShareLinkBaseUrl"];

// ── Migrate DB on startup ──
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();

    if (app.Environment.IsDevelopment())
    {
        await DevelopmentTenantSeeder.SeedAsync(db);
    }

    // Backfill de los campos normalizados de Client para clientas existentes que se
    // crearon antes de la migración AddClientAliasesAndFuzzy. La normalización vive
    // en C# (sin diacríticos, lowercase, etc.) y replicarla en SQL puro dejaría
    // resultados distintos, así que se hace acá una sola vez.
    var pending = await db.Clients
        .Where(c => c.NormalizedName == "" || c.NormalizedName == null)
        .ToListAsync();
    if (pending.Count > 0)
    {
        Console.WriteLine($"⚙️  Backfill de NormalizedName/Phone/Address para {pending.Count} clientas...");
        foreach (var c in pending)
        {
            c.NormalizedName = TextNormalizer.NormalizeName(c.Name);
            c.NormalizedPhone = TextNormalizer.NormalizePhone(c.Phone);
            c.NormalizedAddress = TextNormalizer.NormalizeAddress(c.Address);
        }
        await db.SaveChangesAsync();
        Console.WriteLine("✅ Backfill de clientas completado.");
    }

    // Migración única: el "abono" legacy (Order.AdvancePayment) se mueve al libro de pagos
    // (OrderPayment) para que ese dinero cuadre en reportes/finanzas. Idempotente: una vez
    // migrado queda AdvancePayment = 0 y no se vuelve a procesar.
#pragma warning disable CS0618 // Uso intencional del campo legacy solo para migrarlo
    var ordersWithAdvance = await db.Orders.Where(o => o.AdvancePayment > 0).ToListAsync();
    if (ordersWithAdvance.Count > 0)
    {
        Console.WriteLine($"⚙️  Migrando abono legacy de {ordersWithAdvance.Count} pedidos al libro de pagos...");
        foreach (var o in ordersWithAdvance)
        {
            db.OrderPayments.Add(new EntregasApi.Models.OrderPayment
            {
                OrderId = o.Id,
                Amount = o.AdvancePayment,
                Method = "Abono",
                Date = o.CreatedAt,
                RegisteredBy = "Sistema",
                Notes = "Abono inicial (migrado del campo legacy)"
            });
            o.AdvancePayment = 0;
        }
        await db.SaveChangesAsync();
        Console.WriteLine("✅ Migración de abonos legacy completada.");
    }
#pragma warning restore CS0618

    // El catálogo de premios de RegiPuntos ya NO se siembra globalmente aquí: ahora es
    // por-tenant y lo crea DevelopmentTenantSeeder.EnsureLoyaltyRewardsAsync (con BusinessId)
    // solo en Development. En producción llega por el migrador / onboarding (Fase 1).
}

// ── Middleware pipeline ──
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.TryAdd("X-Content-Type-Options", "nosniff");
    headers.TryAdd("X-Frame-Options", "DENY");
    headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
    headers.TryAdd("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
    headers.TryAdd("Content-Security-Policy", "frame-ancestors 'none'; base-uri 'self'; object-src 'none'");
    await next();
});

// Storage local SOLO en DESARROLLO (cuando las credenciales de Cloudinary son
// "dummy" o se fuerza Storage:UseLocal=true). Las imagenes servidas viven
// en wwwroot/uploads/{slug}/{folder}/{filename} y se exponen en /uploads/*.
// En cualquier otro entorno NO se monta: las imagenes van a Cloudinary.
var useLocalStorage = app.Configuration.GetValue<bool>("Storage:UseLocal")
    || app.Environment.IsDevelopment() && IsCloudinaryDummy(app.Configuration);
if (useLocalStorage)
{
    var uploadsPath = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "uploads");
    Directory.CreateDirectory(uploadsPath);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(uploadsPath),
        RequestPath = "/uploads",
        ServeUnknownFileTypes = true,
        DefaultContentType = "application/octet-stream"
    });
    app.Logger.LogInformation("[Storage] Sirviendo uploads locales desde {Path} en /uploads", uploadsPath);
}

// 1. Primero enrutar
app.UseRouting();
app.UseRateLimiter();

// 2. LUEGO aplicar la política de CORS
app.UseCors("AllowAll");

static bool IsCloudinaryDummy(IConfiguration config)
{
    var section = config.GetSection("Cloudinary");
    var name = section["CloudName"];
    var key = section["ApiKey"];
    var secret = section["ApiSecret"];
    return string.IsNullOrWhiteSpace(name) || name == "dummy"
        || string.IsNullOrWhiteSpace(key) || key == "dummy"
        || string.IsNullOrWhiteSpace(secret) || secret == "dummy";
}

static RateLimitPartition<string> FixedWindow(
    HttpContext context,
    string policy,
    int permitLimit,
    TimeSpan window,
    params string[] routeValueKeys)
{
    return RateLimitPartition.GetFixedWindowLimiter(
        RateLimitPartitionKey(context, policy, routeValueKeys),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
            AutoReplenishment = true
        });
}

static string RateLimitPartitionKey(HttpContext context, string policy, params string[] routeValueKeys)
{
    var ip = ClientIp(context);
    var routeParts = routeValueKeys
        .Select(key => context.Request.RouteValues.TryGetValue(key, out var raw)
            ? Convert.ToString(raw)?.Trim()
            : null)
        .Where(value => !string.IsNullOrWhiteSpace(value));
    var route = string.Join(':', routeParts);
    return string.IsNullOrWhiteSpace(route)
        ? $"{policy}:{ip}"
        : $"{policy}:{ip}:{route}";
}

static string ClientIp(HttpContext context)
{
    return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

// 4. Autenticación y Autorización
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthorization();
app.UseMiddleware<SubscriptionLockMiddleware>();

// 5. Mapear endpoints
app.MapControllers();
app.MapHub<DeliveryHub>("/hubs/delivery");
app.MapHub<TrackingHub>("/hubs/tracking");
app.MapHub<OrderHub>("/hubs/orders");
app.MapHub<LogisticsHub>("/hubs/logistics");
app.MapHub<PosHub>("/hubs/pos");
app.MapHub<LiveHub>("/hubs/live");

app.Run();
