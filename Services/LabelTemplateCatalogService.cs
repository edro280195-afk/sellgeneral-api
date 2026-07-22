using EntregasApi.Data;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

/// <summary>Resuelve la plantilla publicada y crea el diseño inicial de bolsas al primer uso.</summary>
public sealed class LabelTemplateCatalogService(AppDbContext db)
{
    public Task<LabelTemplate?> GetPublishedDefaultAsync(
        LabelTemplateKind kind,
        LabelMediaSize mediaSize,
        CancellationToken cancellationToken) =>
        db.LabelTemplates
            .Include(template => template.PublishedVersion)
            .FirstOrDefaultAsync(template =>
                template.Kind == kind &&
                template.MediaSize == mediaSize &&
                template.IsDefault &&
                !template.IsArchived &&
                template.PublishedVersionId != null,
                cancellationToken);

    public async Task<LabelTemplate> GetOrCreateDefaultTemplateAsync(
        LabelTemplateKind kind,
        LabelMediaSize mediaSize,
        string requestedBy,
        CancellationToken cancellationToken)
    {
        var existing = await GetPublishedDefaultAsync(kind, mediaSize, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var now = DateTime.UtcNow;
        var design = LabelTemplateDesignFactory.CreateDefaultDesign(kind, mediaSize);
        var published = new LabelTemplateVersion
        {
            VersionNumber = 1,
            Status = LabelTemplateVersionStatus.Published,
            DesignJson = design,
            CreatedBy = requestedBy,
            PublishedBy = requestedBy,
            CreatedAt = now,
            PublishedAt = now
        };
        var draft = new LabelTemplateVersion
        {
            VersionNumber = 2,
            Status = LabelTemplateVersionStatus.Draft,
            DesignJson = design,
            CreatedBy = requestedBy,
            CreatedAt = now
        };
        var template = new LabelTemplate
        {
            Name = mediaSize == LabelMediaSize.Shipping4x6 ? "Bolsa 4 × 6 inicial" : "Bolsa cuadrada inicial",
            Description = "Plantilla inicial de bolsas, personalizable por la tienda.",
            Kind = kind,
            MediaSize = mediaSize,
            IsDefault = true,
            CreatedBy = requestedBy,
            UpdatedBy = requestedBy,
            CreatedAt = now,
            UpdatedAt = now,
            Versions = [published, draft]
        };
        template.Name = DefaultName(kind, mediaSize);
        template.Description = DefaultDescription(kind);
        db.LabelTemplates.Add(template);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            template.PublishedVersionId = published.Id;
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            return template;
        }
        catch (DbUpdateException)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            db.Entry(template).State = EntityState.Detached;
            db.Entry(published).State = EntityState.Detached;
            db.Entry(draft).State = EntityState.Detached;
            var winner = await GetPublishedDefaultAsync(kind, mediaSize, cancellationToken);
            if (winner is not null)
            {
                return winner;
            }
            throw;
        }
    }

    public Task<LabelTemplate> GetOrCreateDefaultOrderPackageTemplateAsync(
        LabelMediaSize mediaSize,
        string requestedBy,
        CancellationToken cancellationToken) =>
        GetOrCreateDefaultTemplateAsync(LabelTemplateKind.OrderPackage, mediaSize, requestedBy, cancellationToken);

    private static string DefaultName(LabelTemplateKind kind, LabelMediaSize mediaSize) => kind switch
    {
        LabelTemplateKind.InventoryBox => "Caja de bodega inicial",
        LabelTemplateKind.InventoryItem => "Artículo de bodega inicial",
        LabelTemplateKind.OrderPackage when mediaSize == LabelMediaSize.Shipping4x6 => "Bolsa 4 × 6 inicial",
        LabelTemplateKind.OrderPackage => "Bolsa cuadrada inicial",
        _ => "Etiqueta inicial"
    };

    private static string DefaultDescription(LabelTemplateKind kind) => kind switch
    {
        LabelTemplateKind.InventoryBox => "Plantilla NFC de cajas, personalizable por la tienda.",
        LabelTemplateKind.InventoryItem => "Plantilla de artículos de bodega, personalizable por la tienda.",
        _ => "Plantilla inicial de bolsas, personalizable por la tienda."
    };
}
