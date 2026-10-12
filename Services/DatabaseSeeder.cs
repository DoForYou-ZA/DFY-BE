using DoForYou.API.Data;
using DoForYou.API.Models;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.API.Services;

public static class DatabaseSeeder
{
    public static async System.Threading.Tasks.Task SeedAsync(AppDbContext context)
    {
        // Seed Categories
        if (!await context.Categories.AnyAsync())
        {
            var categories = new[]
            {
                new Category { Name = "Cleaning", Description = "House cleaning, office cleaning", Icon = "fas fa-broom", SortOrder = 1 },
                new Category { Name = "Handyman", Description = "Repairs, maintenance, installations", Icon = "fas fa-tools", SortOrder = 2 },
                new Category { Name = "Transportation", Description = "Moving, delivery, driving", Icon = "fas fa-truck", SortOrder = 3 },
                new Category { Name = "Tutoring", Description = "Academic help, lessons", Icon = "fas fa-graduation-cap", SortOrder = 4 },
                new Category { Name = "Gardening", Description = "Lawn care, landscaping", Icon = "fas fa-seedling", SortOrder = 5 },
                new Category { Name = "Pet Care", Description = "Pet sitting, walking, grooming", Icon = "fas fa-paw", SortOrder = 6 },
                new Category { Name = "Technology", Description = "Computer help, setup, repair", Icon = "fas fa-laptop", SortOrder = 7 },
                new Category { Name = "General", Description = "Other miscellaneous tasks", Icon = "fas fa-tasks", SortOrder = 8 }
            };

            context.Categories.AddRange(categories);
            await context.SaveChangesAsync();
        }

        // Seed Admin User
        if (!await context.Users.AnyAsync(u => u.Email == "admin@doforyou.com"))
        {
            var adminUser = new User
            {
                FirstName = "Admin",
                LastName = "User",
                Email = "admin@doforyou.com",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("Admin123"),
                IsVerified = true,
                ProfileCompleted = true,
                EmailVerified = true,
                PhoneVerified = true,
                Roles = "Admin",
                UserType = "Admin",
                PhoneNumber = "0123456789",
                IdNumber = "0000000000000",
                Address = "DoForYou HQ",
                DateOfBirth = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };

            context.Users.Add(adminUser);
            await context.SaveChangesAsync();
        }
        else
        {
            // Patch existing admin to have full profile
            var admin = await context.Users.FirstAsync(u => u.Email == "admin@doforyou.com");
            if (!admin.ProfileCompleted || string.IsNullOrEmpty(admin.IdNumber))
            {
                admin.IdNumber = admin.IdNumber ?? "0000000000000";
                admin.Address = admin.Address ?? "DoForYou HQ";
                admin.DateOfBirth = admin.DateOfBirth ?? new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                admin.ProfileCompleted = true;
                await context.SaveChangesAsync();
            }
        }

        // Seed Test User
        if (!await context.Users.AnyAsync(u => u.Email == "2co.mokolare@gmail.com"))
        {
            var testUser = new User
            {
                FirstName = "Obakeng",
                LastName = "Mokolare",
                Email = "2co.mokolare@gmail.com",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("password123"),
                IsVerified = true,
                ProfileCompleted = true,
                EmailVerified = true,
                PhoneVerified = true,
                Roles = "User",
                UserType = "both",
                PhoneNumber = "0795258611",
                IdNumber = "9001015009087",
                Address = "123 Test Street, Johannesburg",
                DateOfBirth = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };

            context.Users.Add(testUser);
            await context.SaveChangesAsync();
        }
    }
}