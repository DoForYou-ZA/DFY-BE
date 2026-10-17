using DoForYou.API.Services;
using Microsoft.Extensions.Configuration;

namespace DoForYou.Tests;

public class AuthenticatorServiceTests
{
    private static AuthenticatorService CreateService() => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authenticator:EncryptionKey"] = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray())
            })
            .Build());

    [Fact]
    public void Setup_secret_is_encrypted_and_can_be_decrypted()
    {
        var service = CreateService();
        var secret = service.CreateSecret();
        var encrypted = service.EncryptSecret(secret);

        Assert.NotEqual(secret, encrypted);
        Assert.Equal(secret, service.DecryptSecret(encrypted));
        Assert.Contains("otpauth://totp/", service.CreateProvisioningUri("person@example.com", secret));
        Assert.Contains("issuer=DoForYou", service.CreateProvisioningUri("person@example.com", secret));
    }

    [Fact]
    public void Verifies_RFC6238_six_digit_code_and_rejects_invalid_code()
    {
        // RFC 6238 test secret represented as Base32; Unix time 59 seconds has
        // a six-digit SHA1 TOTP value of 287082.
        var service = CreateService();
        const string secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        var instant = DateTimeOffset.FromUnixTimeSeconds(59);

        Assert.True(service.VerifyCode(secret, "287082", instant));
        Assert.True(service.TryVerifyCode(secret, "287082", out var step, instant));
        Assert.Equal(1L, step);
        Assert.False(service.TryVerifyCode(secret, "287083", out _, instant));
        Assert.False(service.VerifyCode(secret, "28-7082", instant));
    }

    [Fact]
    public void Recovery_codes_are_single_use_and_stored_as_hashes()
    {
        var service = CreateService();
        var raw = service.GenerateRecoveryCode();
        var hashes = System.Text.Json.JsonSerializer.Serialize(new[] { service.HashRecoveryCode(raw) });

        Assert.DoesNotContain(raw, hashes);
        Assert.True(service.TryConsumeRecoveryCode(raw, hashes, out var remaining));
        Assert.Empty(System.Text.Json.JsonSerializer.Deserialize<List<string>>(remaining)!);
        Assert.False(service.TryConsumeRecoveryCode(raw, remaining, out _));
    }

    [Fact]
    public void Invalid_or_absent_encryption_key_fails_closed()
    {
        var service = new AuthenticatorService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authenticator:EncryptionKey"] = "not-base64"
            })
            .Build());

        Assert.False(service.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => service.EncryptSecret("TEST"));
    }
}
