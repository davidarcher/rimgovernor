using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>layout/shell-layer</c> (#4047, epic #2795): a colony of three on a pinned -30 C map with a roofed wooden hut (3x3
/// interior, door south) already standing. The injected plan holds the hut as a barracks with its second wall layer
/// (LayoutPlan.OuterWalls: the eleven cells one out from its ring, the cell beyond the door and the corners excepted).
/// The hut counts as built before the layer (it stands roofed and closed at the start), and under the served brain a
/// wall of the colony's is placed on every layer cell and then built. Owing the layer is policy (ShellBuildTests); this
/// case owns the native placement and build of walls off the ring.
/// </summary>
internal static class LayoutShellLayer
{
    private const int Colonists = 3;
    private const float Outdoor = -30f;
    private const int HutId = 1;

    // An 11x7 site: the hut's 5x5 ring at (1..5, 1..5) with its door at (3, 1), the layer round it on the site's
    // first seven columns, the wood and meals east of it.
    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        World = new Fx.StageWorld { Hour = 8, OutdoorTemperature = Outdoor },
        Site = new Fx.StageSite
        {
            Width = 11,
            Height = 7,
            Near = Fx.StageAnchor.Colonist,
            OffsetX = 3,
            Radius = 30,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged, Fx.StageGround.Heavy, Fx.StageGround.Unroofed],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 11, 7), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones, Fx.StageClearKind.Roofs] }],
        Rooms = [new Fx.StageRoom { Name = "hut", Rect = new Fx.StageRect(1, 1, 5, 5), Stuff = "WoodLog", Doors = [new(3, 1)], Roof = true }],
        Things =
        [
            new Fx.StageThing { Name = "logs", Def = "WoodLog", Count = 75, Cell = new(8, 2) },
            new Fx.StageThing { Name = "logs-2", Def = "WoodLog", Count = 75, Cell = new(8, 3) },
            new Fx.StageThing { Name = "logs-3", Def = "WoodLog", Count = 75, Cell = new(9, 2) },
            new Fx.StageThing { Name = "meals", Def = "MealSimple", Count = 10, Cell = new(9, 5) },
        ],
    };

    internal static TypedCase Case() => new()
    {
        Name = "layout/shell-layer",
        Scope = "Issue #4047: on a pinned -30 C map, a roofed wooden hut stands as a barracks in the injected plan with its second "
            + "wall layer (OuterWalls); the hut is built before the layer, and the served brain places a wall of the colony's on "
            + "every layer cell and builds them.",
        Ticks = 20_000, // a hang guard: eleven wooden walls are a few game hours of work
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Colonists },
        Crew = new Crew { Size = Colonists },
        Keep = ["Food"],
        QuietWorld = true,
        Stage = Spec,
        Serve = new ServeSpec { NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "shell-layer" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var site = s.Staged!.Site ?? throw new AcceptanceException("the shell stage found no site");
        var interior = new Rectangle { X = site.X + 2, Z = site.Z + 2, Width = 3, Height = 3 };
        var hut = new PlannedRoom { Id = HutId, Role = PlannedRole.Barracks, Region = RegionRules.RectRegion(interior), Door = new(site.X + 3, site.Z + 1), DoorSide = new DoorSide.Doored(Rotation.South) };
        var plan = new LayoutPlan { NextRoomId = 2, Rooms = [hut], Roofed = hut.Region };
        var layer = plan.OutdoorWallCells(hut);
        if (layer.Count != 11)
        {
            throw new AcceptanceException($"the hut's layer has {layer.Count} cells, want 11");
        }
        plan = plan with
        {
            OuterWalls = RegionRules.RegionOf(layer),
            Pieces = new Dictionary<int, IReadOnlyList<InteriorPiece>>
            {
                [HutId] = [new InteriorPiece { Def = "Campfire", Rect = new Rectangle { X = site.X + 3, Z = site.Z + 3, Width = 1, Height = 1 } }],
            },
        };
        if (!plan.Valid())
        {
            throw new AcceptanceException("the injected plan does not validate");
        }
        // Built before the layer: the staged hut stands roofed and closed.
        if (await FirstRoomReads.RoofedRoomsAsync(s, "rooms-before", cancellationToken).ConfigureAwait(false) is { Count: 0 })
        {
            throw new AcceptanceException("the staged hut does not stand roofed and closed: the precondition hides the layer's build");
        }
        await TypedLayoutPlan.InjectAsync(s, plan, cancellationToken).ConfigureAwait(false);

        var cells = layer.ToHashSet();
        async Task<Dictionary<Cell, RimGovernor.Brain.GameFacts.Observations.BuildingStatus>> OnLayerAsync(string label, CancellationToken ct)
        {
            var rows = await s.BuildingsAsync(label, new RimGovernor.Brain.GameOrders.Observations.ListBuildingsRequest
            {
                Statuses = [RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Blueprint, RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Frame, RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Built],
            }, ct).ConfigureAwait(false);
            var output = new Dictionary<Cell, RimGovernor.Brain.GameFacts.Observations.BuildingStatus>();
            foreach (var b in rows)
            {
                if (b.Building.Place is RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap { Cell: var p } && cells.Contains(new Cell(p.X, p.Z)))
                {
                    output[new Cell(p.X, p.Z)] = b.Status;
                }
            }
            return output;
        }
        string Summary(Dictionary<Cell, RimGovernor.Brain.GameFacts.Observations.BuildingStatus> on) =>
            string.Create(CultureInfo.InvariantCulture, $"placed={on.Count}/11 built={on.Values.Count(v => v == RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Built)}");

        var on = new Dictionary<Cell, RimGovernor.Brain.GameFacts.Observations.BuildingStatus>();
        _ = await s.ServeUntilAsync("layer-placed", s.TicksLeft, async ct =>
        {
            on = await OnLayerAsync("layer", ct).ConfigureAwait(false);
            return (Summary(on), on.Count == cells.Count);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("layer-placed", Summary(on));
        if (on.Count != cells.Count)
        {
            throw new AcceptanceException($"the layer was never wholly placed: {Summary(on)}, missing {string.Join(' ', cells.Where(c => !on.ContainsKey(c)))}");
        }
        _ = await s.ServeUntilAsync("layer-built", s.TicksLeft, async ct =>
        {
            on = await OnLayerAsync("layer", ct).ConfigureAwait(false);
            return (Summary(on), on.Count == cells.Count && on.Values.All(v => v == RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Built));
        }, cancellationToken).ConfigureAwait(false);
        s.Record("layer-built", Summary(on));
        if (on.Count != cells.Count || on.Values.Any(v => v != RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Built))
        {
            throw new AcceptanceException($"the layer never wholly stood built: {Summary(on)}");
        }
    }
}
