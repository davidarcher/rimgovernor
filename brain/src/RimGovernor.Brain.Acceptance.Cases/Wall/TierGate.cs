using System.Text.Json.Nodes;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>wall/tier-gate</c> (#2523): native material delivery by construction tier. With five steel, a tier-0 steel wall is
/// handed the delivery job and a tier-5 steel wall is not, while a tier-5 wood wall and an untiered steel wall are
/// unaffected; set-tier to 0 lets the tier-5 site compete; an unreachable tier-0 site does not block; with the supervisor
/// inactive nothing is gated. Native: the Harmony patch on the vanilla delivery work-giver, which
/// <c>test/construction_tier_gate</c> stages and queries in the lab.
/// </summary>
internal static class WallTierGate
{
    private const string Tool = "test/construction_tier_gate";

    private static readonly FixtureOp<string, Fx.TierGateReply> Gate = new(
        Tool,
        action => WallKit.Args(("action", action)),
        (label, reply) => reply is not null && Fx.FixtureJson.Deserialize<Fx.TierGateReply>(reply.ToJsonString()) is { Success: true } read
            ? read
            : throw new AcceptanceException($"{label}: {Tool} refused: {reply?.ToJsonString()}"));

    internal static TypedCase Case() => new()
    {
        Name = "wall/tier-gate",
        Scope = "Native delivery by construction tier (#2523): with five steel a tier-0 steel wall gets the delivery job and a tier-5 one "
            + "does not, wood and untiered walls are unaffected; set-tier 0 lets the tier-5 site compete; an unreachable tier-0 site does "
            + "not block; an inactive supervisor gates nothing.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        QuietWorld = true,
        Ops = [Tool],
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var prepared = await s.FixtureAsync("tier-prepare", Gate, "prepare", cancellationToken).ConfigureAwait(false);
        if (prepared.Sites.Count != 4)
        {
            throw new AcceptanceException($"want four staged sites: {prepared}");
        }

        var b = prepared.Sites[1];
        var inactive = await s.FixtureAsync("tier-inactive", Gate, "query", cancellationToken).ConfigureAwait(false);
        if (inactive.SupervisorActive)
        {
            throw new AcceptanceException($"the supervisor must be inactive before authority is granted: {inactive}");
        }

        Expect("an inactive supervisor gates nothing", inactive, [true, true, true, true]);

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        var gated = await s.FixtureAsync("tier-gated", Gate, "query", cancellationToken).ConfigureAwait(false);
        if (!gated.SupervisorActive)
        {
            throw new AcceptanceException($"supervisor not active: {gated}");
        }

        // Tier 0 steel first; tier 5 steel waits; wood and untiered are free.
        Expect("tier 0 first", gated, [true, false, true, true]);

        RimGovernor.Brain.GameOrders.Operations.BuildingIntent SetTier(int tier) => new()
        {
            Placement = new RimGovernor.Brain.GameOrders.Placement.PlacementCandidate { DefName = "Wall", Stuff = "Steel", X = b.X, Z = b.Z, Rotation = RimGovernor.Brain.GameFacts.Placement.Rotation.North, IgnoreNaturalRock = false },
            Tier = new RimGovernor.Brain.GameOrders.Operations.ConstructionTierChange.Selected(tier),
            ExistingTargetId = b.Target, ReplaceWall = false, MinimumFinishingSkill = new RimGovernor.Brain.GameOrders.Operations.FinishingSkillChange.Preserve(),
        };

        _ = await TypedApply.AppliedAsync(s, "tier-raise", SetTier(0), cancellationToken).ConfigureAwait(false);
        Expect("equal tiers compete", await s.FixtureAsync("tier-equal", Gate, "query", cancellationToken).ConfigureAwait(false), [true, true, true, true]);

        _ = await TypedApply.AppliedAsync(s, "tier-lower-again", SetTier(5), cancellationToken).ConfigureAwait(false);
        var enclosed = await s.FixtureAsync("tier-enclosed", Gate, "enclose", cancellationToken).ConfigureAwait(false);
        if (enclosed.A?.Reachable != false)
        {
            throw new AcceptanceException($"the enclosed tier-0 site must be unreachable: {enclosed}");
        }

        Expect("an unreachable tier-0 site does not block", enclosed, [false, true, true, true]);
    }

    /// <summary>Each site has the delivery job the work-giver hands the one builder, or none.</summary>
    private static void Expect(string label, Fx.TierGateReply row, bool[] want)
    {
        var jobs = new[] { row.A, row.B, row.C, row.D };
        for (var i = 0; i < jobs.Length; i++)
        {
            var has = jobs[i]?.Job == "HaulToContainer";
            if (has != want[i])
            {
                throw new AcceptanceException($"{label}: site {"abcd"[i]} delivery job = {has}, want {want[i]}: {row}");
            }
        }
    }
}
