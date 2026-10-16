using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DoForYou.API.Controllers;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using DoForYou.API.Services;
using Task = System.Threading.Tasks.Task;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DoForYou.Tests;

public class IdentityVerificationControllerTests
{
    private const string ConsentVersion = "identity-verification-v1";

    [Fact]
    public async Task Starting_verification_requires_consent_and_records_a_hashed_short_lived_handoff()
    {
        await using var context = CreateContext();
        var user = NewRunner(71);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var controller = CreateController(context, new FakeIdentityService());
        AddAuthenticatedUser(controller, user.Id);

        var denied = await controller.CreateSession(new StartIdentityVerificationRequest
        {
            ConsentAccepted = false,
            ConsentVersion = ConsentVersion
        }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(denied.Result);
        Assert.Null(user.IdentityVerificationHandoffTokenHash);

        var result = await controller.CreateSession(new StartIdentityVerificationRequest
        {
            ConsentAccepted = true,
            ConsentVersion = ConsentVersion
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ApiResponse<object>>(ok.Value);
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(response.Data));
        var handoff = payload.RootElement.GetProperty("handoffCode").GetString()!;

        Assert.NotEmpty(handoff);
        Assert.NotEqual(handoff, user.IdentityVerificationHandoffTokenHash);
        Assert.NotNull(user.IdentityVerificationHandoffExpiry);
        Assert.InRange(user.IdentityVerificationHandoffExpiry!.Value - DateTime.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
        Assert.Equal(1, user.IdentityVerificationAttemptCount);
        Assert.Null(user.IdNumber);
        Assert.Null(user.DateOfBirth);
        var consent = Assert.Single(context.UserConsentEvents);
        Assert.Equal("identity-verification", consent.ConsentType);
        Assert.Equal(ConsentVersion, consent.Version);
        Assert.True(consent.Accepted);
    }

    [Fact]
    public async Task Verification_start_is_rate_limited_and_cannot_create_parallel_handoffs()
    {
        await using var context = CreateContext();
        var user = NewRunner(72);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var controller = CreateController(context, new FakeIdentityService());
        AddAuthenticatedUser(controller, user.Id);

        var request = new StartIdentityVerificationRequest { ConsentAccepted = true, ConsentVersion = ConsentVersion };
        var first = await controller.CreateSession(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(first.Result);

        var parallel = await controller.CreateSession(request, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(parallel.Result);
        Assert.Single(context.UserConsentEvents);

        for (var attempt = 2; attempt <= 3; attempt++)
        {
            user.IdentityVerificationHandoffExpiry = DateTime.UtcNow.AddSeconds(-1);
            await context.SaveChangesAsync();
            var next = await controller.CreateSession(request, CancellationToken.None);
            Assert.IsType<OkObjectResult>(next.Result);
        }

        user.IdentityVerificationHandoffExpiry = DateTime.UtcNow.AddSeconds(-1);
        await context.SaveChangesAsync();

        var fourth = await controller.CreateSession(request, CancellationToken.None);
        Assert.Equal(StatusCodes.Status429TooManyRequests, Assert.IsType<ObjectResult>(fourth.Result).StatusCode);
        Assert.Equal(3, user.IdentityVerificationAttemptCount);
        Assert.Equal(3, await context.UserConsentEvents.CountAsync());
    }

    [Fact]
    public async Task Handoff_exchange_is_single_use_and_starts_pending_verification()
    {
        await using var context = CreateContext();
        const string code = "a-long-test-only-handoff-code-that-is-not-a-real-secret";
        var user = NewRunner(73);
        user.IdentityVerificationHandoffTokenHash = HashCode(code);
        user.IdentityVerificationHandoffExpiry = DateTime.UtcNow.AddMinutes(4);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var provider = new FakeIdentityService { AccessToken = "short-lived-sumsub-sdk-token" };
        var controller = CreateController(context, provider);

        var first = await controller.ExchangeHandoff(
            new ExchangeIdentityVerificationRequest { HandoffCode = code },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(first.Result);
        var response = Assert.IsType<ApiResponse<object>>(ok.Value);
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(response.Data));
        Assert.Equal("short-lived-sumsub-sdk-token", payload.RootElement.GetProperty("accessToken").GetString());

        Assert.Equal("pending", user.IdentityVerificationStatus);
        Assert.Equal("Sumsub", user.IdentityVerificationProvider);
        Assert.Equal($"dfy-user-{user.Id}", user.IdentityVerificationExternalUserId);
        Assert.Null(user.IdentityVerificationHandoffTokenHash);
        Assert.Null(user.IdentityVerificationHandoffExpiry);
        Assert.Null(user.IdNumber);

        var replay = await controller.ExchangeHandoff(
            new ExchangeIdentityVerificationRequest { HandoffCode = code },
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(replay.Result);
        Assert.Equal(1, provider.TokenRequests);
    }

    [Fact]
    public async Task Signed_green_review_marks_only_the_correlated_runner_as_passed_and_replay_is_idempotent()
    {
        await using var context = CreateContext();
        var user = NewRunner(74);
        user.IdentityVerificationStatus = "pending";
        user.IdentityVerificationProvider = "Sumsub";
        user.IdentityVerificationExternalUserId = $"dfy-user-{user.Id}";
        user.IdentityVerificationStartedAt = DateTime.UtcNow.AddMinutes(-2);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var json = JsonSerializer.Serialize(new
        {
            applicantId = "test-applicant-reference",
            externalUserId = $"dfy-user-{user.Id}",
            levelName = "sandbox-runner-level",
            type = "applicantReviewed",
            reviewStatus = "completed",
            reviewResult = new { reviewAnswer = "GREEN" },
            createdAtMs = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
        });
        var first = await SendWebhook(controller: CreateController(context, new FakeIdentityService()), json);

        Assert.IsType<OkObjectResult>(first);
        Assert.Equal("passed", user.IdentityVerificationStatus);
        Assert.Equal("test-applicant-reference", user.IdentityVerificationApplicantId);
        Assert.Single(context.IdentityVerificationEvents);
        var savedEvent = await context.IdentityVerificationEvents.SingleAsync();
        Assert.DoesNotContain(json, JsonSerializer.Serialize(savedEvent), StringComparison.Ordinal);
        Assert.Equal("passed", savedEvent.ResultStatus);

        var replay = await SendWebhook(controller: CreateController(context, new FakeIdentityService()), json);
        Assert.IsType<OkObjectResult>(replay);
        Assert.Equal("passed", user.IdentityVerificationStatus);
        Assert.Single(context.IdentityVerificationEvents);
    }

    [Fact]
    public async Task Signed_review_from_a_different_level_cannot_grant_runner_eligibility()
    {
        await using var context = CreateContext();
        var user = NewRunner(76);
        user.IdentityVerificationStatus = "pending";
        user.IdentityVerificationProvider = "Sumsub";
        user.IdentityVerificationExternalUserId = $"dfy-user-{user.Id}";
        user.IdentityVerificationStartedAt = DateTime.UtcNow.AddMinutes(-2);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var payload = JsonSerializer.Serialize(new
        {
            applicantId = "foreign-level-applicant",
            externalUserId = $"dfy-user-{user.Id}",
            levelName = "a-different-level",
            type = "applicantReviewed",
            reviewStatus = "completed",
            reviewResult = new { reviewAnswer = "GREEN" },
            createdAtMs = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
        });

        var action = await SendWebhook(CreateController(context, new FakeIdentityService()), payload);

        Assert.IsType<AcceptedResult>(action);
        Assert.Equal("pending", user.IdentityVerificationStatus);
        Assert.Empty(context.IdentityVerificationEvents);
    }

    [Fact]
    public async Task Invalid_provider_webhook_signature_is_rejected_without_state_change()
    {
        await using var context = CreateContext();
        var user = NewRunner(75);
        user.IdentityVerificationStatus = "pending";
        user.IdentityVerificationProvider = "Sumsub";
        user.IdentityVerificationExternalUserId = $"dfy-user-{user.Id}";
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var provider = new FakeIdentityService { AcceptWebhook = false };
        var action = await SendWebhook(CreateController(context, provider), "{\"type\":\"applicantReviewed\"}");

        Assert.IsType<UnauthorizedResult>(action);
        Assert.Equal("pending", user.IdentityVerificationStatus);
        Assert.Empty(context.IdentityVerificationEvents);
    }

    private static async Task<IActionResult> SendWebhook(IdentityVerificationController controller, string json)
    {
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        http.Request.Headers["X-Payload-Digest"] = "test-digest";
        http.Request.Headers["X-Payload-Digest-Alg"] = "HMAC_SHA256_HEX";
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return await controller.SumsubWebhook(CancellationToken.None);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static User NewRunner(int id) => new()
    {
        Id = id,
        FirstName = "Test",
        LastName = "Runner",
        Email = $"runner-{id}@example.com",
        PhoneNumber = "0821234567",
        UserType = "runner",
        PasswordHash = BCrypt.Net.BCrypt.HashPassword("Test-only-password-123"),
        EmailVerified = true,
        IsVerified = false,
        IdentityVerificationStatus = "not_started"
    };

    private static IdentityVerificationController CreateController(AppDbContext context, IIdentityVerificationService service) =>
        new(context, service, NullLogger<IdentityVerificationController>.Instance);

    private static void AddAuthenticatedUser(IdentityVerificationController controller, int userId)
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture))], "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
    }

    private static string HashCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();

    private sealed class FakeIdentityService : IIdentityVerificationService
    {
        public bool IsConfigured { get; set; } = true;
        public string ConsentVersion => IdentityVerificationControllerTests.ConsentVersion;
        public string ExpectedLevelName => "sandbox-runner-level";
        public string AccessToken { get; set; } = "provider-test-token";
        public bool AcceptWebhook { get; set; } = true;
        public int TokenRequests { get; private set; }

        public Task<string> CreateSdkAccessTokenAsync(string externalUserId, string verifiedEmail, CancellationToken cancellationToken)
        {
            Assert.StartsWith("dfy-user-", externalUserId, StringComparison.Ordinal);
            Assert.Contains("@example.com", verifiedEmail, StringComparison.Ordinal);
            TokenRequests++;
            return Task.FromResult(AccessToken);
        }

        public bool VerifyWebhookSignature(ReadOnlySpan<byte> rawBody, string? digest, string? digestAlgorithm) =>
            AcceptWebhook && digestAlgorithm == "HMAC_SHA256_HEX" && digest == "test-digest";
    }
}
