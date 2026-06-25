using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

public class LiveProduct : ITenantOwned
{
    public int Id { get; set; }

    /// <summary>Negocio (tenant) dueno de este producto detectado en live.</summary>
    public int BusinessId { get; set; }

    public int LiveSessionId { get; set; }
    public LiveSession? LiveSession { get; set; }

    [Required, MaxLength(100)]
    public string Keyword { get; set; } = "";

    [MaxLength(300)]
    public string? Description { get; set; }

    public decimal Price { get; set; }
    public double? AnnouncedAtSeconds { get; set; }

    public ICollection<LiveCandidate> Candidates { get; set; } = new List<LiveCandidate>();
}
