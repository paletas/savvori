using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Savvori.WebApi;

/// <summary>
/// Safety net for startup migrations: before applying pending migrations to a database that
/// already holds data, take a consistent snapshot next to it so a bad migration is recoverable.
/// </summary>
public static class DatabaseBackup
{
    private const int KeepLatest = 10;

    /// <summary>Returns the backup path, or null when no backup was needed/possible (in-memory
    /// database, brand-new database, or nothing pending).</summary>
    public static string? BackupIfMigrationsPending(SavvoriDbContext db, ILogger logger)
    {
        var pending = db.Database.GetPendingMigrations().ToList();
        if (pending.Count == 0)
        {
            return null;
        }

        var connection = new SqliteConnectionStringBuilder(db.Database.GetConnectionString());
        var source = connection.DataSource;
        if (string.IsNullOrEmpty(source) || source == ":memory:" || connection.Mode == SqliteOpenMode.Memory)
        {
            return null;
        }

        source = Path.GetFullPath(source);
        // A database with no applied migrations is new (or empty); there is nothing to lose.
        if (!File.Exists(source) || new FileInfo(source).Length == 0 || !db.Database.GetAppliedMigrations().Any())
        {
            return null;
        }

        var directory = Path.Combine(Path.GetDirectoryName(source)!, "backups");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory,
            $"{Path.GetFileNameWithoutExtension(source)}-{DateTime.UtcNow:yyyyMMddHHmmss}-pre-{pending[0]}.db");

        // VACUUM INTO produces a consistent copy even while the database is open (WAL-safe).
        db.Database.OpenConnection();
        try
        {
            using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "VACUUM INTO $path";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$path";
            parameter.Value = target;
            command.Parameters.Add(parameter);
            command.ExecuteNonQuery();
        }
        finally
        {
            db.Database.CloseConnection();
        }

        logger.LogInformation("Backed up database to {Backup} before applying {Count} migration(s)", target, pending.Count);

        foreach (var old in new DirectoryInfo(directory).GetFiles("*-pre-*.db")
                     .OrderByDescending(f => f.Name).Skip(KeepLatest))
        {
            old.Delete();
        }

        return target;
    }
}
