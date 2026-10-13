using Microsoft.Data.Sqlite;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The <see cref="StoreSnapshotter"/> of a run: copies a case's <c>service.sqlite</c> into a checkpoint bundle through
/// SQLite's online backup (Go <c>store.Open</c> + <c>Snapshot</c>). The source is opened read-only on a connection of
/// its own, so the service that holds the file open (the game process) keeps writing and the copy is one consistent
/// point; the journal's schema is not checked, a bundle keeps whatever the service wrote.
/// </summary>
public static class StoreBackup
{
    /// <summary>Copies <paramref name="source"/> to <paramref name="destination"/>; the destination's directory exists.</summary>
    public static Task SnapshotAsync(string source, string destination, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => Snapshot(source, destination, cancellationToken), cancellationToken);
    }

    private static void Snapshot(string source, string destination, CancellationToken cancellationToken)
    {
        if (File.Exists(destination))
        {
            throw new AcceptanceException($"store snapshot: {destination} already exists");
        }

        Postmortems.EnsureProvider();
        try
        {
            using var from = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = Path.GetFullPath(source),
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                    DefaultTimeout = 10,
                }.ToString());
            from.Open();
            cancellationToken.ThrowIfCancellationRequested();
            using var to = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = Path.GetFullPath(destination),
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false,
                }.ToString());
            to.Open();
            from.BackupDatabase(to);
        }
        catch (Exception e) when (e is SqliteException or InvalidOperationException or IOException)
        {
            throw new AcceptanceException($"store snapshot {source}: {e.Message}", e);
        }
    }
}
