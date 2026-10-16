using System.ComponentModel.DataAnnotations;

namespace DoForYou.API.Models;

/// <summary>
/// Minimal audit metadata for signed identity-provider callbacks.
/// Raw webhook bodies and identity-document data are deliberately not persisted.
/// </summary>
public sealed class IdentityVerificationEvent
{
    public int Id { get; set; }
    public int UserId { get; set; }

    [Required, StringLength(24)]
    public string Provider { get; set; } = "Sumsub";

    [Required, StringLength(64)]
    public string EventHash { get; set; } = string.Empty;

    [StringLength(120)]
    public string? ProviderApplicantId { get; set; }

    [Required, StringLength(60)]
    public string EventType { get; set; } = string.Empty;

    [Required, StringLength(32)]
    public string ResultStatus { get; set; } = string.Empty;

    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
}
