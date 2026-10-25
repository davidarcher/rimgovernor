using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>odyssey/lava-field</c> (#1719): a colony starts beside a 16x16 field of the game's own deep lava and builds clear
/// of it: once the served colony has laid zones, every field cell still reads as lava and no zone, no player building
/// and no colonist stands on one. The planner's refusal
/// of hazard cells replays offline in policy TestZoningKeepsHazardOffCoreFieldPasture; no eruption is fired.
/// </summary>
internal static class OdysseyLavaField
{
    private const string Lava = "LavaDeep";
    private const int Size = 16;

    internal static TypedCase Case() => new()
    {
        Name = "odyssey/lava-field",
        Scope = "A 16x16 field of the game's own LavaDeep beside the colonists stays lava, and once the served "
            + "colony has laid zones no growing or pasture zone, no player building and no colonist stands on a lava cell.",
        Ticks = 10_000,
        Tier = CaseTier.OnDemand,
        Expansions = ["ludeon.rimworld.odyssey"],
        NoKeep = true,
        Keep = ["Food"],
        Stage = new Fx.StageSpec
        {
            // East of the first colonist: beside the start, inside the layout plan's reach.
            Site = new Fx.StageSite { Width = Size, Height = Size, Near = Fx.StageAnchor.Colonist, OffsetX = 5, OffsetZ = -Size / 2 },
            Cells = [new() { Rect = new(0, 0, Size, Size), Terrain = Lava }],
        },
        Serve = new ServeSpec
        {
            Families = ["shelter", "field"],
            NativeTimeout = TimeSpan.FromSeconds(30),
            Prefix = "odyssey-lava-field",
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var site = s.Staged!.Site ?? throw new AcceptanceException($"stage: no site: {ServedOps.Json(s.Staged)}");

        // The served colony lays its layout and fields.
        _ = await s.ServeUntilAsync("zoned", s.TicksLeft, async ct =>
        {
            var zones = await s.ZonesAsync("zones", ct).ConfigureAwait(false);
            return ($"{zones.Count} zones", zones.Count > 0);
        }, cancellationToken).ConfigureAwait(false);

        var reply = await s.CallAsync("lava-cells", NativeOps.GetCells, new RimGovernor.Brain.GameOrders.Observations.GetCellsRequest
        {
            Scope = s.Scope,
            Rectangle = new RimGovernor.Brain.GameFacts.Observations.Rectangle
            {
                Minimum = new RimGovernor.Brain.Domain.Cell { X = site.X, Z = site.Z },
                Maximum = new RimGovernor.Brain.Domain.Cell { X = site.X + site.Width - 1, Z = site.Z + site.Height - 1 },
            },
        }, cancellationToken).ConfigureAwait(false);
        var grid = MapCellGrid.Of(reply.Grid);
        var cells = grid.Cells();
        var things = grid.Things();
        var notLava = cells.Where(c => c.Terrain != Lava).Select(c => c.Cell).ToList();
        var zoned = grid.ZoneCells().Values.SelectMany(z => z).ToList();
        var built = cells.Where((_, i) => things[i].Any(t => t.Has(P.ThingMarks.Edifice) && t.Faction == P.ThingFaction.Player)).Select(c => c.Cell).ToList();
        s.Record("lava", ServedOps.Args(("cells", cells.Count), ("not_lava", notLava.Count), ("zoned", zoned.Count), ("built", built.Count)));
        if (cells.Count != Size * Size || notLava.Count > 0)
        {
            throw new AcceptanceException($"lava-cells: {notLava.Count} of {cells.Count} field cells are not {Lava}, first {notLava.FirstOrDefault()}");
        }

        if (zoned.Count > 0 || built.Count > 0)
        {
            throw new AcceptanceException($"lava-cells: {zoned.Count} zone cells and {built.Count} player buildings stand on lava, first {zoned.Concat(built).First()}");
        }

        var pawns = await s.CallAsync("colonists", NativeOps.ListPawns, new RimGovernor.Brain.GameOrders.Observations.ListPawnsRequest { Scope = s.Scope, Filter = new RimGovernor.Brain.GameFacts.Observations.PawnFilter { Colonist = RimGovernor.Brain.GameFacts.Observations.PawnMembership.Only } }, cancellationToken).ConfigureAwait(false);
        var rows = pawns.Pawns;
        bool OnLava(RimGovernor.Brain.Domain.Cell? at) => at is { X: { } x, Z: { } z } && x >= site.X && x < site.X + site.Width && z >= site.Z && z < site.Z + site.Height;
        var standing = rows.Where(p => OnLava((p.Pawn.Place as RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap)?.Cell)).Select(p => p.Pawn.Id).ToList();
        if (rows.Count == 0 || standing.Count > 0)
        {
            throw new AcceptanceException($"colonists: expected colonists none of whom stand on lava, got {rows.Count} with [{string.Join(' ', standing)}] on it");
        }
    }
}
