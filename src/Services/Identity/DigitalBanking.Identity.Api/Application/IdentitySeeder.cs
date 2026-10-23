using DigitalBanking.Contracts;
using DigitalBanking.Identity.Api.Domain;
using DigitalBanking.Identity.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Identity.Api.Application;

public static class IdentitySeeder
{
    public static async Task SeedAsync(IdentityDbContext dbContext, PasswordHasher<User> passwordHasher)
    {
        await EnsureColumnAsync(dbContext);
        await EnsureUserAsync(dbContext, passwordHasher, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "admin@digitalbank.local", "Bank Admin", UserRoles.Admin, "Admin@12345");
        await EnsureUserAsync(dbContext, passwordHasher, Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "internal@digitalbank.local", "Ayesha Malik", UserRoles.InternalEmployee, "Internal@12345");
        await EnsureUserAsync(dbContext, passwordHasher, Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), "external@digitalbank.local", "Omar Sheikh", UserRoles.ExternalEmployee, "External@12345");
        await EnsureUserAsync(dbContext, passwordHasher, Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb2"), "internal2@digitalbank.local", "Bilal Ahmed", UserRoles.InternalEmployee, "Internal@12345");
        await EnsureUserAsync(dbContext, passwordHasher, Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"), "customer@digitalbank.local", "Demo Customer", UserRoles.Customer, "Customer@12345");
        await EnsureUserAsync(dbContext, passwordHasher, Guid.Parse("dddddddd-dddd-dddd-dddd-ddddddddddd2"), "ali.raza@digitalbank.local", "Ali Raza", UserRoles.Customer, "Customer@12345");
        await EnsureUserAsync(dbContext, passwordHasher, Guid.Parse("dddddddd-dddd-dddd-dddd-ddddddddddd3"), "sara.khan@digitalbank.local", "Sara Khan", UserRoles.Customer, "Customer@12345");
        await dbContext.SaveChangesAsync();
    }

    private static async Task EnsureColumnAsync(IdentityDbContext dbContext)
    {
        await dbContext.Database.ExecuteSqlRawAsync("""
            IF COL_LENGTH('[identity].[Users]', 'FullName') IS NULL
            ALTER TABLE [identity].[Users] ADD FullName nvarchar(200) NOT NULL CONSTRAINT DF_Users_FullName DEFAULT '';
            """);
    }

    private static async Task EnsureUserAsync(
        IdentityDbContext dbContext,
        PasswordHasher<User> passwordHasher,
        Guid userId,
        string email,
        string fullName,
        string role,
        string password)
    {
        var existing = await dbContext.Users.FirstOrDefaultAsync(user => user.Email == email);
        if (existing is null)
        {
            var user = new User
            {
                Id = userId,
                Email = email,
                FullName = fullName,
                Role = role,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow
            };
            user.PasswordHash = passwordHasher.HashPassword(user, password);
            dbContext.Users.Add(user);
            return;
        }

        if (string.IsNullOrWhiteSpace(existing.FullName))
        {
            existing.FullName = fullName;
        }
    }
}
