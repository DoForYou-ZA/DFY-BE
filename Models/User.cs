using System.ComponentModel.DataAnnotations;

namespace DoForYou.API.Models;

public class User
{
    public int Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string UserType { get; set; } = string.Empty;
    public string? IdNumber { get; set; }
    public string? Address { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public bool IsVerified { get; set; }
    public bool ProfileCompleted { get; set; }
    public decimal Rating { get; set; }
    public int CompletedTasks { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public string? Roles { get; set; }
    public DateTime? EmailVerificationExpiry { get; set; }
    public DateTime? EmailVerificationSentAt { get; set; }
    public string? EmailVerificationToken { get; set; }
    public bool EmailVerified { get; set; }
    public string? PhoneVerificationCode { get; set; }
    public DateTime? PhoneVerificationExpiry { get; set; }
    public bool PhoneVerified { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public string? Username { get; set; }
    public decimal WalletBalance { get; set; }
    public bool IsAvailable { get; set; } = true;
    public bool IsArchived { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public string? ArchivedReason { get; set; }
    public string? Bio { get; set; }
    public string? ServiceCategories { get; set; }
    public string? CoverageArea { get; set; }
    public string? ProfilePhotoUrl { get; set; }

    // Evidence of registration consent; versions identify the exact published notices.
    public DateTime? TermsAcceptedAt { get; set; }
    public string? TermsVersion { get; set; }
    public DateTime? PrivacyAcceptedAt { get; set; }
    public string? PrivacyVersion { get; set; }
    public bool MarketingConsent { get; set; }
    public DateTime? MarketingConsentUpdatedAt { get; set; }
    public string? PasswordResetTokenHash { get; set; }
    public DateTime? PasswordResetExpiry { get; set; }
    public DateTime? PasswordResetSentAt { get; set; }
    public string? SecurityStamp { get; set; }
}