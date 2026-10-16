using System.Security.Claims;
using System.Text.Json;
using DoForYou.API.Controllers;
using DoForYou.API.Data;
using DoForYou.API.Models;
using DoForYou.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.Tests;

public class ProfilePrivacyTests
{
    [Fact]
    public async System.Threading.Tasks.Task Standard_profile_response_never_returns_full_id_number()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new AppDbContext(options);
        context.Users.Add(new User
        {
            Id = 42,
            FirstName = "Test",
            LastName = "Profile",
            Email = "profile@example.com",
            PhoneNumber = "0821234567",
            UserType = "creator",
            IdNumber = "8001015009087",
            DateOfBirth = new DateTime(1980, 1, 1),
            Address = "15 Church Street, Pretoria",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Test-only-password-123"),
            EmailVerified = true,
            IsVerified = false,
            ProfileCompleted = true
        });
        await context.SaveChangesAsync();

        var controller = new UserController(context, new UserPolicyService());
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "42")], "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var action = await controller.GetProfile();
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var json = JsonSerializer.Serialize(ok.Value);

        Assert.DoesNotContain("\"idNumber\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"dateOfBirth\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"hasIdNumber\":true", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"idNumberLastFour\":\"9087\"", json, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async System.Threading.Tasks.Task General_profile_update_rejects_identity_data_until_dedicated_verification_flow_exists()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new AppDbContext(options);
        var user = new User
        {
            Id = 43,
            FirstName = "Test",
            LastName = "Profile",
            Email = "profile-write@example.com",
            PhoneNumber = "0821234567",
            UserType = "runner",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Test-only-password-123"),
            EmailVerified = true,
            ProfileCompleted = false
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var controller = new UserController(context, new UserPolicyService());
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "43")], "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var action = await controller.UpdateProfile(new UpdateProfileRequest
        {
            FirstName = "Test",
            LastName = "Profile",
            UserType = "runner",
            IdNumber = "8001015009087",
            DateOfBirth = new DateTime(1980, 1, 1)
        });

        var conflict = Assert.IsType<ConflictObjectResult>(action.Result);
        Assert.Contains("dedicated runner verification flow", JsonSerializer.Serialize(conflict.Value), StringComparison.OrdinalIgnoreCase);
        var persisted = await context.Users.SingleAsync(u => u.Id == 43);
        Assert.Null(persisted.IdNumber);
        Assert.Null(persisted.DateOfBirth);
    }

}
