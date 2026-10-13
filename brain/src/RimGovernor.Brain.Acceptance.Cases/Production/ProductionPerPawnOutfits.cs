using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>production/per-pawn-outfits</c> (Go <c>per_pawn_outfits.go</c>): MaintainEquipment puts every lab colonist on its own
/// outfit, labelled with its short name, then prunes every other outfit, the vanilla ones included (#1302). End-to-end signal
/// over the ApparelPolicyIntent and PolicyPruneIntent: the filters themselves are policy/apparel_policy_test.go.
/// </summary>
internal static class ProductionPerPawnOutfits
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "production/per-pawn-outfits",
        Scope = "MaintainEquipment puts every lab colonist on its own outfit, labelled with its short name, then prunes every "
            + "other outfit, the vanilla ones included (#1302). End-to-end signal over the ApparelPolicyIntent and "
            + "PolicyPruneIntent: the filters themselves are policy/apparel_policy_test.go.",
        Start = new LabStart(),
        Serve = new ServeSpec { Families = ["gear"] },
        Budget = TimeSpan.FromMinutes(4),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var service = await ObservedSession.Of(s).ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
            service.KeepAuthority(cancellationToken);
            var journal = await service.StoreAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var wait = new Wait { Ceiling = TimeSpan.FromMinutes(3), Stall = TimeSpan.FromSeconds(90), Interval = TimeSpan.FromSeconds(1), Terminal = service.Exited };
                await wait.WaitProgressAsync(
                    async ct =>
                    {
                        var plans = await journal.PlanHistoryAsync(256, "outfit-prune-*", ct).ConfigureAwait(false);
                        foreach (var plan in plans)
                        {
                            foreach (var progress in plan["progress"].AsSlice())
                            {
                                var row = progress.AsMap() ?? [];
                                if (row["kind"].AsString() == "policy_prune" && row["database"].AsString() == "outfit" && row["stage"].AsString() == "completed")
                                {
                                    return ("outfits pruned", true);
                                }
                            }
                        }

                        return ("waiting for per-pawn outfits and the outfit prune", false);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                (journal as IDisposable)?.Dispose();
            }
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }

        var h = await ObservedSession.Of(s).ReattachAsync(cancellationToken).ConfigureAwait(false);
        var read = await h.CallAsync("outfits", "test/gear_fixture", new JsonObject { ["mode"] = "outfits" }, cancellationToken).ConfigureAwait(false);
        s.Report["outfits"] = read;
        var held = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in read["pawns"].AsSlice())
        {
            var pawn = row.AsMap() ?? [];
            var outfit = pawn["outfit"].AsString();
            if (outfit.Length == 0 || pawn["label"].AsString() != pawn["shortName"].AsString())
            {
                throw new AcceptanceException($"pawn {pawn.ToJson()} is not on its own outfit");
            }

            if (!held.Add(outfit))
            {
                throw new AcceptanceException($"outfit {outfit} is shared");
            }
        }

        if (held.Count == 0)
        {
            throw new AcceptanceException("no colonists read");
        }

        foreach (var id in read["outfits"].AsSlice())
        {
            if (!held.Contains(id.AsString()))
            {
                throw new AcceptanceException($"outfit {id.ToJson()} survived the prune");
            }
        }
    }
}
