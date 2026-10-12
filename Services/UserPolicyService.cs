using System.ComponentModel.DataAnnotations;
using DoForYou.API.Models;

namespace DoForYou.API.Services;

/// <summary>
/// Single backend authority for user profile readiness and role capabilities.
/// Frontends may use these values for presentation, but authorization must always
/// be evaluated here/server-side at the point of action.
/// </summary>
public interface IUserPolicyService
{
    bool IsProfileComplete(User user, bool hasVerifiedBank = true);
    int GetProfileCompletion(User user, bool hasVerifiedBank = true);
    IReadOnlyList<string> GetMissingProfileFields(User user, bool hasVerifiedBank = true);
    bool CanCreateTasks(User user);
    bool CanAcceptTasks(User user, bool hasVerifiedBank = true);
    bool IsValidPhone(string value);
    bool IsPlaceholderAddress(string? value);
    bool IsValidSouthAfricanId(string? value);
}

public sealed class UserPolicyService : IUserPolicyService
{
    private static readonly string[] PlaceholderAddresses =
    {
        "just around", "near me", "around", "n/a", "na", "tbc", "unknown", "somewhere"
    };

    public bool IsProfileComplete(User user, bool hasVerifiedBank = true) =>
        GetMissingProfileFields(user, hasVerifiedBank).Count == 0;

    public int GetProfileCompletion(User user, bool hasVerifiedBank = true)
    {
        const int total = 9;
        var complete = total - GetMissingProfileFields(user, hasVerifiedBank).Count;
        return (int)Math.Round(complete * 100.0 / total);
    }

    public IReadOnlyList<string> GetMissingProfileFields(User user, bool hasVerifiedBank = true)
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(user.FirstName))
            missing.Add("firstName");
        if (string.IsNullOrWhiteSpace(user.LastName))
            missing.Add("lastName");
        if (!new EmailAddressAttribute().IsValid(user.Email))
            missing.Add("email");
        if (string.IsNullOrWhiteSpace(user.PhoneNumber) || !IsValidPhone(user.PhoneNumber))
            missing.Add("phoneNumber");
        if (string.IsNullOrWhiteSpace(user.Address) || IsPlaceholderAddress(user.Address))
            missing.Add("address");
        if (!IsValidSouthAfricanId(user.IdNumber))
            missing.Add("idNumber");
        if (!user.DateOfBirth.HasValue)
            missing.Add("dateOfBirth");
        var userTypeLower = user.UserType?.ToLowerInvariant();
        if (userTypeLower is not ("creator" or "runner" or "both"))
            missing.Add("userType");
        if (userTypeLower is "runner" or "both" && !hasVerifiedBank)
            missing.Add("verifiedBankAccount");

        return missing;
    }

    public bool CanCreateTasks(User user) =>
        IsProfileComplete(user) && (user.UserType?.ToLowerInvariant() is "creator" or "both");

    public bool CanAcceptTasks(User user, bool hasVerifiedBank = true) =>
        IsProfileComplete(user, hasVerifiedBank) && (user.UserType?.ToLowerInvariant() is "runner" or "both");

    public bool IsValidPhone(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return (digits.Length == 10 && digits.StartsWith("0")) ||
               (digits.Length == 11 && digits.StartsWith("27"));
    }

    public bool IsPlaceholderAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        return PlaceholderAddresses.Contains(value.Trim().ToLowerInvariant());
    }

    public bool IsValidSouthAfricanId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 13 || !value.All(char.IsDigit))
            return false;

        var sum = 0;
        for (var i = 0; i < 13; i++)
        {
            var digit = value[i] - '0';
            if (i % 2 == 1)
            {
                digit *= 2;
                if (digit > 9) digit -= 9;
            }
            sum += digit;
        }

        return sum % 10 == 0;
    }
}
