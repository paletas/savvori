using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Savvori.WebApi;

namespace Savvori.Web.Tests;

public sealed class DatabaseBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "savvori-backup-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "savvori.db");

    public DatabaseBackupTests() => Directory.CreateDirectory(_dir);

    private SavvoriDbContext CreateContext() => new(new DbContextOptionsBuilder<SavvoriDbContext>()
        .UseSqlite($"Data Source={DbPath};Pooling=False").Options);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void FreshDatabase_IsNotBackedUp()
    {
        using var db = CreateContext();

        Assert.Null(DatabaseBackup.BackupIfMigrationsPending(db, NullLogger.Instance));
    }

    [Fact]
    public void UpToDateDatabase_IsNotBackedUp()
    {
        using var db = CreateContext();
        db.Database.Migrate();

        Assert.Null(DatabaseBackup.BackupIfMigrationsPending(db, NullLogger.Instance));
    }

    [Fact]
    public void PendingMigrationOnExistingDatabase_IsBackedUpWithData()
    {
        using var db = CreateContext();
        db.Database.Migrate();
        // Simulate an older deployed schema: the latest migration is not yet recorded as applied.
        db.Database.ExecuteSqlRaw("DELETE FROM __EFMigrationsHistory");
        db.Database.ExecuteSqlRaw(
            "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('19990101000000_Old', '10.0.0')");

        var backup = DatabaseBackup.BackupIfMigrationsPending(db, NullLogger.Instance);

        Assert.NotNull(backup);
        Assert.StartsWith(Path.Combine(_dir, "backups"), backup);
        using var connection = new SqliteConnection($"Data Source={backup};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'ShoppingLists'";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public void OldBackups_AreTrimmed()
    {
        using var db = CreateContext();
        db.Database.Migrate();
        db.Database.ExecuteSqlRaw("DELETE FROM __EFMigrationsHistory");
        db.Database.ExecuteSqlRaw(
            "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('19990101000000_Old', '10.0.0')");
        var backups = Path.Combine(_dir, "backups");
        Directory.CreateDirectory(backups);
        for (var i = 0; i < 12; i++)
            File.WriteAllText(Path.Combine(backups, $"savvori-2000010100{i:00}00-pre-X.db"), "");

        DatabaseBackup.BackupIfMigrationsPending(db, NullLogger.Instance);

        Assert.Equal(10, Directory.GetFiles(backups, "*-pre-*.db").Length);
    }
}
