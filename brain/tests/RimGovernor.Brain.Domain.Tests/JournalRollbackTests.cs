using System.Data;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class JournalRollbackTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig : IDisposable
    {
        public readonly SqliteConnection Connection;
        public readonly Journal Journal;

        public Rig()
        {
            var path = Path.Combine(Path.GetTempPath(), "rg-rollback-" + Guid.NewGuid().ToString("N") + ".db");
            // Initialize the real schema and SQLite provider, then own a connection whose
            // authorizer can reproduce a real SQLite rollback error without a test hook.
            using (Journal.Open(path, Ct)) { }
            Connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            Connection.Open();
            Journal = new Journal(Connection, path);
        }

        public void DenyRollback()
        {
            strdelegate_authorizer authorize = (_, action, operation, _, _, _) =>
                action == raw.SQLITE_TRANSACTION && operation == "ROLLBACK" ? raw.SQLITE_DENY : raw.SQLITE_OK;
            Assert.Equal(raw.SQLITE_OK, raw.sqlite3_set_authorizer(Connection.Handle!, authorize, this));
        }

        public void Dispose() => Journal.Dispose();
    }

    [Fact]
    public void AlreadyEndedTransactionReleasesGateAndAllowsTheNextTransaction()
    {
        using var rig = new Rig();
        var tx = rig.Journal.Begin(Ct);
        tx.Execute("ROLLBACK");
        tx.Dispose();
        tx.Dispose();
        Assert.Equal(7, rig.Journal.Run(t => t.Scalar("SELECT 7"), Ct));
    }

    [Fact]
    public void RollbackIgnoresCallerCancellationAndDiscardsUncommittedWrites()
    {
        using var rig = new Rig();
        rig.Journal.Run(t => t.Execute("CREATE TABLE rollback_probe(value INTEGER)"), Ct);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var tx = rig.Journal.Begin(caller.Token);
        tx.Execute("INSERT INTO rollback_probe VALUES(1)");
        caller.Cancel();
        tx.Dispose();
        Assert.Equal(0, rig.Journal.Run(t => t.Scalar("SELECT COUNT(*) FROM rollback_probe"), Ct));
    }

    [Fact]
    public void RealRollbackFailureClosesConnectionAndReleasesGate()
    {
        using var rig = new Rig();
        var tx = rig.Journal.Begin(Ct);
        rig.DenyRollback();
        Assert.Throws<SqliteException>(tx.Dispose);
        Assert.Equal(ConnectionState.Closed, rig.Connection.State);
        tx.Dispose();
        // If the gate leaked this would wait for the test cancellation instead of
        // immediately refusing the poisoned connection.
        Assert.Throws<InvalidOperationException>(() => rig.Journal.Begin(Ct));
    }

    [Fact]
    public void OperationFailureRemainsFirstWhenRollbackAlsoFails()
    {
        using var rig = new Rig();
        var primary = new IOException("operation failed");
        rig.DenyRollback();
        var failure = Assert.Throws<AggregateException>(() => rig.Journal.Run<int>(_ => throw primary, Ct));
        Assert.Same(primary, failure.InnerExceptions[0]);
        Assert.IsType<SqliteException>(failure.InnerExceptions[1]);
        Assert.Equal(ConnectionState.Closed, rig.Connection.State);
    }
}
