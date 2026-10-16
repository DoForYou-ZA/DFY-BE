using System.Security.Claims;
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
        Assert.Equal(3, context.UserConsentEvents.Count());
        Assert.All(context.UserConsentEvents, consent => Assert.Equal(user.Id, consent.UserId));
        Assert.Contains(context.UserConsentEvents, consent => consent.ConsentType == "terms" && consent.Accepted && consent.Version == "terms-v1");
        Assert.Contains(context.UserConsentEvents, consent => consent.ConsentType == "privacy" && consent.Accepted && consent.Version == "privacy-v1");
        Assert.Contains(context.UserConsentEvents, consent => consent.ConsentType == "marketing" && !consent.Accepted && consent.Version == "marketing-v1");
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

    [Fact]
    public async System.Threading.Tasks.Task Password_reset_respects_daily_send_quota()
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
            Email = "reset-quota@example.com",
            PhoneNumber = "0821234567",
            UserType = "creator",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("A-unique-test-passphrase-2026"),
            EmailVerified = true,
            PasswordResetSentAt = now.AddMinutes(-2),
            PasswordResetSendWindowStartAt = now.AddHours(-2),
            PasswordResetSendCount = 5
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var emailSender = new RecordingEmailSender();
        var controller = CreateController(context, emailSender);
        var result = await controller.ForgotPassword(new ForgotPasswordRequest { Email = user.Email });

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty(emailSender.Messages);
        Assert.Equal(5, user.PasswordResetSendCount);
    }

    [Fact]
    public async System.Threading.Tasks.Task Password_reset_allows_a_new_quota_after_24_hours()
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
            Email = "reset-window@example.com",
            PhoneNumber = "0821234567",
            UserType = "creator",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("A-unique-test-passphrase-2026"),
            EmailVerified = true,
            PasswordResetSentAt = now.AddMinutes(-2),
            PasswordResetSendWindowStartAt = now.AddHours(-25),
            PasswordResetSendCount = 5
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var emailSender = new RecordingEmailSender();
        var controller = CreateController(context, emailSender);
        var result = await controller.ForgotPassword(new ForgotPasswordRequest { Email = user.Email });

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Single(emailSender.Messages);
        Assert.Equal(1, user.PasswordResetSendCount);
        Assert.True(user.PasswordResetExpiry > DateTime.UtcNow);
    }

    [Fact]
    public async System.Threading.Tasks.Task Password_reset_invalidates_lockout_state_and_other_sessions()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new AppDbContext(options);
        const string rawToken = "one-time-reset-token";
        var user = new User
        {
            FirstName = "Test",
            LastName = "User",
            Email = "reset-unlock@example.com",
            PhoneNumber = "0821234567",
            UserType = "creator",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Old-unique-passphrase-123"),
            SecurityStamp = "old-session",
            EmailVerified = true,
            PasswordResetTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant(),
            PasswordResetExpiry = DateTime.UtcNow.AddMinutes(10),
            FailedLoginAttempts = 10,
            FailedLoginWindowStartAt = DateTime.UtcNow.AddMinutes(-1),
            LoginLockoutUntil = DateTime.UtcNow.AddMinutes(15)
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var controller = CreateController(context, new RecordingEmailSender());
        var result = await controller.ResetPassword(new AccountResetPasswordRequest
        {
            Token = rawToken,
            NewPassword = "A-new-unique-passphrase-2026"
        });

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.Null(user.FailedLoginWindowStartAt);
        Assert.Null(user.LoginLockoutUntil);
        Assert.NotEqual("old-session", user.SecurityStamp);
    }

    [Fact]
    public async System.Threading.Tasks.Task Ten_failed_password_attempts_trigger_timed_lockout()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new AppDbContext(options);
        var user = new User
        {
            FirstName = "Test",
            LastName = "User",
            Email = "lockout@example.com",
            PhoneNumber = "0821234567",
            UserType = "creator",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("A-valid-unique-passphrase-2026"),
            EmailVerified = true,
            SecurityStamp = "initial-stamp",
            Roles = "User"
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var controller = CreateController(context, new RecordingEmailSender());
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var result = await controller.Login(new LoginRequest
            {
                Email = " LOCKOUT@example.com ",
                Password = "wrong-password"
            });
            Assert.IsType<UnauthorizedObjectResult>(result.Result);
        }

        Assert.Equal(10, user.FailedLoginAttempts);
        Assert.True(user.LoginLockoutUntil > DateTime.UtcNow);

        var validPasswordDuringLockout = await controller.Login(new LoginRequest
        {
            Email = user.Email,
            Password = "A-valid-unique-passphrase-2026"
        });
        Assert.IsType<UnauthorizedObjectResult>(validPasswordDuringLockout.Result);
    }

    [Fact]
    public async System.Threading.Tasks.Task Server_logout_rotates_the_security_stamp()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new AppDbContext(options);
        var user = new User
        {
            Id = 73,
            FirstName = "Test",
            LastName = "User",
            Email = "logout@example.com",
            PhoneNumber = "0821234567",
            UserType = "creator",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("A-valid-unique-passphrase-2026"),
            EmailVerified = true,
            SecurityStamp = "active-session-stamp",
            Roles = "User"
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var controller = CreateController(context, new RecordingEmailSender());
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test"));
        var result = await controller.Logout();

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotEqual("active-session-stamp", user.SecurityStamp);
    }

    [Fact]
    public async System.Threading.Tasks.Task Authenticator_setup_expires_after_ten_minutes()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new AppDbContext(options);
        var user = new User
        {
            Id = 74,
            FirstName = "Test",
            LastName = "User",
            Email = "totp-setup@example.com",
            PhoneNumber = "0821234567",
            UserType = "runner",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("A-valid-unique-passphrase-2026"),
            EmailVerified = true,
            SecurityStamp = "active-session-stamp",
            Roles = "User"
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var controller = CreateController(context, new RecordingEmailSender());
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test"));
        var setup = await controller.SetupTwoFactor(new TwoFactorSetupRequest { CurrentPassword = "A-valid-unique-passphrase-2026" });
        Assert.IsType<OkObjectResult>(setup.Result);
        Assert.NotNull(user.PendingAuthenticatorSecretExpiry);
        Assert.InRange((user.PendingAuthenticatorSecretExpiry!.Value - DateTime.UtcNow).TotalMinutes, 9.0, 10.0);

        user.PendingAuthenticatorSecretExpiry = DateTime.UtcNow.AddSeconds(-1);
        var expired = await controller.ConfirmTwoFactor(new TwoFactorCodeRequest { Code = "000000" });
        Assert.IsType<ConflictObjectResult>(expired.Result);
        Assert.Null(user.PendingAuthenticatorSecretEncrypted);
        Assert.Null(user.PendingAuthenticatorSecretExpiry);
        Assert.False(user.AuthenticatorTwoFactorEnabled);
    }

    [Fact]
    public async System.Threading.Tasks.Task Recovery_code_can_only_be_used_once_for_sign_in()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new AppDbContext(options);
        var authService = new AuthenticatorService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authenticator:EncryptionKey"] = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray())
            })
            .Build());
        var recoveryCode = authService.GenerateRecoveryCode();
        var user = new User
        {
            Id = 75,
            FirstName = "Test",
            LastName = "User",
            Email = "totp-recovery@example.com",
            PhoneNumber = "0821234567",
            UserType = "runner",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("A-valid-unique-passphrase-2026"),
            EmailVerified = true,
            AuthenticatorTwoFactorEnabled = true,
            AuthenticatorSecretEncrypted = "encrypted-secret-is-not-needed-for-recovery",
            AuthenticatorRecoveryCodeHashes = System.Text.Json.JsonSerializer.Serialize(new[] { authService.HashRecoveryCode(recoveryCode) }),
            SecurityStamp = "active-session-stamp",
            Roles = "User"
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var controller = CreateController(context, new RecordingEmailSender());
        var first = await controller.Login(new LoginRequest
        {
            Email = user.Email,
            Password = "A-valid-unique-passphrase-2026",
            SecondFactorCode = recoveryCode
        });
        Assert.IsType<OkObjectResult>(first.Result);
        Assert.Empty(System.Text.Json.JsonSerializer.Deserialize<List<string>>(user.AuthenticatorRecoveryCodeHashes!)!);

        var replay = await controller.Login(new LoginRequest
        {
            Email = user.Email,
            Password = "A-valid-unique-passphrase-2026",
            SecondFactorCode = recoveryCode
        });
        Assert.IsType<UnauthorizedObjectResult>(replay.Result);
    }

    private static AuthController CreateController(AppDbContext context, IEmailSender emailSender, int? authenticatedUserId = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "test-signing-key-that-is-at-least-32-characters-long",
            ["Jwt:Issuer"] = "DoForYou",
            ["Jwt:Audience"] = "DoForYou",
            ["Frontend:BaseUrl"] = "https://dfy-fe-staging.onrender.com",
            ["Authenticator:EncryptionKey"] = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray())
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
        var httpContext = new DefaultHttpContext();
        if (authenticatedUserId.HasValue)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, authenticatedUserId.Value.ToString())
            }, "TestAuth"));
        }
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
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
