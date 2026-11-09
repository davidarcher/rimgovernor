using System.Globalization;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Telemetry;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>hazard/warmth-budget</c> (#4052, epic #3578): the warmth budget on a real cold map. One stripped colonist at
/// hypothermia 0 inside a roofed hut with a lit campfire, the lab's only room, so the Safe area covers it; outdoors
/// pinned at -70 C, dangerous but not deadly to the stripped colonist, so native judges every stack reached. A forbidden
/// steel stack a short walk south of the door and one in the far corner of the map beyond two strips of deep water, whose
/// serpentine round trip from severity 0 crosses WarmthSeverityCap at that cold; a few berry bushes beside the near
/// stack. Under the served work, supply, recovery and sheltering families (work enables the coupled census the colony's first plan reads, which otherwise holds the clock; supply is the admin supply-safety Need that allows the named stack) the brain opens the colonist's warmth window
/// naming the near stack (the warmth_window row), lets the colonist out of Safe for it, the stack is hauled into the
/// hut's stockpile and the colonist is back in the hut under serious hypothermia; no window ever names the far stack,
/// which stays where it lies.
/// </summary>
internal static class HazardWarmthBudget
{
    private const string Colonist = "colonist";
    private const string Near = "near";
    private const string Far = "far";
    private const string Bushes = "berries";
    private const string Hut = "hut";

    // Absolute lab cells (LabWorld.LabMapSize = 100): the hut in the south-west corner, its door south, the near stack a
    // dozen cells south of the door, the far stack in the north-east corner.
    private static readonly Fx.StageRect HutRect = new(2, 2, 5, 5);
    private static readonly Fx.StageCell Door = new(4, 2);
    private static readonly Fx.StageCell NearCell = new(4, 14);
    private static readonly Fx.StageCell FarCell = new(96, 96);

    // Two strips of deep water across the map, the first at WallZ open only at its east end, the second at WallZ2 open
    // only at its west end, so the far stack's route snakes to the corner (about 280 cells): at -70 C the straight route
    // alone would fit. Terrain, not a wall: a player wall is a colony region and a neutral one is salvage.
    private const int WallZ = 50;
    private const int WallGapX = 95;
    private const int WallZ2 = 75;
    private const int WallGap2X = 4;

    // Cold enough that the far round trip from severity 0 crosses the cap with the margin, while the near one costs a
    // fraction of it, yet within RimWorld's Danger.Some band for the stripped colonist (comfortable 16 C, deadly below
    // about -74 C) so native reach and vanilla's haul both allow the trip; the campfires keep the hut warm.
    private const float Outdoors = -70f;

    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        World = new Fx.StageWorld { OutdoorTemperature = Outdoors },
        Clear =
        [
            new Fx.StageClear { Rect = new(0, 0, 12, 18), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones, Fx.StageClearKind.Fog, Fx.StageClearKind.Snow] },
            new Fx.StageClear { Rect = new(94, 94, 5, 5), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones, Fx.StageClearKind.Fog, Fx.StageClearKind.Snow] },
        ],
        Cells =
        [
            new Fx.StageCells { Rect = new(0, WallZ, WallGapX, 1), Terrain = "WaterDeep" },
            new Fx.StageCells { Rect = new(WallGap2X + 1, WallZ2, 100 - WallGap2X - 1, 1), Terrain = "WaterDeep" },
        ],
        Rooms = [new Fx.StageRoom { Name = Hut, Rect = HutRect, Stuff = "WoodLog", Doors = [Door], Roof = true }],
        Buildings =
        [
            new Fx.StageBuilding { Name = "campfire", Def = "Campfire", Cells = [new(3, 5)], Fuel = 20f },
            new Fx.StageBuilding { Name = "campfire2", Def = "Campfire", Cells = [new(3, 3)], Fuel = 20f },
        ],
        Zones = [new Fx.StageZone { Name = "store", Kind = Fx.StageZoneKind.Stockpile, Cells = [new(5, 3), new(5, 4), new(5, 5)], Priority = Fx.StageStoragePriority.Important, Allow = ["Steel"] }],
        Things =
        [
            new Fx.StageThing { Name = Near, Def = "Steel", Count = 75, Cell = NearCell, Forbidden = true },
            new Fx.StageThing { Name = Far, Def = "Steel", Count = 75, Cell = FarCell, Forbidden = true },
        ],
        Plants = [new Fx.StagePlant { Name = Bushes, Def = "Plant_Berry", Cells = [new(8, 12), new(9, 12), new(10, 12)] }],
        Pawns =
        [
            new Fx.StagePawn
            {
                Name = Colonist,
                Colonist = 0,
                Cell = new(4, 4),
                Gear = new Fx.StageGear { Strip = true },
                AllWork = 3,
                Timetable = Fx.StageTimeAssignment.Work,
            },
        ],
    };

    internal static TypedCase Case() => new()
    {
        Name = "hazard/warmth-budget",
        Scope = "Issue #4052 (epic #3578): on the lab pinned to -70 C, one stripped colonist at hypothermia 0 in a roofed hut with a lit "
            + "campfire, a forbidden steel stack twelve cells south of the door and one in the far corner beyond two strips of deep water whose round trip crosses "
            + "WarmthSeverityCap: under the served work, supply, recovery and sheltering families a warmth_window row names the near "
            + "stack as a loot trip and never the far one, the near stack is hauled into the hut's stockpile, the colonist is read "
            + "back in the hut under serious hypothermia at every read inside it, and the far stack stays where it lies. The window "
            + "plan is a unit test; the native cold, the Safe area release and vanilla's haul are not.",
        Ticks = 30_000,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Stage = Spec,
        Crew = new Crew { Size = 1 },
        QuietWorld = true,
        Serve = new ServeSpec { Families = ["work", "supply", "recovery", "sheltering"], NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "warmth-budget" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var colonist = staged.Pawns[Colonist].Id;
        var near = staged.Things[Near].Ids.Single();
        var far = staged.Things[Far].Ids.Single();
        var hut = staged.Rooms[Hut].Interior.Select(c => (c.X, c.Z)).ToHashSet();
        var start = await BodyAsync(s, "body-start", colonist, cancellationToken).ConfigureAwait(false);
        s.Record("body_start", ServedOps.Args(("comfy_min_c", start.ComfortableMinC), ("speed", start.MoveSpeed), ("severity", start.Hypothermia), ("outdoors", staged.OutdoorTemperature)));
        if (start.Hypothermia > WarmthPlanRules.WarmthRefillSeverity)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the colonist starts at hypothermia {start.Hypothermia:F3}, over the refill severity {WarmthPlanRules.WarmthRefillSeverity}"));
        }

        // The staging must make the far stack unaffordable and the near one cheap by the brain's own pricing over the
        // read body (routes estimated as the longer axis from the door); otherwise the claim is not testable here.
        var plan = WarmthPlanRules.PlanTrips(new WarmthRequest
        {
            Now = new Tick(0),
            Temperature = staged.OutdoorTemperature,
            ComfortMin = start.ComfortableMinC,
            Severity = start.Hypothermia,
            Speed = start.MoveSpeed,
            Tuning = DashEstimateRules.PublishedHypothermiaTuning,
            Candidates =
            [
                new WarmthCandidate { Id = near, Kind = WarmthTripKind.Loot, Tier = WarmthTier.Other, Value = 75, PathCells = Route(NearCell) },
                new WarmthCandidate { Id = far, Kind = WarmthTripKind.Loot, Tier = WarmthTier.Other, Value = 75, PathCells = Route(FarCell) },
            ],
        });
        var estimated = plan.Trips.Select(t => t.Id).ToList();
        s.Record("estimate", ServedOps.Args(("near_route", Route(NearCell)), ("far_route", Route(FarCell)), ("fits", string.Join(",", estimated))));
        if (!estimated.Contains(near) || estimated.Contains(far))
        {
            throw new AcceptanceException($"the staging does not price the near stack in and the far stack out of the budget: planned {string.Join(",", estimated)}");
        }

        var seen = new SortedSet<string>(StringComparer.Ordinal);
        var read = new HashSet<ulong>();
        var nearNamed = false;
        var hauled = false;
        var inHut = false;
        var windows = 0;
        var severity = 0.0;
        var outside = 0.0;
        long tick = 0;
        _ = await s.ServeUntilAsync("warmth-budget", s.TicksLeft, async ct =>
        {
            foreach (var row in s.Telemetry(TelemetryKinds.WarmthWindow))
            {
                if (!read.Add(row.Sequence))
                {
                    continue;
                }

                windows++;
                foreach (var trip in (row.Fields().GetValueOrDefault("trips") as List<object?> ?? []).Select(v => v?.ToString() ?? ""))
                {
                    seen.Add(trip);
                    if (trip.EndsWith("/" + far, StringComparison.Ordinal))
                    {
                        throw new AcceptanceException($"a warmth window named the far stack {far}: {string.Join(" ", seen)}");
                    }

                    nearNamed |= trip == "loot/" + near;
                }
            }

            var pawn = await TypedPawnOps.PawnAsync(s, "pawn", colonist, ct).ConfigureAwait(false) ?? throw new AcceptanceException($"the colonist {colonist} has no pawn row");
            if (pawn.Dead)
            {
                throw new AcceptanceException($"the colonist died: {pawn}");
            }

            severity = pawn.Settings.Body.Hypothermia;
            inHut = pawn.Pawn.Place is RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap { Cell: { X: var x, Z: var z } } && hut.Contains((x, z));
            if (inHut && severity >= WarmthPlanRules.WarmthSeverityCap)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the colonist is in the hut at hypothermia {severity:F3}, serious ({WarmthPlanRules.WarmthSeverityCap}); trips {string.Join(" ", seen)}"));
            }

            if (!inHut)
            {
                outside = Math.Max(outside, severity);
            }

            var supplies = await s.CallAsync("steel", NativeOps.ListSupplies, new RimGovernor.Brain.GameOrders.Observations.ListSuppliesRequest
            {
                Scope = s.Scope,
                Filter = new RimGovernor.Brain.GameFacts.Observations.StockFilter { DefNames = ["Steel"], Category = RimGovernor.Brain.GameFacts.Observations.StockCategory.Haulable, Ownership = RimGovernor.Brain.GameFacts.Observations.StockOwnership.All, IncludeHeld = true },
            }, ct).ConfigureAwait(false);
            tick = supplies.Context.Tick;
            var farItem = supplies.Stocks.SelectMany(st => st.Items).FirstOrDefault(i => i.Id == far);
            if (farItem is null || farItem.Cell.X != FarCell.X || farItem.Cell.Z != FarCell.Z)
            {
                throw new AcceptanceException($"the far stack {far} moved or is gone: {farItem}; trips {string.Join(" ", seen)}");
            }

            var nearItem = supplies.Stocks.SelectMany(st => st.Items).FirstOrDefault(i => i.Id == near);
            hauled |= nearItem is null || hut.Contains((nearItem.Cell.X, nearItem.Cell.Z));
            var state = string.Create(CultureInfo.InvariantCulture, $"windows={windows} named={nearNamed} hauled={hauled} inHut={inHut} severity={severity:F3}");
            return (state, nearNamed && hauled && inHut);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("outcome", ServedOps.Args(("tick", tick), ("window_rows", windows), ("trips", string.Join(" ", seen)), ("severity_back", severity), ("severity_outside_max", outside), ("near_hauled", hauled)));
        if (outside >= WarmthPlanRules.WarmthSeverityCap)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the colonist reached hypothermia {outside:F3} outside, serious ({WarmthPlanRules.WarmthSeverityCap})"));
        }
    }

    // A route's cells from the door: the longer axis (diagonal steps count one node), by way of each strip's gap for a
    // cell beyond it.
    private static double Route(Fx.StageCell cell)
    {
        Fx.StageCell gap1 = new(WallGapX, WallZ), gap2 = new(WallGap2X, WallZ2);
        return cell.Z > WallZ2 ? Chebyshev(Door, gap1) + Chebyshev(gap1, gap2) + Chebyshev(gap2, cell)
            : cell.Z > WallZ ? Chebyshev(Door, gap1) + Chebyshev(gap1, cell)
            : Chebyshev(Door, cell);
    }

    private static double Chebyshev(Fx.StageCell a, Fx.StageCell b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Z - b.Z));

    private static async Task<RimGovernor.Brain.GameFacts.Observations.PawnBodyStats> BodyAsync(TypedSession s, string label, string pawn, CancellationToken cancellationToken)
    {
        var state = await TypedPawnOps.PawnAsync(s, label, pawn, cancellationToken).ConfigureAwait(false) ?? throw new AcceptanceException($"{label}: the colonist {pawn} has no pawn row");
        return state.Settings.Body;
    }
}
