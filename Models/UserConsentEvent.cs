using System.ComponentModel.DataAnnotations;

namespace DoForYou.API.Models;

/// <summary>
/// Append-only record of required or optional consent decisions. This is intentionally
/// separate from the user's current preference so a later change does not erase history.
/// </summary>
public sealed class UserConsentEvent
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }

    [Required, StringLength(40)]
    public string ConsentType { get; set; } = string.Empty;

    public bool Accepted { get; set; }

    [Required, StringLength(40)]
    public string Version { get; set; } = string.Empty;

    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    [Required, StringLength(40)]
    public string Source { get; set; } = "account";
}
