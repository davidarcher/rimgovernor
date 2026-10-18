using Microsoft.Data.Sqlite;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class RoundsLoadTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    [Fact]
    public void AnUnchangedPayloadIsDecodedOnceAndAnyOtherIsDecodedAndChecked()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), "rg-rounds-load-" + Guid.NewGuid().ToString("N") + ".db");
        RoundsResult Review(Journal journal, long tick) => journal.ReviewRounds(new RoundsRequest
        {
            Current = World, Tick = new(tick), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("a"), Revision = journal.LoadRounds(ct).Revision,
        }, ct);
        using var journal = Journal.Open(path, ct);
        _ = Review(journal, 100);
        var first = journal.LoadRounds(ct);
        Assert.Same(first, journal.LoadRounds(ct));
        _ = Review(journal, 110);
        var next = journal.LoadRounds(ct);
        Assert.NotSame(first, next);
        Assert.Equal(110, next.Tick.Value);
        Assert.Same(next, journal.LoadRounds(ct));
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var write = connection.CreateCommand();
            // Another connection's commit, valid JSON the codec would not have written: the open store sees the
            // new data_version, reads the row again and the re-encoding check refuses it.
            write.CommandText = "UPDATE rounds SET payload=json_set(payload,'$.Unknown',1)";
            Assert.Equal(1, write.ExecuteNonQuery());
        }
        Assert.ThrowsAny<Exception>(() => journal.LoadRounds(ct));
    }
}
