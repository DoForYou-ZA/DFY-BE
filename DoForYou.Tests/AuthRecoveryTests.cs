using System.Security.Cryptography;
using System.Text;
using DoForYou.API.Controllers;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using DoForYou.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DoForYou.Tests;

public class AuthRecoveryTests
{

    [Fact]
    public async System.Threading.Tasks.Task Registration_stores_hashed_verification_token_and_does_not_issue_session()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new AppDbContext(options);
        var emailSender = new RecordingEmailSender();
        var controller = CreateController(context, emailSender);

        var result = await controller.Register(new RegisterRequest
        {
            FirstName = "Test",
            LastName = "Creator",
            Email = "new.creator@example.com",
            PhoneNumber = "0821234567",
            UserType = "creator",
            Password = "A-strong-unique-passphrase-2026",
            TermsAccepted = true,
            PrivacyAccepted = true,
            TermsVersion = "terms-v1",
            PrivacyVersion = "privacy-v1",
            MarketingConsent = false
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<AuthResponse>(ok.Value);
        var user = Assert.Single(context.Users);

        Assert.True(response.Success);
        Assert.Null(response.Token);
        Assert.False(user.EmailVerified);
        Assert.False(user.PhoneVerified);
        Assert.Null(user.IdNumber);
        Assert.Null(user.Address);
        Assert.Null(user.DateOfBirth);
        Assert.Equal("not_started", user.IdentityVerificationStatus);
        Assert.False(user.AuthenticatorTwoFactorEnabled);
        Assert.NotNull(user.EmailVerificationToken);
        Assert.NotNull(user.EmailVerificationExpiry);
        Assert.NotNull(user.EmailVerificationSentAt);
        Assert.Equal(1, user.EmailVerificationSendCount);
        Assert.NotNull(user.EmailVerificationSendWindowStartAt);
        var emailBody = Assert.Single(emailSender.Messages).Body;
        var tokenLine = emailBody.Split('\n').Single(line => line.Contains("?token=", StringComparison.Ordinal));
        var rawToken = Uri.UnescapeDataString(tokenLine[(tokenLine.IndexOf("?token=", StringComparison.Ordinal) + 7)..]);
        Assert.NotEqual(rawToken, user.EmailVerificationToken);
        Assert.Contains("verify-email", emailBody);
    }

    [Fact]
    public async System.Threading.Tasks.Task Resend_verification_is_throttled_within_60_seconds()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new AppDbContext(options);
        var now = DateTime.UtcNow;
        var user = new User
        {
            FirstName = "Test",
            LastName = "User",
            Email = "resend@example.com",
            PhoneNumber = "0821234567",
            UserType = "creator",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("A-unique-test-passphrase-2026"),
            EmailVerified = false,
            EmailVerificationSentAt = now.AddSeconds(-59),
            EmailVerificationSendWindowStartAt = now.AddHours(-1),
            EmailVerificationSendCount = 1
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var emailSender = new RecordingEmailSender();
        var controller = CreateController(context, emailSender);
        var result = await controller.ResendVerification(new ResendVerificationRequest { Email = user.Email });

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty(emailSender.Messages);
        Assert.Equal(1, user.EmailVerificationSendCount);
    }

    [Fact]
    public async System.Threading.Tasks.Task Resend_verification_allows_send_after_60_seconds_and_increments_budget()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new AppDbContext(options);
        var now = DateTime.UtcNow;
        var user = new User
        {
            FirstName = "Test",
            LastName = "User",
            Email = "resend-allowed@example.com",
            PhoneNumber = "0821234567",
            UserType = "creator",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("A-unique-test-passphrase-2026"),
            EmailVerified = false,
            EmailVerificationSentAt = now.AddSeconds(-61),
            EmailVerificationSendWindowStartAt = now.AddHours(-1),
            EmailVerificationSendCount = 1
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var emailSender = new RecordingEmailSender();
        var controller = CreateController(context, emailSender);
        var result = await controller.ResendVerification(new ResendVerificationRequest { Email = user.Email });

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Single(emailSender.Messages);
        Assert.Equal(2, user.EmailVerificationSendCount);
        Assert.True(user.EmailVerificationExpiry > DateTime.UtcNow);
    }

    [Fact]
    public async System.Threading.Tasks.Task Resend_verification_stops_at_five_sends_per_24_hours()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new AppDbContext(options);
        var now = DateTime.UtcNow;
        var user = new User
        {
            FirstName = "Test",
            LastName = "User",
            Email = "resend-limit@example.com",
            PhoneNumber = "0821234567",
            UserType = "creator",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("A-unique-test-passphrase-2026"),
            EmailVerified = false,
            EmailVerificationSentAt = now.AddMinutes(-2),
            EmailVerificationSendWindowStartAt = now.AddHours(-2),
            EmailVerificationSendCount = 5
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var emailSender = new RecordingEmailSender();
        var controller = CreateController(context, emailSender);
        var result = await controller.ResendVerification(new ResendVerificationRequest { Email = user.Email });

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty(emailSender.Messages);
        Assert.Equal(5, user.EmailVerificationSendCount);
    }

    [Fact]
    public async System.Threading.Tasks.Task Password_reset_consumes_token_and_revokes_existing_sessions()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new AppDbContext(options);
        var user = new User
        {
            FirstName = "Test",
            LastName = "User",
            Email = "reset@example.com",
            PhoneNumber = "0821234567",
            UserType = "creator",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Old-unique-passphrase-123"),
            SecurityStamp = "existing-session-stamp",
            EmailVerified = false,
            IsArchived = false
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var emailSender = new RecordingEmailSender();
        var controller = CreateController(context, emailSender);
        var forgotResult = await controller.ForgotPassword(new ForgotPasswordRequest { Email = " RESET@example.com " });

        Assert.IsType<OkObjectResult>(forgotResult.Result);
        Assert.NotNull(user.PasswordResetTokenHash);
        Assert.NotNull(user.PasswordResetExpiry);
        Assert.True(user.PasswordResetExpiry > DateTime.UtcNow);

        await controller.ForgotPassword(new ForgotPasswordRequest { Email = "reset@example.com" });
        Assert.Single(emailSender.Messages);

        var emailBody = Assert.Single(emailSender.Messages).Body;
        var tokenLine = emailBody.Split('\n').Single(line => line.Contains("?token=", StringComparison.Ordinal));
        var token = Uri.UnescapeDataString(tokenLine[(tokenLine.IndexOf("?token=", StringComparison.Ordinal) + 7)..]);
        Assert.NotEqual(token, user.PasswordResetTokenHash);

        var previousStamp = user.SecurityStamp;
        var resetResult = await controller.ResetPassword(new AccountResetPasswordRequest
        {
            Token = token,
            NewPassword = "A-new-unique-passphrase-2026"
        });
        var ok = Assert.IsType<OkObjectResult>(resetResult.Result);
        var response = Assert.IsType<ApiResponse<bool>>(ok.Value);

        Assert.True(response.Success);
        Assert.True(BCrypt.Net.BCrypt.Verify("A-new-unique-passphrase-2026", user.PasswordHash));
        Assert.True(user.EmailVerified);
        Assert.NotEqual(previousStamp, user.SecurityStamp);
        Assert.Null(user.PasswordResetTokenHash);
        Assert.Null(user.PasswordResetExpiry);

        var replayResult = await controller.ResetPassword(new AccountResetPasswordRequest
        {
            Token = token,
            NewPassword = "Another-unique-passphrase-2026"
        });
        Assert.IsType<BadRequestObjectResult>(replayResult.Result);
    }

    [Fact]
    public async System.Threading.Tasks.Task Email_verification_consumes_hashed_token_once()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new AppDbContext(options);
        const string token = "verification-token-for-test-only";
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        var user = new User
        {
            FirstName = "Test",
            LastName = "User",
            Email = "verify@example.com",
            PhoneNumber = "0821234567",
            UserType = "creator",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Old-unique-passphrase-123"),
            EmailVerificationToken = tokenHash,
            EmailVerificationExpiry = DateTime.UtcNow.AddHours(1),
            EmailVerified = false
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var controller = CreateController(context, new RecordingEmailSender());
        var result = await controller.VerifyEmail(new VerifyEmailRequest { Token = token });

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.True(user.EmailVerified);
        Assert.Null(user.EmailVerificationToken);
        Assert.Null(user.EmailVerificationExpiry);
    }

    private static AuthController CreateController(AppDbContext context, IEmailSender emailSender)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "test-signing-key-that-is-at-least-32-characters-long",
            ["Jwt:Issuer"] = "DoForYou",
            ["Jwt:Audience"] = "DoForYou",
            ["Frontend:BaseUrl"] = "https://dfy-fe-staging.onrender.com"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var controller = new AuthController(
            context,
            configuration,
            NullLogger<AuthController>.Instance,
            new NoOpNotificationService(),
            new UserPolicyService(),
            emailSender,
            new AuthenticatorService(configuration));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private sealed class RecordingEmailSender : IEmailSender
    {
        public List<(string Recipient, string Subject, string Body)> Messages { get; } = new();

        public System.Threading.Tasks.Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default)
        {
            Messages.Add((recipient, subject, body));
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    private sealed class NoOpNotificationService : INotificationService
    {
        public System.Threading.Tasks.Task CreateNotificationAsync(int userId, string type, string title, string message, int? relatedTaskId = null) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task NotifyTaskClaimedAsync(int creatorId, string taskDescription, string runnerName, int? taskId = null) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task NotifyTaskCompletedAsync(int creatorId, string taskDescription, string runnerName, int? taskId = null) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task NotifyPaymentReleasedAsync(int runnerId, string taskDescription, decimal amount, int? taskId = null) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task NotifyPayoutCompletedAsync(int runnerId, string taskDescription, decimal amount, int? taskId = null) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task NotifyNewMessageAsync(int recipientId, string taskDescription, string senderName, int? taskId = null) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task NotifyAdminsAsync(string type, string title, string message, int? relatedTaskId = null) => System.Threading.Tasks.Task.CompletedTask;
    }
}
