using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>wall/tier-gate</c> (Go <c>tier_gate.go</c>).</summary>
internal static class WallTierGate
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "wall/tier-gate",
        Scope = "Native material delivery by construction tier (#2523): with five steel, a tier-0 steel wall is handed the delivery job and a tier-5 steel wall is not; a tier-5 wood wall and an untiered steel wall are unaffected; " +
            "set-tier to 0 lets the tier-5 site compete; an unreachable tier-0 site does not block; with the supervisor inactive nothing is gated. A Go snapshot cannot prove the Harmony patch on the vanilla delivery work-giver.",
        Start = new LabStart(),
        RequiredOps = ["test/construction_tier_gate"],
        Budget = TimeSpan.FromMinutes(3),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static readonly string[] SiteKeys = ["a", "b", "c", "d"];

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;

        async Task<JsonObject> Call(string label, string action)
        {
            var row = await h.CallAsync(label, "test/construction_tier_gate", new JsonObject { ["action"] = action }, cancellationToken).ConfigureAwait(false);
            if (row["success"].AsBool() != true)
            {
                throw new AcceptanceException($"construction_tier_gate {action} refused: {row.ToJson()}");
            }

            s.Report[label] = row;
            return row;
        }

        var prepared = await Call("tier-prepare", "prepare").ConfigureAwait(false);
        var sites = prepared["sites"].AsSlice();
        if (sites.Count != 4)
        {
            throw new AcceptanceException($"expected four staged sites: {prepared.ToJson()}");
        }

        var bSite = sites[1].AsMap() ?? [];
        var inactive = await Call("tier-inactive", "query").ConfigureAwait(false);
        if (inactive["supervisorActive"].AsBool() == true)
        {
            throw new AcceptanceException($"supervisor must be inactive before authority is granted: {inactive.ToJson()}");
        }

        Expect("inactive supervisor gates nothing", inactive, [true, true, true, true]);

        var id = s.Identity;
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "tier-gate-authority", id, cancellationToken).ConfigureAwait(false);
        var gated = await Call("tier-gated", "query").ConfigureAwait(false);
        if (gated["supervisorActive"].AsBool() != true)
        {
            throw new AcceptanceException($"supervisor not active: {gated.ToJson()}");
        }

        // Tier 0 steel first; tier 5 steel waits; wood and untiered are free.
        Expect("tier 0 first", gated, [true, false, true, true]);

        async Task SetTier(string label, int tier)
        {
            var placement = new JsonObject
            {
                ["defName"] = "Wall",
                ["stuff"] = "Steel",
                ["x"] = bSite["x"]?.DeepClone(),
                ["z"] = bSite["z"]?.DeepClone(),
                ["rotation"] = "ROTATION_NORTH",
            };
            var building = new JsonObject { ["placement"] = placement, ["tier"] = tier, ["existingTargetId"] = bSite["target"]?.DeepClone() };
            var result = await ApplyIntents.ApplyOneAsync(h, label, id, label, new JsonObject { ["building"] = building }, cancellationToken).ConfigureAwait(false);
            if (result["applied"].AsMap() is null)
            {
                throw new AcceptanceException($"{label} not applied: {result.ToJson()}");
            }
        }

        await SetTier("tier-raise", 0).ConfigureAwait(false);
        var equal = await Call("tier-equal", "query").ConfigureAwait(false);
        Expect("equal tiers compete", equal, [true, true, true, true]);

        await SetTier("tier-lower-again", 5).ConfigureAwait(false);
        var enclosed = await Call("tier-enclosed", "enclose").ConfigureAwait(false);
        if (enclosed["a"].Member("reachable").AsBool() == true)
        {
            throw new AcceptanceException($"enclosed tier-0 site must be unreachable: {enclosed.ToJson()}");
        }

        Expect("an unreachable tier-0 site does not block", enclosed, [false, true, true, true]);
    }

    /// <summary>Each site reports the job the delivery work-giver hands the one builder, or none.</summary>
    private static bool HasJob(JsonObject row, string site) => row[site].Member("job").AsString() == "HaulToContainer";

    private static void Expect(string label, JsonObject row, bool[] want)
    {
        for (var i = 0; i < SiteKeys.Length; i++)
        {
            if (HasJob(row, SiteKeys[i]) != want[i])
            {
                throw new AcceptanceException($"{label}: site {SiteKeys[i]} delivery job = {(HasJob(row, SiteKeys[i]) ? "true" : "false")}, want {(want[i] ? "true" : "false")}: {row.ToJson()}");
            }
        }
    }
}
