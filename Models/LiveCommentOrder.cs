using System.ComponentModel.DataAnnotations;

namespace EntregasApi.Models;

public class LiveCommentOrder : ITenantOwned
{
    public int Id { get; set; }

    /// <summary>Negocio (tenant) dueno de este comentario detectado.</summary>
    public int BusinessId { get; set; }

    public int LiveSessionId { get; set; }
    public LiveSession? LiveSession { get; set; }

    [Required, MaxLength(100)]
    public string Keyword { get; set; } = "";

    [MaxLength(200)]
    public string CommentDisplayName { get; set; } = "";

    public double? CommentedAtSeconds { get; set; }
    public double OcrConfidence { get; set; }
}
