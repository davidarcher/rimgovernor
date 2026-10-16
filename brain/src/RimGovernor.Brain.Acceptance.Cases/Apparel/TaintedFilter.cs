using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>apparel/tainted_filter</c> (#3285, epic #3217 Gate 1): the tainted-apparel choice is the coupled comparator's. A
/// content colonist whose only parka is a dead man's, in a cold snap, gets an outfit that allows tainted apparel after a
/// review: the comparator compared disallowing it (the DeadMansApparel card's labor-free candidate) and rejected it, so the
/// military/equipment Need writes the pawn's own outfit with AllowDeadmansApparel on.
/// </summary>
internal static class TaintedFilter
{
    private const string WearOp = "test/tainted_parka_wear";

    internal static AcceptanceCase Case() => new()
    {
        Name = "apparel/tainted_filter",
        Scope = "Issue #3285: after a review, a content colonist in a cold snap whose only parka is tainted is on its own outfit "
            + "and that outfit allows tainted apparel (the comparator rejected the DeadMansApparel card's disallow candidate), "
            + "with the parka still worn.",
        Start = new LabStart(),
        Serve = new ServeSpec { Families = ["gear", "work"], Prefix = "apparel-tainted-filter" },
        RequiredOps = [WearOp],
        Budget = TimeSpan.FromMinutes(6),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var worn = await s.Harness.CallAsync("wear-tainted-parka", WearOp, new JsonObject(), cancellationToken).ConfigureAwait(false);
        s.Report["worn"] = worn;
        if (worn["tainted"].AsNumber() != 1 || worn["bloodlust"].AsBool() != false || worn["outdoor"].AsNumber() >= 0)
        {
            throw new AcceptanceException($"the fixture did not stage a content colonist in a tainted parka in the cold: {worn.ToJson()}");
        }

        var pawn = worn["pawn"].AsString();
        var service = await ObservedSession.Of(s).ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
            service.KeepAuthority(cancellationToken);
            var journal = await ServiceJournal.OpenWithRetryAsync(s.Config, cancellationToken).ConfigureAwait(false);
            try
            {
                var wait = new Wait { Ceiling = TimeSpan.FromMinutes(5), Stall = Wait.StallBudget(), Interval = TimeSpan.FromSeconds(2), Terminal = service.Exited };
                await wait.WaitProgressAsync(
                    async ct =>
                    {
                        var allowed = await AllowedAsync(journal, pawn, ct).ConfigureAwait(false);
                        return allowed
                            ? ("an apparel policy allowing tainted apparel completed for the colonist", true)
                            : ("waiting for an apparel policy allowing tainted apparel", false);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                journal.Dispose();
            }
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }

        // The native filter after the service releases the game slot.
        var h = s.Reattach();
        var read = await h.CallAsync("outfits", "test/gear_fixture", new JsonObject { ["mode"] = "outfits" }, cancellationToken).ConfigureAwait(false);
        var row = read["pawns"].AsSlice().Select(r => r.AsMap()).FirstOrDefault(r => r?["pawn"].AsString() == pawn);
        s.Report["outfit"] = row?.DeepClone();
        if (row is null || row["label"].AsString() != row["shortName"].AsString() || row["tainted"].AsBool() != true || row["wornTainted"].AsNumber() != 1)
        {
            throw new AcceptanceException($"the colonist's own outfit does not allow the tainted parka it wears: {row.ToJson()}");
        }
    }

    /// <summary>Whether a completed apparel policy for <paramref name="pawn"/> allows tainted apparel.</summary>
    private static async Task<bool> AllowedAsync(Journal journal, string pawn, CancellationToken cancellationToken)
    {
        foreach (var plan in await ServiceJournal.PlanHistoryAsync(journal, "apparel-policy-*", cancellationToken).ConfigureAwait(false))
        {
            foreach (var progress in plan.Progress)
            {
                if (progress.Action.ApparelPolicy is { Spec: { AllowTainted: true } spec } && spec.Pawn.Value == pawn && progress.View.Stage == Stage.Completed)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
