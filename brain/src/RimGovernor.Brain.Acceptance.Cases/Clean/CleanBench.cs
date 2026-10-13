using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>clean/bench</c> (Go <c>bench.go</c>): opportunistic cleaning before a bill (#2515). With eight old filth beside a
/// fuelled bench, the patched native WorkGiver_DoBill hands a normal pawn a Clean job capped at five filth, while a pawn
/// with Cleaning at priority 0 and a drafted pawn still get the bill.
/// </summary>
internal static class CleanBench
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "clean/bench",
        Scope = "Opportunistic cleaning before a bill (#2515): with eight old filth beside a fuelled bench, the patched native "
            + "WorkGiver_DoBill hands a normal pawn a Clean job capped at five filth, while a pawn with Cleaning at priority 0 "
            + "and a drafted pawn still get the bill. A Go snapshot cannot prove the Harmony patch on vanilla job selection.",
        Start = new LabStart(),
        RequiredOps = ["test/bench_cleaning"],
        Budget = TimeSpan.FromMinutes(3),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "bench-cleaning-authority", s.Identity, cancellationToken).ConfigureAwait(false);
        var row = await h.CallAsync("bench-cleaning", "test/bench_cleaning", [], cancellationToken).ConfigureAwait(false);
        s.Report["probe"] = row.DeepClone();
        if (row["success"].AsBool() != true)
        {
            throw new AcceptanceException($"bench_cleaning refused: {row.ToJson()}");
        }

        if (row["supervisorActive"].AsBool() != true)
        {
            throw new AcceptanceException($"supervisor not active: {row.ToJson()}");
        }

        if (row["cleanBefore"].AsString() != "DoBill")
        {
            throw new AcceptanceException($"a clean bench must start the bill: {row.ToJson()}");
        }

        if (row["normal"].AsString() != "Clean" || row["normalTargets"].AsNumber() != 5)
        {
            throw new AcceptanceException($"a normal pawn must clean exactly the five-filth cap first: {row.ToJson()}");
        }

        foreach (var who in new[] { "disabled", "drafted" })
        {
            if (row[who].AsString() != "DoBill" || row[who + "Targets"].AsNumber() != 0)
            {
                throw new AcceptanceException($"the {who} pawn must go straight to the bill: {row.ToJson()}");
            }
        }
    }
}
