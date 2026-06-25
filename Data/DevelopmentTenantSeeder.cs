using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Data;

public static class DevelopmentTenantSeeder
{
    private const int DefaultBusinessId = 1;

    /// <summary>Contraseña fija de las cuentas de DESARROLLO (solo DEV; nunca producción).</summary>
    public const string DevPassword = "Dev12345!";

    public static async Task SeedAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        await EnsureBusinessAsync(db, cancellationToken);
        await BackfillTenantDataAsync(db, cancellationToken);
        await EnsureAppSettingsAsync(db, cancellationToken);
        await EnsureLoyaltyRewardsAsync(db, cancellationToken);
        await EnsureDevIdentityAsync(db, cancellationToken);
    }

    private static async Task EnsureBusinessAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var business = await db.Businesses.FirstOrDefaultAsync(b => b.Id == DefaultBusinessId, cancellationToken);
        if (business is not null)
        {
            business.PlanTier = "Elite";
            business.SubscriptionStatus = SubscriptionStatus.Active;
            // Marca real de Regi Bazar: rosa del canal FCM. Solo se siembra
            // en DEV si la columna esta en su default generico (asi no
            // pisamos una eleccion manual del owner en otras BD).
            if (business.BrandPrimaryColor == "#6C4AE0")
            {
                business.BrandPrimaryColor = "#FF0072";
            }
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        db.Businesses.Add(new Business
        {
            Id = DefaultBusinessId,
            Name = "Regi Bazar",
            Slug = "regibazar",
            City = "Nuevo Laredo",
            FrontendUrl = "https://regibazar.com",
            BrandPrimaryColor = "#FF0072",
            DepotLat = 27.4861,
            DepotLng = -99.5069,
            GeocodingRegion = "Nuevo Laredo, Tamaulipas, MX",
            GeminiBusinessName = "Regi Bazar",
            PlanTier = "Elite",
            SubscriptionStatus = SubscriptionStatus.Active,
            IsActive = true
        });

        await db.SaveChangesAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """
            SELECT setval(
                pg_get_serial_sequence('"Businesses"', 'Id'),
                GREATEST((SELECT COALESCE(MAX("Id"), 1) FROM "Businesses"), 1));
            """,
            cancellationToken);
    }

    private static async Task BackfillTenantDataAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await BackfillAsync<Client>(db, cancellationToken);
        await BackfillAsync<DeliveryRoute>(db, cancellationToken);
        await BackfillAsync<Product>(db, cancellationToken);
        await BackfillAsync<CashRegisterSession>(db, cancellationToken);
        await BackfillAsync<Supplier>(db, cancellationToken);
        await BackfillAsync<Investment>(db, cancellationToken);
        await BackfillAsync<SalesPeriod>(db, cancellationToken);
        await BackfillAsync<AppSettings>(db, cancellationToken);
        await BackfillAsync<TandaProduct>(db, cancellationToken);
        await BackfillAsync<Tanda>(db, cancellationToken);
        await BackfillAsync<Raffle>(db, cancellationToken);
        await BackfillAsync<LoyaltyReward>(db, cancellationToken);
        await BackfillAsync<LiveSession>(db, cancellationToken);
        await BackfillAsync<LiveProduct>(db, cancellationToken);
        await BackfillAsync<LiveSpokenOrder>(db, cancellationToken);
        await BackfillAsync<LiveCommentOrder>(db, cancellationToken);
        await BackfillAsync<LiveCandidate>(db, cancellationToken);
        await BackfillAsync<FcmToken>(db, cancellationToken);
        await BackfillAsync<PushSubscriptionModel>(db, cancellationToken);
        await BackfillAsync<Order>(db, cancellationToken);
        await BackfillAsync<OrderItem>(db, cancellationToken);
        await BackfillAsync<OrderPayment>(db, cancellationToken);
        await BackfillAsync<OrderPackage>(db, cancellationToken);
        await BackfillAsync<Delivery>(db, cancellationToken);
        await BackfillAsync<DeliveryEvidence>(db, cancellationToken);
        await BackfillAsync<DriverExpense>(db, cancellationToken);
        await BackfillAsync<ClientAlias>(db, cancellationToken);
        await BackfillAsync<LoyaltyTransaction>(db, cancellationToken);
        await BackfillAsync<TandaParticipant>(db, cancellationToken);
        await BackfillAsync<TandaPayment>(db, cancellationToken);
        await BackfillAsync<RaffleParticipant>(db, cancellationToken);
        await BackfillAsync<RaffleEntry>(db, cancellationToken);
        await BackfillAsync<RaffleDraw>(db, cancellationToken);
    }

    private static Task<int> BackfillAsync<TEntity>(
        AppDbContext db,
        CancellationToken cancellationToken) where TEntity : class, ITenantOwned
    {
        return db.Set<TEntity>()
            .IgnoreQueryFilters()
            .Where(entity => entity.BusinessId == 0)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(entity => entity.BusinessId, DefaultBusinessId),
                cancellationToken);
    }

    private static async Task EnsureAppSettingsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var exists = await db.AppSettings
            .IgnoreQueryFilters()
            .AnyAsync(settings => settings.BusinessId == DefaultBusinessId, cancellationToken);

        if (exists)
        {
            return;
        }

        db.AppSettings.Add(new AppSettings
        {
            BusinessId = DefaultBusinessId,
            DefaultShippingCost = 60m,
            LinkExpirationHours = 72
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Identidad de DESARROLLO para poder probar el sistema en una base virgen (todavía no hay
    /// onboarding self-serve; eso es Fase 1.2). Crea un segundo negocio demo (slug/región/depot/
    /// FrontendUrl distintos — útil para validar aislamiento multi-tenant y la parametrización del 0.5)
    /// y cuentas Owner/Driver/Scaner por negocio. SOLO corre en Development (SeedAsync solo se invoca ahí).
    /// </summary>
    private static async Task EnsureDevIdentityAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var demo = await db.Businesses.FirstOrDefaultAsync(b => b.Slug == "tienda-demo", cancellationToken);
        if (demo is null)
        {
            demo = new Business
            {
                Name = "Tienda Demo",
                Slug = "tienda-demo",
                City = "Monterrey",
                FrontendUrl = "http://localhost:4300",
                DepotLat = 25.6866,
                DepotLng = -100.3161,
                GeocodingRegion = "Monterrey, Nuevo Leon, MX",
                GeminiBusinessName = "Tienda Demo",
                PlanTier = "Entrada",
                SubscriptionStatus = SubscriptionStatus.Active,
                IsActive = true
            };
            db.Businesses.Add(demo);
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (demo.SubscriptionStatus != SubscriptionStatus.Active)
        {
            demo.SubscriptionStatus = SubscriptionStatus.Active;
            await db.SaveChangesAsync(cancellationToken);
        }

        await EnsureDevAccountAsync(db, "owner1@regibazar.dev", "Dev Owner RB", DefaultBusinessId, MembershipRole.Owner, cancellationToken);
        await EnsureDevAccountAsync(db, "driver1@regibazar.dev", "Dev Driver RB", DefaultBusinessId, MembershipRole.Driver, cancellationToken);
        await EnsureDevAccountAsync(db, "scaner1@regibazar.dev", "Dev Scaner RB", DefaultBusinessId, MembershipRole.Scaner, cancellationToken);
        await EnsureDevAccountAsync(db, "owner2@tiendademo.dev", "Dev Owner Demo", demo.Id, MembershipRole.Owner, cancellationToken);
    }

    private static async Task EnsureDevAccountAsync(
        AppDbContext db,
        string email,
        string displayName,
        int businessId,
        MembershipRole role,
        CancellationToken cancellationToken)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Email == email, cancellationToken);
        if (account is null)
        {
            account = new Account
            {
                DisplayName = displayName,
                Email = email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(DevPassword)
            };
            db.Accounts.Add(account);
            await db.SaveChangesAsync(cancellationToken);
        }

        var hasMembership = await db.Memberships.AnyAsync(
            m => m.AccountId == account.Id && m.BusinessId == businessId, cancellationToken);
        if (!hasMembership)
        {
            db.Memberships.Add(new Membership
            {
                AccountId = account.Id,
                BusinessId = businessId,
                Role = role
            });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task EnsureLoyaltyRewardsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var exists = await db.LoyaltyRewards
            .IgnoreQueryFilters()
            .AnyAsync(reward => reward.BusinessId == DefaultBusinessId, cancellationToken);

        if (exists)
        {
            return;
        }

        db.LoyaltyRewards.AddRange(
            new LoyaltyReward { BusinessId = DefaultBusinessId, Name = "$50 de descuento", Description = "Aplica $50 menos en tu proximo pedido.", PointsCost = 100, Type = LoyaltyRewardType.FixedDiscount, Value = 50m, Icon = "💸", SortOrder = 1 },
            new LoyaltyReward { BusinessId = DefaultBusinessId, Name = "Envio gratis", Description = "Te invitamos el envio de tu pedido.", PointsCost = 150, Type = LoyaltyRewardType.FreeShipping, Value = 0m, Icon = "🚚", SortOrder = 2 },
            new LoyaltyReward { BusinessId = DefaultBusinessId, Name = "$100 de descuento", Description = "Aplica $100 menos en tu proximo pedido.", PointsCost = 200, Type = LoyaltyRewardType.FixedDiscount, Value = 100m, Icon = "💰", SortOrder = 3 },
            new LoyaltyReward { BusinessId = DefaultBusinessId, Name = "Regalito sorpresa", Description = "Una sorpresita de Regi Bazar en tu pedido.", PointsCost = 300, Type = LoyaltyRewardType.Gift, Value = 0m, Icon = "🎁", SortOrder = 4 }
        );

        await db.SaveChangesAsync(cancellationToken);
    }
}
