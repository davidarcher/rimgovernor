using System.Text.Json.Nodes;
using Fx = RimGovernor.Fixtures.Contracts;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Placement = RimGovernor.Contracts.Wire.Placement;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>wall/construction-skill</c> (#2522): a BuildingIntent's finishing-skill floor and construction tier on a bed
/// blueprint transfer to its frame and survive a save and reload; a stale blueprint id is refused, set-tier re-tiers the
/// frame and the floor is write-once. Low-skill finishing and direct completion are refused while a funded wall
/// progresses through vanilla work, equal-skilled completion succeeds and a same-cell replacement inherits nothing.
/// Native: the Harmony work-giver hooks, save references, vanilla conversion and pawn construction, which
/// <c>test/construction_skill</c> stages and probes in the lab (two capable colonists of set skills).
/// </summary>
internal static class WallConstructionSkill
{
    private const string Tool = "test/construction_skill";

    private static readonly FixtureOp<string, Fx.ConstructionSkillReply> Skill = new(
        Tool,
        action => WallKit.Args(("action", action)),
        (label, reply) => reply is not null && Fx.FixtureJson.Deserialize<Fx.ConstructionSkillReply>(reply.ToJsonString()) is { Success: true } read
            ? read
            : throw new AcceptanceException($"{label}: {Tool} refused: {reply?.ToJsonString()}"));

    internal static TypedCase Case() => new()
    {
        Name = "wall/construction-skill",
        Scope = "A BuildingIntent's finishing floor 9 and tier 3 on a bed blueprint transfer to the frame and survive save/load; a stale "
            + "blueprint id is refused, set-tier re-tiers the frame and the floor is write-once; low-skill finishing and completion are "
            + "refused while a funded wall progresses, equal-skilled completion succeeds and a same-cell replacement inherits nothing.",
        Ticks = 6000,
        Tier = CaseTier.OnDemand,
        QuietWorld = true,
        Ops = [Tool],
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var prepared = await s.FixtureAsync("skill-prepare", Skill, "prepare", cancellationToken).ConfigureAwait(false);
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        Operations.BuildingIntent Bed(string target, int? minimum, int tier) => new()
        {
            Placement = new Placement.PlacementCandidate { DefName = "Bed", Stuff = "WoodLog", X = prepared.X, Z = prepared.Z, Rotation = Placement.Rotation.North, IgnoreNaturalRock = false },
            MinimumFinishingSkill = minimum,
            Tier = tier,
            ExistingTargetId = target,
        };

        _ = await TypedApply.AppliedAsync(s, "skill-minimum", Bed(prepared.Target, 9, 3), cancellationToken).ConfigureAwait(false);
        var converted = await s.FixtureAsync("skill-convert", Skill, "convert", cancellationToken).ConfigureAwait(false);
        if (converted is not { Minimum: 9, Tier: 3 })
        {
            throw new AcceptanceException($"conversion lost the minimum or tier: {converted}");
        }

        // The blueprint id is stale once the frame stands; a different tier on the frame applies; the write-once floor refuses a change.
        var stale = await TypedApply.RefusedAsync(s, "skill-tier-stale", Bed(prepared.Target, null, 1), cancellationToken).ConfigureAwait(false);
        _ = await TypedApply.AppliedAsync(s, "skill-tier-set", Bed(converted.Target, null, 1), cancellationToken).ConfigureAwait(false);
        var floor = await TypedApply.RefusedAsync(s, "skill-floor-change", Bed(converted.Target, 10, 1), cancellationToken).ConfigureAwait(false);
        if (stale.Reason?.Contains("stale", StringComparison.Ordinal) != true || floor.Reason?.Contains("already set", StringComparison.Ordinal) != true)
        {
            throw new AcceptanceException($"want the stale-target and write-once refusals, got {stale} and {floor}");
        }

        var audit = await s.FixtureAsync("skill-audit", Skill, "audit", cancellationToken).ConfigureAwait(false);
        if (audit is not { Tier: 1, Minimum: 9 })
        {
            throw new AcceptanceException($"set-tier or floor readback: {audit}");
        }

        await s.ReloadAsync("skill-reload", cancellationToken).ConfigureAwait(false);
        _ = await s.RunUntilAsync("wall-progress", s.TicksLeft, async ct =>
        {
            var row = await s.FixtureAsync("skill-progress", Skill, "audit", ct).ConfigureAwait(false);
            if (row is not { Minimum: 9, Tier: 1, LowRefused: true, HighAllowed: true, BedPending: true })
            {
                throw new AcceptanceException($"after the reload: want minimum 9, tier 1, low refused, high allowed and the bed pending, got {row}");
            }

            return (Wait.Signature(row.WallProgress), row.WallProgress);
        }, cancellationToken).ConfigureAwait(false);

        var controls = await s.FixtureAsync("skill-controls", Skill, "controls", cancellationToken).ConfigureAwait(false);
        s.RecordFixture("construction_skill", controls);
        if (controls is not { LowRefused: true, LowCompletionRefused: true, Completed: true, NoLeak: true })
        {
            throw new AcceptanceException($"completion controls: {controls}");
        }
    }
}
