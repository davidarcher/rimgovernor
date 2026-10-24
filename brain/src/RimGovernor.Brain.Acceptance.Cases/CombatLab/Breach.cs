using System.Text.Json.Nodes;
using Mirror = RimGovernor.Contracts.Wire.Mirror;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>combatlab/breach</c> (#1149, #913-#915): a sapper raid staged against lab-breach's doorless walled room digs in,
/// and a frame's combat row reads a raider's job Mine while it stands against the wall outside the room.
/// </summary>
internal static class CombatLabBreach
{
    /// <summary>Bounds the walk to the wall and the first Mine job: the sappers stand 15 cells off it.</summary>
    private const int DigTicks = 3000 + (2 * CombatLabKit.ProbeTicks);

    private static readonly LabFixture Fixture = CombatLabKit.Fixture("lab-breach");

    internal static TypedCase Case() => new()
    {
        Name = "combatlab/breach",
        Scope = "The sapper Mine job on lab-breach (#1149, #913-#915): a sapper raid staged against a doorless walled room digs in, and a frame's "
            + "combat_pawns row reads a raider's job Mine while it stands against the wall outside the room. Native because the job is vanilla's "
            + "sapper AI digging, not a recorded fact.",
        Ticks = DigTicks,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Fixture.Colonists },
        Stage = CombatLabKit.Spec(Fixture),
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var lab = new StagedLab(Fixture, s.Staged!);
        var raiders = lab.Hostiles().ToHashSet(StringComparer.Ordinal);
        var centre = lab.Cell(0, 0);
        var jobs = new SortedDictionary<string, int>(StringComparer.Ordinal);
        Mirror.CombatPawn? miner = null;
        _ = await new LabFrames(s).UntilAsync("dig", DigTicks, 0, frame =>
        {
            foreach (var row in frame.CombatPawns.Where(r => raiders.Contains(r.Id.Value)))
            {
                jobs[row.Job ?? ""] = jobs.GetValueOrDefault(row.Job ?? "") + 1;

                // Mine walks to the wall first: digging is Mine against it, from a cell just outside the ring.
                if (row is { Job: "Mine", Cell: { } cell } && CombatLabKit.Chebyshev(cell, centre) == CombatFixtures.BreachHalf + 1)
                {
                    miner = row;
                }
            }

            return miner is not null;
        }, cancellationToken).ConfigureAwait(false);
        s.Record("jobs", new JsonObject([.. jobs.Select(j => new KeyValuePair<string, JsonNode?>(j.Key, j.Value))]));
        s.RecordWire("miner", miner);
    }
}
