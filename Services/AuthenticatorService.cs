using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DoForYou.API.Services;

public interface IAuthenticatorService
{
    bool IsConfigured { get; }
    string CreateSecret();
    string EncryptSecret(string secret);
    string DecryptSecret(string encrypted);
    string CreateProvisioningUri(string email, string secret);
    bool VerifyCode(string secret, string? code, DateTimeOffset? now = null);
    string GenerateRecoveryCode();
    string HashRecoveryCode(string code);
    bool TryConsumeRecoveryCode(string code, string? serializedHashes, out string remainingSerializedHashes);
}

public sealed class AuthenticatorService(IConfiguration configuration) : IAuthenticatorService
{
    private const string Issuer = "DoForYou";
    private const int SecretBytes = 20;
    private const int StepSeconds = 30;
    private const int Digits = 6;
    private static readonly char[] Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".ToCharArray();

    private byte[]? GetKey()
    {
        var raw = Environment.GetEnvironmentVariable("DFY_TOTP_ENCRYPTION_KEY")
            ?? configuration["Authenticator:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            var key = Convert.FromBase64String(raw.Trim());
            return key.Length == 32 ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public bool IsConfigured => GetKey() is not null;

    public string CreateSecret() => Base32Encode(RandomNumberGenerator.GetBytes(SecretBytes));

    public string EncryptSecret(string secret)
    {
        var key = GetKey() ?? throw new InvalidOperationException("DFY_TOTP_ENCRYPTION_KEY is not configured.");
        var plaintext = Encoding.UTF8.GetBytes(secret);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        var result = new byte[1 + nonce.Length + tag.Length + ciphertext.Length];
        result[0] = 1;
        Buffer.BlockCopy(nonce, 0, result, 1, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, 1 + nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, result, 1 + nonce.Length + tag.Length, ciphertext.Length);
        CryptographicOperations.ZeroMemory(plaintext);
        return Convert.ToBase64String(result);
    }

    public string DecryptSecret(string encrypted)
    {
        var key = GetKey() ?? throw new InvalidOperationException("DFY_TOTP_ENCRYPTION_KEY is not configured.");
        var packed = Convert.FromBase64String(encrypted);
        if (packed.Length < 1 + 12 + 16 + 1 || packed[0] != 1)
            throw new CryptographicException("Authenticator secret ciphertext format is invalid.");
        var nonce = packed.AsSpan(1, 12);
        var tag = packed.AsSpan(13, 16);
        var ciphertext = packed.AsSpan(29);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public string CreateProvisioningUri(string email, string secret)
    {
        var label = Uri.EscapeDataString($"{Issuer}:{email}");
        return $"otpauth://totp/{label}?secret={Uri.EscapeDataString(secret)}&issuer={Uri.EscapeDataString(Issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }

    public bool VerifyCode(string secret, string? code, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var normalized = new string(code.Where(char.IsAsciiDigit).ToArray());
        if (normalized.Length != Digits) return false;

        byte[] key;
        try { key = Base32Decode(secret); }
        catch (FormatException) { return false; }

        var counter = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / StepSeconds;
        for (var offset = -1; offset <= 1; offset++)
        {
            var expected = ComputeCode(key, counter + offset);
            var left = Encoding.ASCII.GetBytes(expected);
            var right = Encoding.ASCII.GetBytes(normalized);
            if (CryptographicOperations.FixedTimeEquals(left, right))
            {
                CryptographicOperations.ZeroMemory(key);
                return true;
            }
        }

        CryptographicOperations.ZeroMemory(key);
        return false;
    }

    public string GenerateRecoveryCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(8);
        return Convert.ToHexString(bytes).Insert(8, "-");
    }

    public string HashRecoveryCode(string code)
    {
        var normalized = NormalizeRecoveryCode(code);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    public bool TryConsumeRecoveryCode(string code, string? serializedHashes, out string remainingSerializedHashes)
    {
        remainingSerializedHashes = serializedHashes ?? "[]";
        if (string.IsNullOrWhiteSpace(serializedHashes)) return false;

        List<string>? hashes;
        try { hashes = System.Text.Json.JsonSerializer.Deserialize<List<string>>(serializedHashes); }
        catch (System.Text.Json.JsonException) { return false; }
        if (hashes is null || hashes.Count == 0) return false;

        var attempted = HashRecoveryCode(code);
        var matchedIndex = -1;
        for (var i = 0; i < hashes.Count; i++)
        {
            var expectedBytes = Encoding.ASCII.GetBytes(hashes[i]);
            var actualBytes = Encoding.ASCII.GetBytes(attempted);
            if (expectedBytes.Length == actualBytes.Length &&
                CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
                matchedIndex = i;
        }

        if (matchedIndex < 0) return false;
        hashes.RemoveAt(matchedIndex);
        remainingSerializedHashes = System.Text.Json.JsonSerializer.Serialize(hashes);
        return true;
    }

    private static string ComputeCode(byte[] key, long counter)
    {
        Span<byte> counterBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);
        var hash = HMACSHA1.HashData(key, counterBytes);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24)
            | ((hash[offset + 1] & 0xff) << 16)
            | ((hash[offset + 2] & 0xff) << 8)
            | (hash[offset + 3] & 0xff);
        var otp = binary % 1_000_000;
        CryptographicOperations.ZeroMemory(hash);
        return otp.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string NormalizeRecoveryCode(string code) =>
        new(code.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string Base32Encode(ReadOnlySpan<byte> data)
    {
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bitsLeft = 0;
        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                output.Append(Base32Alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }
        if (bitsLeft > 0) output.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 31]);
        return output.ToString();
    }

    private static byte[] Base32Decode(string value)
    {
        var clean = value.Trim().TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>();
        var buffer = 0;
        var bitsLeft = 0;
        foreach (var character in clean)
        {
            var index = Array.IndexOf(Base32Alphabet, character);
            if (index < 0) throw new FormatException("Invalid Base32 authenticator secret.");
            buffer = (buffer << 5) | index;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                output.Add((byte)((buffer >> (bitsLeft - 8)) & 0xff));
                bitsLeft -= 8;
            }
        }
        return output.ToArray();
    }
}
