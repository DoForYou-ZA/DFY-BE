using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DoForYou.API.Services;

/// <summary>
/// Only server-side Sumsub calls are performed here. The provider's app token,
/// secret and webhook HMAC key must never be shipped to either client.
/// </summary>
public interface IIdentityVerificationService
{
    bool IsConfigured { get; }
    string ConsentVersion { get; }
    Task<string> CreateSdkAccessTokenAsync(string externalUserId, string verifiedEmail, CancellationToken cancellationToken);
    bool VerifyWebhookSignature(ReadOnlySpan<byte> rawBody, string? digest, string? digestAlgorithm);
}

public sealed class SumsubIdentityVerificationService(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<SumsubIdentityVerificationService> logger) : IIdentityVerificationService
{
    private const string ApiBaseUrl = "https://api.sumsub.com";
    private const string AccessTokenPath = "/resources/accessTokens/sdk";
    private const int AccessTokenTtlSeconds = 600;

    private string AppToken => ReadSetting("SUMSUB_APP_TOKEN", "Sumsub:AppToken");
    private string SecretKey => ReadSetting("SUMSUB_SECRET_KEY", "Sumsub:SecretKey");
    private string LevelName => ReadSetting("SUMSUB_LEVEL_NAME", "Sumsub:LevelName");
    private string WebhookSecret => ReadSetting("SUMSUB_WEBHOOK_SECRET", "Sumsub:WebhookSecret");
    public string ConsentVersion => ReadSetting("IDENTITY_VERIFICATION_CONSENT_VERSION", "IdentityVerification:ConsentVersion");

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AppToken) &&
        !string.IsNullOrWhiteSpace(SecretKey) &&
        !string.IsNullOrWhiteSpace(LevelName) &&
        !string.IsNullOrWhiteSpace(WebhookSecret) &&
        !string.IsNullOrWhiteSpace(ConsentVersion);

    public async Task<string> CreateSdkAccessTokenAsync(
        string externalUserId,
        string verifiedEmail,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Identity verification is not configured.");

        if (string.IsNullOrWhiteSpace(externalUserId) ||
            string.IsNullOrWhiteSpace(verifiedEmail))
            throw new ArgumentException("A stable user reference and verified email are required.");

        var requestBody = JsonSerializer.Serialize(new
        {
            applicantIdentifiers = new { email = verifiedEmail.Trim().ToLowerInvariant() },
            ttlInSecs = AccessTokenTtlSeconds,
            userId = externalUserId,
            levelName = LevelName
        });
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signatureInput = Encoding.UTF8.GetBytes(timestamp + "POST" + AccessTokenPath + requestBody);
        var secretBytes = Encoding.UTF8.GetBytes(SecretKey);
        string signature;
        try
        {
            signature = Convert.ToHexString(HMACSHA256.HashData(secretBytes, signatureInput)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            CryptographicOperations.ZeroMemory(signatureInput);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(ApiBaseUrl), AccessTokenPath));
        request.Headers.TryAddWithoutValidation("X-App-Token", AppToken);
        request.Headers.TryAddWithoutValidation("X-App-Access-Sig", signature);
        request.Headers.TryAddWithoutValidation("X-App-Access-Ts", timestamp);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Sumsub access-token request failed with HTTP {StatusCode}.", (int)response.StatusCode);
            throw new InvalidOperationException("The identity-verification provider could not start a session.");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var providerResponse = await JsonSerializer.DeserializeAsync<AccessTokenResponse>(
            responseStream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            cancellationToken);

        var token = providerResponse?.Token ?? providerResponse?.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            logger.LogWarning("Sumsub access-token response did not contain an access token.");
            throw new InvalidOperationException("The identity-verification provider returned an invalid session response.");
        }

        return token;
    }

    public bool VerifyWebhookSignature(ReadOnlySpan<byte> rawBody, string? digest, string? digestAlgorithm)
    {
        if (string.IsNullOrWhiteSpace(WebhookSecret) ||
            string.IsNullOrWhiteSpace(digest) ||
            string.IsNullOrWhiteSpace(digestAlgorithm))
            return false;

        HashAlgorithmName algorithm;
        switch (digestAlgorithm.Trim().ToUpperInvariant())
        {
            case "HMAC_SHA256_HEX":
                algorithm = HashAlgorithmName.SHA256;
                break;
            case "HMAC_SHA512_HEX":
                algorithm = HashAlgorithmName.SHA512;
                break;
            default:
                // SHA-1 is legacy/deprecated by Sumsub; refuse it for a new integration.
                return false;
        }

        byte[] secretBytes = Encoding.UTF8.GetBytes(WebhookSecret);
        byte[] actual;
        try
        {
            actual = algorithm == HashAlgorithmName.SHA256
                ? HMACSHA256.HashData(secretBytes, rawBody)
                : HMACSHA512.HashData(secretBytes, rawBody);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }

        try
        {
            byte[] supplied;
            try { supplied = Convert.FromHexString(digest.Trim()); }
            catch (FormatException) { return false; }

            return supplied.Length == actual.Length &&
                   CryptographicOperations.FixedTimeEquals(actual, supplied);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    private string ReadSetting(string environmentName, string configurationName) =>
        Environment.GetEnvironmentVariable(environmentName)?.Trim()
        ?? configuration[configurationName]?.Trim()
        ?? string.Empty;

    private sealed class AccessTokenResponse
    {
        [JsonPropertyName("token")]
        public string? Token { get; set; }

        [JsonPropertyName("accessToken")]
        public string? AccessToken { get; set; }
    }
}
