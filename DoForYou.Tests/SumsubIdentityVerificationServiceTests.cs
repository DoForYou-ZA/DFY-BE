using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DoForYou.API.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DoForYou.Tests;

public class SumsubIdentityVerificationServiceTests
{
    private const string AppToken = "test-app-token";
    private const string SecretKey = "test-app-secret";
    private const string WebhookSecret = "test-webhook-secret";
    private const string ConsentVersion = "identity-verification-v1";

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sumsub:AppToken"] = AppToken,
            ["Sumsub:SecretKey"] = SecretKey,
            ["Sumsub:LevelName"] = "dfy-runner-sandbox",
            ["Sumsub:WebhookSecret"] = WebhookSecret,
            ["IdentityVerification:ConsentVersion"] = ConsentVersion
        })
        .Build();

    [Fact]
    public void Webhook_signature_accepts_sha256_and_sha512_using_fixed_time_comparison()
    {
        var body = Encoding.UTF8.GetBytes("{\"type\":\"applicantReviewed\",\"reviewResult\":{\"reviewAnswer\":\"GREEN\"}}");
        var service = new SumsubIdentityVerificationService(new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))), Configuration(),
            NullLogger<SumsubIdentityVerificationService>.Instance);

        var sha256 = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), body)).ToLowerInvariant();
        var sha512 = Convert.ToHexString(HMACSHA512.HashData(Encoding.UTF8.GetBytes(WebhookSecret), body)).ToLowerInvariant();

        Assert.True(service.VerifyWebhookSignature(body, sha256, "HMAC_SHA256_HEX"));
        Assert.True(service.VerifyWebhookSignature(body, sha512, "HMAC_SHA512_HEX"));
        Assert.False(service.VerifyWebhookSignature(body, "00", "HMAC_SHA256_HEX"));
        Assert.False(service.VerifyWebhookSignature(body, sha256, "HMAC_SHA1_HEX"));
        Assert.False(service.VerifyWebhookSignature(body, null, "HMAC_SHA256_HEX"));
    }

    [Fact]
    public async Task Access_token_request_is_signed_over_timestamp_method_path_and_exact_body()
    {
        string? bodySeen = null;
        string? signatureSeen = null;
        string? timestampSeen = null;
        string? pathSeen = null;
        string? emailSeen = null;

        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            bodySeen = await request.Content!.ReadAsStringAsync(cancellationToken);
            signatureSeen = request.Headers.GetValues("X-App-Access-Sig").Single();
            timestampSeen = request.Headers.GetValues("X-App-Access-Ts").Single();
            pathSeen = request.RequestUri!.PathAndQuery;
            emailSeen = request.Headers.GetValues("X-App-Token").Single();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token\":\"short-lived-sdk-token\",\"userId\":\"dfy-user-17\"}", Encoding.UTF8, "application/json")
            };
        });

        var service = new SumsubIdentityVerificationService(
            new HttpClient(handler),
            Configuration(),
            NullLogger<SumsubIdentityVerificationService>.Instance);

        var token = await service.CreateSdkAccessTokenAsync("dfy-user-17", "  PERSON@EXAMPLE.COM ", CancellationToken.None);

        Assert.Equal("short-lived-sdk-token", token);
        Assert.Equal(AppToken, emailSeen);
        Assert.Contains("person@example.com", bodySeen, StringComparison.Ordinal);
        Assert.Equal("/resources/accessTokens/sdk", pathSeen);
        var bytesToSign = Encoding.UTF8.GetBytes(timestampSeen + "POST" + pathSeen + bodySeen);
        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(SecretKey), bytesToSign)).ToLowerInvariant();
        Assert.Equal(expected, signatureSeen);
    }

    [Fact]
    public void Missing_required_provider_or_consent_configuration_fails_closed()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sumsub:AppToken"] = AppToken,
                ["Sumsub:SecretKey"] = SecretKey,
                ["Sumsub:LevelName"] = "dfy-runner-sandbox",
                // A webhook secret and approved consent version are intentionally missing.
            })
            .Build();
        var service = new SumsubIdentityVerificationService(
            new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))),
            configuration,
            NullLogger<SumsubIdentityVerificationService>.Instance);

        Assert.False(service.IsConfigured);
        Assert.False(service.VerifyWebhookSignature(Encoding.UTF8.GetBytes("{}"), "00", "HMAC_SHA256_HEX"));
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
