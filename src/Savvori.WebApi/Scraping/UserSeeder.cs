using Microsoft.EntityFrameworkCore;
using Savvori.Shared;

namespace Savvori.WebApi.Scraping;

public static class UserSeeder
{
    // Test credentials — development only, never use in production.
    private static readonly (string Email, string Password, bool IsAdmin)[] TestUsers =
    [
        ("admin@savvori.dev", "Admin123!", true),
        ("user@savvori.dev",  "User123!",  false)
    ];

    public static async Task SeedAsync(SavvoriDbContext db, ILogger logger)
    {
        if (!db.Database.IsRelational()) return;

        var seeded = 0;
        foreach (var (email, password, isAdmin) in TestUsers)
        {
            if (await db.Users.AnyAsync(u => u.Email == email)) continue;

            db.Users.Add(new User
            {
                Id           = Guid.NewGuid(),
                Email        = email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                IsAdmin      = isAdmin,
                CreatedAt    = DateTime.UtcNow,
                UpdatedAt    = DateTime.UtcNow
            });
            seeded++;
        }

        if (seeded > 0)
        {
            await db.SaveChangesAsync();
            logger.LogInformation("UserSeeder: seeded {Count} test user(s).", seeded);
        }
    }
}
