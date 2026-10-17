using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>tools/deepresources</c>: seeded deep resource lumps aggregate by connected definition, both scanner types expose
/// their native state, and drills report exact deposit and depletion (#482, #538).
/// </summary>
internal static class ToolsDeepResources
{
    private const string SeedTool = "test/deep_resources_seed";

    private static readonly FixtureOp<string, Fx.DeepResourcesReply> Seed = new(
        SeedTool,
        _ => [],
        (label, reply) => reply is not null && Fx.FixtureJson.Deserialize<Fx.DeepResourcesReply>(reply.ToJsonString()) is { Success: true } read
            ? read
            : throw new AcceptanceException($"{label}: {SeedTool} refused: {reply?.ToJsonString()}"));

    internal static TypedCase Case() => new()
    {
        Name = "tools/deepresources",
        Scope = "colony facts deepResources over test/deep_resources_seed (no stage primitive writes the deep resource grid): "
            + "three Plasteel cells read as one 300-count lump, a separate 25-count Plasteel cell as another, a diagonal Steel "
            + "pair as one 70-count lump; both scanner types read built, unpowered, idle, with a positive time to next find; "
            + "the drill on the lump reads 100 Plasteel remaining and the drill over cleared ground reads depleted with no deposit.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Ops = [SeedTool],
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var seed = await s.FixtureAsync("seed", Seed, "", cancellationToken).ConfigureAwait(false);
        var (ex, ez) = (seed.X, seed.Z);
        var reply = await s.CallAsync("deep-resources", NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        var facts = TypedReads.Observed("deep-resources", NativeOps.ReadColonyFacts, reply, reply.Outcome?.Observed);
        var f = facts.DeepResources?.Outcome?.Observed ?? throw new AcceptanceException($"deep-resources: no observed census: {facts.DeepResources}");
        s.RecordWire("deep_resources", f);

        bool At(Common.Cell? cell, int x, int z) => cell?.X == x && cell.Z == z;
        var near = f.Lumps.Where(l => l.Centre is { X: { } x, Z: { } z } && Math.Abs(x - ex) <= 5 && Math.Abs(z - ez) <= 5).ToList();
        bool Lump(string def, int x, int z, long count, uint cells) => near.Any(l => l.DefName == def && At(l.Centre, x, z) && l.Count == count && l.CellCount == cells);
        if (near.Count != 3 || !Lump("Plasteel", ex, ez, 300, 3) || !Lump("Plasteel", ex + 4, ez, 25, 1) || !Lump("Steel", ex + 3, ez + 3, 70, 2))
        {
            throw new AcceptanceException($"lumps: want Plasteel 300x3 at the centre, Plasteel 25x1 at +4 and Steel 70x2 at +3,+3, got {string.Join("; ", near)}");
        }

        foreach (var (kind, rows) in new[] { ("ground", f.GroundScanners), ("long-range", f.LongRangeScanners) })
        {
            var scanners = rows.Where(r => r.Position?.X == ex - 12).ToList();
            if (scanners is not [{ Built: true, Powered: false, Working: false, TicksToNextFind: > 0 }])
            {
                throw new AcceptanceException($"{kind} scanner: want one built, unpowered, idle scanner at x {ex - 12} with a positive time to next find, got {string.Join("; ", rows)}");
            }
        }

        if (f.Drills.FirstOrDefault(d => d.DefName != "DeepDrill" || d.Depleted is null || d.Designated != false) is { } incomplete)
        {
            throw new AcceptanceException($"drills: incomplete row {incomplete}");
        }

        var yielding = f.Drills.FirstOrDefault(d => At(d.Position, ex, ez));
        if (yielding is not { Depleted: false, Resource: "Plasteel", Remaining: 100 })
        {
            throw new AcceptanceException($"drills: want the drill on the lump to read 100 Plasteel remaining, got {yielding}");
        }

        var depleted = f.Drills.FirstOrDefault(d => At(d.Position, ex, ez - 8));
        if (depleted is not { Depleted: true, Resource: null, Remaining: null })
        {
            throw new AcceptanceException($"drills: want the drill over cleared ground depleted with no deposit, got {depleted}");
        }
    }
}
