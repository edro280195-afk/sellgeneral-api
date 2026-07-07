using Microsoft.EntityFrameworkCore;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.DataProtection;
using System.Linq.Expressions;
using System.Security.Cryptography;

namespace EntregasApi.Data;

public class AppDbContext : DbContext
{
    private const int DefaultBusinessId = 1;
    private readonly ICurrentTenant? _tenant;
    private readonly IDataProtector? _mercadoPagoTokenProtector;
    private readonly IDataProtector? _payoutAccountProtector;

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public AppDbContext(
        DbContextOptions<AppDbContext> options,
        ICurrentTenant tenant,
        IDataProtectionProvider dataProtectionProvider) : base(options)
    {
        _tenant = tenant;
        _mercadoPagoTokenProtector = dataProtectionProvider.CreateProtector("EntregasApi.Business.MercadoPagoAccessToken");
        _payoutAccountProtector = dataProtectionProvider.CreateProtector("EntregasApi.PayoutAccount.AccountNumber");
    }

    public int ActiveBusinessId => _tenant?.ActiveBusinessId ?? DefaultBusinessId;

    // Tablas existentes
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Business> Businesses => Set<Business>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<DeliveryRoute> DeliveryRoutes => Set<DeliveryRoute>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<DeliveryEvidence> DeliveryEvidences => Set<DeliveryEvidence>();
    public DbSet<AppSettings> AppSettings => Set<AppSettings>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Investment> Investments => Set<Investment>();
    public DbSet<DriverExpense> DriverExpenses => Set<DriverExpense>();

    // --- NUEVAS TABLAS ---
    public DbSet<CashRegisterSession> CashRegisterSessions => Set<CashRegisterSession>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<TandaProduct> TandaProducts => Set<TandaProduct>();
    public DbSet<Tanda> Tandas => Set<Tanda>();
    public DbSet<TandaParticipant> TandaParticipants => Set<TandaParticipant>();
    public DbSet<TandaPayment> TandaPayments => Set<TandaPayment>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<LoyaltyTransaction> LoyaltyTransactions => Set<LoyaltyTransaction>();
    public DbSet<LoyaltyReward> LoyaltyRewards => Set<LoyaltyReward>();
    public DbSet<PushSubscriptionModel> PushSubscriptions => Set<PushSubscriptionModel>();
    public DbSet<OrderPayment> OrderPayments => Set<OrderPayment>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<PayoutAccount> PayoutAccounts => Set<PayoutAccount>();
    public DbSet<SalesPeriod> SalesPeriods => Set<SalesPeriod>();
    public DbSet<OrderPackage> OrderPackages => Set<OrderPackage>();
    public DbSet<FcmToken> FcmTokens => Set<FcmToken>();
    public DbSet<OrderRating> OrderRatings => Set<OrderRating>();

    // Sorteos
    public DbSet<Raffle> Raffles => Set<Raffle>();
    public DbSet<RaffleParticipant> RaffleParticipants => Set<RaffleParticipant>();
    public DbSet<RaffleEntry> RaffleEntries => Set<RaffleEntry>();
    public DbSet<RaffleDraw> RaffleDraws => Set<RaffleDraw>();

    // Identidad multi-señal de clientas
    public DbSet<ClientAlias> ClientAliases => Set<ClientAlias>();
    public DbSet<ClientMergeAudit> ClientMergeAudits => Set<ClientMergeAudit>();
    public DbSet<ClientClaimAudit> ClientClaimAudits => Set<ClientClaimAudit>();

    // Live Capture pipeline
    public DbSet<LiveSession> LiveSessions => Set<LiveSession>();
    public DbSet<LiveProduct> LiveProducts => Set<LiveProduct>();
    public DbSet<LiveSpokenOrder> LiveSpokenOrders => Set<LiveSpokenOrder>();
    public DbSet<LiveCommentOrder> LiveCommentOrders => Set<LiveCommentOrder>();
    public DbSet<LiveCandidate> LiveCandidates => Set<LiveCandidate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasIndex(a => a.Phone)
                  .IsUnique()
                  .HasFilter("\"Phone\" IS NOT NULL");

            entity.HasIndex(a => a.FacebookUserId)
                  .IsUnique()
                  .HasFilter("\"FacebookUserId\" IS NOT NULL");

            entity.HasIndex(a => a.Email)
                  .IsUnique()
                  .HasFilter("\"Email\" IS NOT NULL");

            entity.Property(a => a.CreatedAt)
                  .HasDefaultValueSql("NOW()");

            entity.ToTable(t => t.HasCheckConstraint(
                "CK_Accounts_IdentityMethod",
                "\"Phone\" IS NOT NULL OR \"FacebookUserId\" IS NOT NULL OR \"Email\" IS NOT NULL"));
        });

        modelBuilder.Entity<Business>(entity =>
        {
            entity.HasIndex(b => b.Slug).IsUnique();

            entity.Property(b => b.PlanTier)
                  .HasDefaultValue("Entrada");

            entity.Property(b => b.PendingPlanTier)
                  .HasMaxLength(40);

            entity.Property(b => b.SubscriptionStatus)
                  .HasConversion<string>()
                  .HasMaxLength(40)
                  .HasDefaultValue(SubscriptionStatus.Active);

            entity.Property(b => b.IsActive)
                  .HasDefaultValue(true);

            entity.Property(b => b.SubscriptionPeriodMonths)
                  .HasDefaultValue(1);

            entity.Property(b => b.CreatedAt)
                  .HasDefaultValueSql("NOW()");

            entity.Property(b => b.GeocodingRegion)
                  .HasDefaultValue("Nuevo Laredo, Tamaulipas, MX");

            entity.Property(b => b.BrandPrimaryColor)
                  .HasDefaultValue("#6C4AE0");

            entity.Property(b => b.MercadoPagoAccessToken)
                  .HasConversion(
                      token => ProtectMercadoPagoToken(token),
                      token => UnprotectMercadoPagoToken(token));
        });

        modelBuilder.Entity<Membership>(entity =>
        {
            entity.HasIndex(m => new { m.AccountId, m.BusinessId })
                  .IsUnique();

            entity.Property(m => m.CreatedAt)
                  .HasDefaultValueSql("NOW()");

            entity.HasOne(m => m.Account)
                  .WithMany(a => a.Memberships)
                  .HasForeignKey(m => m.AccountId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(m => m.Business)
                  .WithMany(b => b.Memberships)
                  .HasForeignKey(m => m.BusinessId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasIndex(t => t.TokenHash).IsUnique();
            entity.HasIndex(t => t.AccountId);
            entity.Property(t => t.CreatedAt).HasDefaultValueSql("NOW()");
            entity.HasOne(t => t.Account)
                  .WithMany()
                  .HasForeignKey(t => t.AccountId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Order>()
            .HasIndex(o => o.AccessToken)
            .IsUnique();

        modelBuilder.Entity<DeliveryRoute>()
            .HasIndex(r => r.DriverToken)
            .IsUnique();

        modelBuilder.Entity<Client>()
            .HasIndex(c => new { c.BusinessId, c.Name })
            .IsUnique();

        // Identidad multi-señal: campos normalizados + alias
        modelBuilder.Entity<Client>(entity =>
        {
            entity.Property(c => c.NormalizedName)
                  .IsRequired()
                  .HasDefaultValue(string.Empty);

            entity.HasIndex(c => c.NormalizedPhone)
                  .HasDatabaseName("IX_Clients_NormalizedPhone");

            entity.HasMany(c => c.Aliases)
                  .WithOne(a => a.Client!)
                  .HasForeignKey(a => a.ClientId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.Account)
                  .WithMany()
                  .HasForeignKey(c => c.AccountId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(c => c.AccountId)
                  .HasDatabaseName("IX_Clients_AccountId");
        });

        modelBuilder.Entity<ClientAlias>(entity =>
        {
            entity.HasIndex(a => new { a.BusinessId, a.NormalizedAlias })
                  .IsUnique()
                  .HasDatabaseName("IX_ClientAliases_NormalizedAlias");

            entity.HasIndex(a => a.ClientId)
                  .HasDatabaseName("IX_ClientAliases_ClientId");
        });

        modelBuilder.Entity<ClientClaimAudit>(entity =>
        {
            entity.HasIndex(a => new { a.AccountId, a.ClientId })
                  .IsUnique()
                  .HasDatabaseName("IX_ClientClaimAudits_Account_Client");

            entity.HasIndex(a => a.AccountId)
                  .HasDatabaseName("IX_ClientClaimAudits_AccountId");

            entity.HasIndex(a => a.ClientId)
                  .HasDatabaseName("IX_ClientClaimAudits_ClientId");

            entity.Property(a => a.ClaimedAt)
                  .HasDefaultValueSql("NOW()");
        });

        // Live Capture pipeline
        modelBuilder.Entity<LiveCandidate>()
            .HasOne(c => c.LiveSession)
            .WithMany(s => s.Candidates)
            .HasForeignKey(c => c.LiveSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<LiveCandidate>()
            .HasOne(c => c.LiveProduct)
            .WithMany(p => p.Candidates)
            .HasForeignKey(c => c.LiveProductId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<TandaParticipant>()
            .HasIndex(tp => new { tp.TandaId, tp.AssignedTurn })
            .IsUnique()
            .HasDatabaseName("IX_TandaParticipant_Tanda_Turn");

        // --- RELACIONES & CONFIGURACIONES ---

        // One-to-one: Order -> Delivery (OrderId es nullable para soportar deliveries de tanda)
        modelBuilder.Entity<Order>()
            .HasOne(o => o.Delivery)
            .WithOne(d => d.Order)
            .HasForeignKey<Delivery>(d => d.OrderId)
            .IsRequired(false);

        // Delivery -> TandaParticipant (many-to-one opcional; XOR con OrderId)
        modelBuilder.Entity<Delivery>(entity =>
        {
            entity.HasOne(d => d.TandaParticipant)
                  .WithMany()
                  .HasForeignKey(d => d.TandaParticipantId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(d => d.TandaParticipantId);

            // Garantiza el XOR: exactamente uno de OrderId / TandaParticipantId debe estar presente.
            entity.ToTable(t => t.HasCheckConstraint(
                "CK_Deliveries_OrderXorTanda",
                "(\"OrderId\" IS NOT NULL AND \"TandaParticipantId\" IS NULL) OR " +
                "(\"OrderId\" IS NULL AND \"TandaParticipantId\" IS NOT NULL)"));
        });

        // Order -> Payments (1:N)
        modelBuilder.Entity<Order>()
            .HasMany(o => o.Payments)
            .WithOne(p => p.Order)
            .HasForeignKey(p => p.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<OrderPayment>(entity =>
        {
            entity.HasIndex(p => p.OrderId);
            entity.HasIndex(p => p.Date);

            entity.HasOne(p => p.CashRegisterSession)
                  .WithMany(s => s.Payments)
                  .HasForeignKey(p => p.CashRegisterSessionId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PayoutAccount>(entity =>
        {
            entity.ToTable("PayoutAccounts");

            entity.Property(p => p.Kind)
                  .HasConversion<string>()
                  .HasMaxLength(30);

            entity.Property(p => p.AccountNumber)
                  .HasMaxLength(700)
                  .HasConversion(
                      number => ProtectPayoutAccountNumber(number),
                      number => UnprotectPayoutAccountNumber(number));

            entity.Property(p => p.IsActive)
                  .HasDefaultValue(true);

            entity.Property(p => p.CreatedAt)
                  .HasDefaultValueSql("NOW()");

            entity.Property(p => p.UpdatedAt)
                  .HasDefaultValueSql("NOW()");

            entity.HasIndex(p => new { p.BusinessId, p.IsActive, p.IsDefault })
                  .HasDatabaseName("IX_PayoutAccounts_Business_Default");
        });

        modelBuilder.Entity<CashRegisterSession>(entity =>
        {
            entity.HasOne(s => s.Account)
                  .WithMany()
                  .HasForeignKey(s => s.AccountId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Product>()
            .HasIndex(p => new { p.BusinessId, p.SKU })
            .IsUnique();
        // Order -> Packages (1:N)
        modelBuilder.Entity<Order>()
            .HasMany(o => o.Packages)
            .WithOne(p => p.Order)
            .HasForeignKey(p => p.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<OrderPackage>(entity =>
        {
            entity.HasIndex(p => p.QrCodeValue).IsUnique();
            entity.HasIndex(p => p.OrderId); // Útil cuando pidas "Todas las bolsas de la orden X"
        });

        // Proveedores e Inversiones
        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.HasIndex(s => s.Name);
            entity.HasMany(s => s.Investments)
                  .WithOne(i => i.Supplier)
                  .HasForeignKey(i => i.SupplierId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // SalesPeriods
        modelBuilder.Entity<SalesPeriod>(entity =>
        {
            entity.HasIndex(p => p.IsActive);

            entity.HasMany(p => p.Orders)
                  .WithOne(o => o.SalesPeriod)
                  .HasForeignKey(o => o.SalesPeriodId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(p => p.Investments)
                  .WithOne(i => i.SalesPeriod)
                  .HasForeignKey(i => i.SalesPeriodId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Investment>(entity =>
        {
            entity.HasIndex(i => i.SupplierId);
            entity.HasIndex(i => i.SalesPeriodId);
            entity.HasIndex(i => i.Date);
        });

        // Gastos de Chofer
        modelBuilder.Entity<DriverExpense>(entity =>
        {
            entity.HasIndex(e => e.DeliveryRouteId);
            entity.HasIndex(e => e.Date);
            entity.HasOne(e => e.DeliveryRoute)
                  .WithMany()
                  .HasForeignKey(e => e.DeliveryRouteId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Chat
        modelBuilder.Entity<ChatMessage>(entity =>
        {
            entity.HasOne(m => m.DeliveryRoute)
                  .WithMany(r => r.ChatMessages)
                  .HasForeignKey(m => m.DeliveryRouteId)
                  .OnDelete(DeleteBehavior.SetNull); // Keep messages if route is deleted

            entity.HasOne(m => m.Delivery)
                  .WithMany()
                  .HasForeignKey(m => m.DeliveryId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Loyalty (Puntos)
        modelBuilder.Entity<LoyaltyTransaction>()
            .HasOne(t => t.Client)
            .WithMany() // Si quieres lista en Client, agrégala allá, si no déjalo así
            .HasForeignKey(t => t.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<AppSettings>()
            .HasIndex(s => s.BusinessId)
            .IsUnique();

        modelBuilder.Entity<FcmToken>(entity =>
        {
            entity.ToTable("FcmTokens");
            entity.HasIndex(e => e.Token).IsUnique();
            entity.HasIndex(e => e.Role);
            entity.HasIndex(e => e.DriverRouteToken);
            entity.Property(e => e.Role).HasDefaultValue("driver");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("NOW()");
        });

        modelBuilder.Entity<PushSubscriptionModel>(entity =>
        {
            entity.ToTable("PushSubscriptions");

            entity.HasIndex(e => e.Endpoint)
                  .IsUnique();

            entity.HasIndex(e => new { e.Role, e.ClientId })
                  .HasDatabaseName("IX_PushSub_Role_ClientId");

            entity.HasIndex(e => new { e.Role, e.DriverRouteToken })
                  .HasDatabaseName("IX_PushSub_Role_DriverToken");

            entity.Property(e => e.Role)
                  .HasDefaultValue("client");

            entity.Property(e => e.CreatedAt)
                  .HasDefaultValueSql("NOW()");
        });

        // Sorteos
        modelBuilder.Entity<Raffle>(entity =>
        {
            entity.HasIndex(r => r.Status);
            entity.HasIndex(r => r.RaffleDate);
            entity.HasIndex(r => r.TandaId);

            entity.HasOne(r => r.Winner)
                  .WithMany()
                  .HasForeignKey(r => r.WinnerId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(r => r.Tanda)
                  .WithMany()
                  .HasForeignKey(r => r.TandaId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(r => r.PrizeProduct)
                  .WithMany()
                  .HasForeignKey(r => r.PrizeProductId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(r => r.Participants)
                  .WithOne(p => p.Raffle)
                  .HasForeignKey(p => p.RaffleId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(r => r.Entries)
                  .WithOne(e => e.Raffle)
                  .HasForeignKey(e => e.RaffleId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(r => r.Draws)
                  .WithOne(d => d.Raffle)
                  .HasForeignKey(d => d.RaffleId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RaffleParticipant>(entity =>
        {
            entity.HasIndex(p => new { p.RaffleId, p.ClientId })
                  .IsUnique()
                  .HasDatabaseName("IX_RaffleParticipant_Raffle_Client");

            entity.HasOne(p => p.Client)
                  .WithMany()
                  .HasForeignKey(p => p.ClientId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RaffleEntry>(entity =>
        {
            entity.HasIndex(e => e.OrderId);
            entity.HasIndex(e => new { e.RaffleId, e.ClientId });

            entity.HasOne(e => e.Client)
                  .WithMany()
                  .HasForeignKey(e => e.ClientId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Order)
                  .WithMany()
                  .HasForeignKey(e => e.OrderId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RaffleDraw>(entity =>
        {
            entity.HasIndex(d => d.RaffleId);
            entity.HasIndex(d => d.DrawDate);

            entity.HasOne(d => d.Winner)
                  .WithMany()
                  .HasForeignKey(d => d.WinnerId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        ApplyTenantOwnership(modelBuilder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTenantOwnedEntities();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        StampTenantOwnedEntities();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ApplyTenantOwnership(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType)))
        {
            var entity = modelBuilder.Entity(entityType.ClrType);

            entity.Property(nameof(ITenantOwned.BusinessId))
                  .IsRequired()
                  .HasDefaultValue(DefaultBusinessId);

            entity.HasIndex(nameof(ITenantOwned.BusinessId));

            entity.HasOne(typeof(Business), navigationName: null)
                  .WithMany()
                  .HasForeignKey(nameof(ITenantOwned.BusinessId))
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasQueryFilter(BuildTenantFilter(entityType.ClrType));
        }
    }

    private LambdaExpression BuildTenantFilter(Type entityType)
    {
        var parameter = Expression.Parameter(entityType, "e");
        var businessId = Expression.Property(parameter, nameof(ITenantOwned.BusinessId));
        var activeBusinessId = Expression.Property(Expression.Constant(this), nameof(ActiveBusinessId));

        return Expression.Lambda(Expression.Equal(businessId, activeBusinessId), parameter);
    }

    private void StampTenantOwnedEntities()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State == EntityState.Added && entry.Entity.BusinessId == 0)
            {
                entry.Entity.BusinessId = ActiveBusinessId;
            }
        }
    }

    private string? ProtectMercadoPagoToken(string? token)
    {
        return string.IsNullOrWhiteSpace(token) || _mercadoPagoTokenProtector is null
            ? token
            : _mercadoPagoTokenProtector.Protect(token);
    }

    private string? UnprotectMercadoPagoToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || _mercadoPagoTokenProtector is null)
        {
            return token;
        }

        try
        {
            return _mercadoPagoTokenProtector.Unprotect(token);
        }
        catch (CryptographicException)
        {
            return token;
        }
    }

    private string ProtectPayoutAccountNumber(string number)
    {
        return string.IsNullOrWhiteSpace(number) || _payoutAccountProtector is null
            ? number
            : _payoutAccountProtector.Protect(number);
    }

    private string UnprotectPayoutAccountNumber(string number)
    {
        if (string.IsNullOrWhiteSpace(number) || _payoutAccountProtector is null)
        {
            return number;
        }

        try
        {
            return _payoutAccountProtector.Unprotect(number);
        }
        catch (CryptographicException)
        {
            return number;
        }
    }
}
