using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>mood/gathering</c> (Go <c>gathering.go</c>): HoldGatherings starts exactly one party after a colony-wide mood dip
/// (#2549, #2548). Native because only RimWorld's lord manager shows that a gathering exists.
/// </summary>
internal static class Gathering
{
    private const string PartySpotOp = "test/party_spot";
    private const string LordsOp = "test/gathering_lords";
    private const double DipLoss = 9.0;

    internal static AcceptanceCase Case() => new()
    {
        Name = "mood/gathering",
        Scope = "HoldGatherings starts exactly one party after a colony-wide mood dip (#2549, #2548): five Core-only colonists "
            + "each carrying a 9-point negative memory (at least the 8 points AttendedParty repays) and a built PartySpot, no "
            + "AttendedParty memory anywhere. The journal records one completed gathering action and no second; a native read "
            + "counts exactly one running lord job of the Party GatheringDef whose spot is the PartySpot, read while the party runs. "
            + "Native because only RimWorld's lord manager shows that a gathering exists.",
        Start = new LabStart { Colonists = 5 },
        Serve = new ServeSpec { Families = [.. MoodDrive.EpicFamilies("gathering")], Prefix = "mood-gathering" },
        RequiredOps = [Ledger.ThoughtsOp, PartySpotOp, LordsOp],
        Budget = TimeSpan.FromMinutes(8),
        Crew = new Crew { Size = 5 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var spot = await MoodDrive.FixtureCallAsync(h, "party-spot", PartySpotOp, null, cancellationToken).ConfigureAwait(false);
        report["party_spot"] = spot.DeepClone();
        var dip = await MoodDrive.FixtureCallAsync(h, "dip", Ledger.ThoughtsOp, new JsonObject { ["pawn"] = string.Empty, ["thought"] = Ledger.FreeThought, ["loss"] = DipLoss }, cancellationToken).ConfigureAwait(false);
        report["dip"] = dip.DeepClone();
        foreach (var row in dip["pawns"].Rows())
        {
            var found = false;
            foreach (var memory in row["memories"].Rows())
            {
                found = found || (memory["def"].AsString() == Ledger.FreeThought && MoodDrive.Near(memory["offset"].AsNumber(), -DipLoss, .05));
            }

            if (!found)
            {
                throw new AcceptanceException($"a colonist lacks the {DipLoss.ToString("F0", CultureInfo.InvariantCulture)}-point dip: {row.ToJson()}");
            }
        }

        // The preconditions the case needs, read natively before the controller starts: a refused gathering then names
        // its gate, never the planner.
        var before = await MoodDrive.FixtureCallAsync(h, "lords-before", LordsOp, new JsonObject { ["def"] = PolicyFunctions.PartyGatheringDef }, cancellationToken).ConfigureAwait(false);
        report["lords_before"] = before.DeepClone();
        if (before["colonists"].AsNumber() < PolicyFunctions.GatheringMinColonists || before["spots"].AsNumber() != 1
            || before["lords"].AsNumber() != 0 || before["attendedMemories"].AsNumber() != 0)
        {
            throw new AcceptanceException($"gathering fixture is not set: {before.ToJson()}");
        }

        if (!before["canExecute"].Flag())
        {
            throw new AcceptanceException($"the game cannot hold a Party now (hour {before["hour"].ToText()}, game conditions ok: {before["acceptable"].ToText()}): {before.ToJson()}");
        }

        var d = new MoodDrive(s);
        await d.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // gatherings are the gathering plans the journal holds, by completion.
            async Task<(int Completed, int Other)> GatheringsAsync(CancellationToken ct)
            {
                var store = await d.JournalAsync(ct).ConfigureAwait(false);
                var plans = await store.PlanHistoryAsync(256, "gathering-*", ct).ConfigureAwait(false);
                var completed = 0;
                var other = 0;
                foreach (var plan in plans)
                {
                    foreach (var progress in plan["progress"].Rows())
                    {
                        if (!string.Equals(progress["kind"].AsString(), "gathering", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (progress["stage"].AsString() == "completed")
                        {
                            completed++;
                        }
                        else
                        {
                            other++;
                        }
                    }
                }

                return (completed, other);
            }

            await d.WaitAsync(
                TimeSpan.FromMinutes(5),
                async ct =>
                {
                    var (completed, other) = await GatheringsAsync(ct).ConfigureAwait(false);
                    return (string.Create(CultureInfo.InvariantCulture, $"{completed} {other}"), completed > 0);
                },
                cancellationToken).ConfigureAwait(false);

            // The journal holds one gathering action and no second. The native read follows at once, while the party
            // runs: its lord job ends on its own duration, so a later read finds none, and stopping the service closes
            // the journal.
            var (doneCount, otherCount) = await GatheringsAsync(cancellationToken).ConfigureAwait(false);
            if (doneCount + otherCount != 1)
            {
                throw new AcceptanceException($"{doneCount} completed and {otherCount} other gathering actions, want exactly one");
            }
        }
        finally
        {
            await d.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        // One lord job of the Party def, on the PartySpot.
        h = await d.NativeAsync("lords", cancellationToken).ConfigureAwait(false);
        var after = await MoodDrive.FixtureCallAsync(h, "lords-after", LordsOp, new JsonObject { ["def"] = PolicyFunctions.PartyGatheringDef }, cancellationToken).ConfigureAwait(false);
        report["lords_after"] = after.DeepClone();
        if (after["lords"].AsNumber() != 1)
        {
            throw new AcceptanceException($"expected exactly one running Party lord job, got {after["lords"].ToText()}: {after.ToJson()}");
        }

        if (after["spotX"].AsNumber() != spot["x"].AsNumber() || after["spotZ"].AsNumber() != spot["z"].AsNumber())
        {
            throw new AcceptanceException($"the party is not on the PartySpot {spot["x"].ToText()},{spot["z"].ToText()}: {after.ToJson()}");
        }

        if (after["organizer"].AsString().Length == 0)
        {
            throw new AcceptanceException($"the party has no organizer: {after.ToJson()}");
        }
    }
}
