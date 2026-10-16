using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DoForYou.API.Services;

public enum PasswordPolicyStatus
{
    Accepted,
    Compromised,
    Unavailable
}

public sealed record PasswordPolicyResult(PasswordPolicyStatus Status);

public interface IPasswordPolicyService
{
    Task<PasswordPolicyResult> EvaluateAsync(string password, CancellationToken cancellationToken = default);
}

/// <summary>
/// Rejects a small local list of common passwords, then checks the Pwned Passwords
/// range API using k-anonymity. The password and its full hash are never transmitted.
/// Range responses are compared in memory only and are not cached or persisted.
/// </summary>
public sealed class PwnedPasswordPolicyService(HttpClient httpClient) : IPasswordPolicyService
{
    private static readonly HashSet<string> CommonPasswords = new(StringComparer.Ordinal)
    {
        "password", "password1", "password123", "passw0rd", "p@ssw0rd",
        "123456", "1234567", "12345678", "123456789", "1234567890",
        "qwerty", "qwerty123", "123qwe", "abc123", "admin", "admin123",
        "letmein", "welcome", "welcome1", "changeme", "iloveyou",
        "monkey", "dragon", "sunshine", "football", "princess",
        "trustno1", "master", "login", "solo", "starwars",
        "000000", "111111", "654321", "Password1", "Password123"
    };

    public async Task<PasswordPolicyResult> EvaluateAsync(string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(password))
            return new PasswordPolicyResult(PasswordPolicyStatus.Compromised);

        if (CommonPasswords.Contains(password) ||
            CommonPasswords.Contains(password.ToLowerInvariant()))
        {
            return new PasswordPolicyResult(PasswordPolicyStatus.Compromised);
        }

        byte[]? passwordBytes = null;
        byte[]? hashBytes = null;
        try
        {
            passwordBytes = Encoding.UTF8.GetBytes(password);
            hashBytes = SHA1.HashData(passwordBytes);
            var hash = Convert.ToHexString(hashBytes);
            var prefix = hash[..5];
            var wantedSuffix = hash[5..];

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.pwnedpasswords.com/range/{prefix}");
            request.Headers.TryAddWithoutValidation("User-Agent", "DoForYou-PasswordSecurity/1.0");
            request.Headers.TryAddWithoutValidation("Add-Padding", "true");

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new PasswordPolicyResult(PasswordPolicyStatus.Unavailable);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
                return new PasswordPolicyResult(PasswordPolicyStatus.Unavailable);

            foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = line.IndexOf(':');
                if (separator != 35)
                    continue;

                var suffix = line[..separator].Trim();
                if (!string.Equals(suffix, wantedSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (long.TryParse(line[(separator + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) &&
                    count > 0)
                {
                    return new PasswordPolicyResult(PasswordPolicyStatus.Compromised);
                }
            }

            return new PasswordPolicyResult(PasswordPolicyStatus.Accepted);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PasswordPolicyResult(PasswordPolicyStatus.Unavailable);
        }
        catch (HttpRequestException)
        {
            return new PasswordPolicyResult(PasswordPolicyStatus.Unavailable);
        }
        catch (IOException)
        {
            return new PasswordPolicyResult(PasswordPolicyStatus.Unavailable);
        }
        finally
        {
            if (passwordBytes != null)
                CryptographicOperations.ZeroMemory(passwordBytes);
            if (hashBytes != null)
                CryptographicOperations.ZeroMemory(hashBytes);
        }
    }
}
