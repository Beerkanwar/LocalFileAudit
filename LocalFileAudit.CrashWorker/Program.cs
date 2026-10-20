using Microsoft.Data.Sqlite;

namespace LocalFileAudit.CrashWorker;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Database path argument required.");
            return 1;
        }

        string dbPath = args[0];

        using var connection = new SqliteConnection($"Data Source={dbPath};");
        connection.Open();

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                PRAGMA journal_mode = 'wal';
                PRAGMA synchronous = NORMAL;
                CREATE TABLE IF NOT EXISTS CrashTestEntries (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Payload TEXT NOT NULL
                );
            ";
            cmd.ExecuteNonQuery();
        }

        // Commit 100 records in WAL mode
        using (var transaction = connection.BeginTransaction())
        {
            for (int i = 0; i < 100; i++)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "INSERT INTO CrashTestEntries (Payload) VALUES (@p);";
                cmd.Parameters.AddWithValue("@p", $"Entry_{i}_{Guid.NewGuid():N}");
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        // Signal to parent process that writes are committed and child is ready to be forcefully killed
        Console.WriteLine("COMMITTED_AND_READY");
        Console.Out.Flush();

        // Loop indefinitely without closing the connection cleanly until terminated via process kill
        while (true)
        {
            Thread.Sleep(50);
        }
    }
}
