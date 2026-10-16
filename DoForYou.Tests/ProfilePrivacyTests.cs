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
        Assert.Contains("\"hasIdNumber\":true", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"idNumberLastFour\":\"9087\"", json, StringComparison.OrdinalIgnoreCase);
    }
}
