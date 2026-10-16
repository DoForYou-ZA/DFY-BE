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
    bool TryGetAdultDateOfBirthFromSouthAfricanId(string? value, out DateTime dateOfBirth);
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
        var userTypeLower = NormalizeUserType(user.UserType);
        if (userTypeLower is not ("creator" or "runner" or "both"))
            missing.Add("userType");
        if (userTypeLower is "runner" or "both" && !hasVerifiedBank)
            missing.Add("verifiedBankAccount");

        return missing;
    }

    public bool CanCreateTasks(User user) =>
        IsProfileComplete(user) && (NormalizeUserType(user.UserType) is "creator" or "both");

    public bool CanAcceptTasks(User user, bool hasVerifiedBank = true) =>
        IsProfileComplete(user, hasVerifiedBank) && (NormalizeUserType(user.UserType) is "runner" or "both");

    private static string NormalizeUserType(string? userType) =>
        userType?.Trim().ToLowerInvariant() ?? string.Empty;

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
        if (string.IsNullOrWhiteSpace(value) || value.Length != 13 || !value.All(char.IsAsciiDigit))
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

    /// <summary>
    /// Derives a plausible adult date from YYMMDD. Checksum success is not proof
    /// of identity or ownership. The 110-year ceiling prevents an underage YY
    /// from being silently interpreted as a century-old birth.
    /// </summary>
    public bool TryGetAdultDateOfBirthFromSouthAfricanId(string? value, out DateTime dateOfBirth)
    {
        dateOfBirth = default;
        if (!IsValidSouthAfricanId(value)) return false;

        var yy = int.Parse(value!.Substring(0, 2), System.Globalization.CultureInfo.InvariantCulture);
        var month = int.Parse(value.Substring(2, 2), System.Globalization.CultureInfo.InvariantCulture);
        var day = int.Parse(value.Substring(4, 2), System.Globalization.CultureInfo.InvariantCulture);
        var today = DateTime.UtcNow.Date;
        var currentCenturyYear = (today.Year / 100) * 100 + yy;

        foreach (var year in new[] { currentCenturyYear, currentCenturyYear - 100 })
        {
            try
            {
                var dob = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
                var age = today.Year - dob.Year;
                if (dob.Date > today.AddYears(-age)) age--;
                if (age >= 18 && age <= 110 && dob.Date <= today)
                {
                    dateOfBirth = dob;
                    return true;
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        return false;
    }
}
