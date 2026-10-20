using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>layout/first-room-ice</c> (#3659): a colony of three on a pinned -30 C map with no roofed room. The injected plan
/// holds a heated barracks (a campfire piece, nothing built; staged walls would be auto-roofed by the game at once) and a
/// bedroom, as an ice-sheet first plan orders them (FirstRoomPlanTests). Under the served brain the first coupled selection that
/// starts a planned room starts the barracks, and no colonist's hypothermia reaches HypothermiaSerious before the barracks
/// stands roofed and closed. Deriving the barracks on a cold map is policy; this case owns the native survival to closure.
/// </summary>
internal static class LayoutFirstRoomIce
{
    private const int Colonists = 3;
    private const float Outdoor = -30f;
    private const int BarracksId = 1;
    private const int BedroomId = 2;

    // A 16x7 site: the barracks (5x3 interior, ring 7x5) at its west end with its door south, the bedroom's 3x3 interior at
    // (11..13, 1..3), the wood and meals between them.
    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        World = new Fx.StageWorld { Hour = 8, OutdoorTemperature = Outdoor },
        Site = new Fx.StageSite
        {
            Width = 16,
            Height = 7,
            Near = Fx.StageAnchor.Colonist,
            OffsetX = 3,
            Radius = 30,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged, Fx.StageGround.Heavy, Fx.StageGround.Unroofed],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 16, 7), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones, Fx.StageClearKind.Roofs] }],
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
        Name = "layout/first-room-ice",
        Scope = "Issue #3659: on a pinned -30 C map, three colonists with an injected plan holding a heated barracks (nothing built) "
            + "and a bedroom: the first coupled selection starting a planned room starts the barracks, and no colonist's "
            + "hypothermia reaches HypothermiaSerious (0.35) before the barracks stands roofed and closed.",
        Ticks = 15_000, // a hang guard: the roof is a few game hours of work
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Colonists },
        Crew = new Crew { Size = Colonists },
        Keep = ["Food"],
        QuietWorld = true,
        Stage = Spec,
        Serve = new ServeSpec { NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "first-room-ice" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var site = s.Staged!.Site ?? throw new AcceptanceException("the ice stage found no site");
        var interior = new Rectangle { X = site.X + 1, Z = site.Z + 1, Width = 5, Height = 3 };
        await TypedLayoutPlan.InjectAsync(s, new LayoutPlan
        {
            NextRoomId = 3,
            Rooms =
            [
                new PlannedRoom { Id = BarracksId, Role = PlannedRole.Barracks, Region = RegionRules.RectRegion(interior), Door = new(site.X + 3, site.Z), DoorRot = Rotation.South },
                new PlannedRoom { Id = BedroomId, Role = PlannedRole.Bedroom, Region = RegionRules.RectRegion(new(site.X + 11, site.Z + 1, 3, 3)), Door = new(site.X + 12, site.Z), DoorRot = Rotation.South },
            ],
            Pieces = new Dictionary<int, IReadOnlyList<InteriorPiece>>
            {
                [BarracksId] = [new InteriorPiece { Def = "Campfire", Rect = new Rectangle { X = site.X + 3, Z = site.Z + 2, Width = 1, Height = 1 } }],
            },
        }, cancellationToken).ConfigureAwait(false);

        var before = CoupledSelections.Of(s, 0).Select(r => r.Sequence).DefaultIfEmpty(0UL).Max();
        CoupledSelectionRow? first = null;
        var worst = (Pawn: "", Severity: 0.0);
        var closed = false;
        _ = await s.ServeUntilAsync("barracks-closed", s.TicksLeft, async ct =>
        {
            first ??= CoupledSelections.Of(s, before).FirstOrDefault(r => r.SelectedIds.Any(id => id.StartsWith("planned/room", StringComparison.Ordinal)));
            foreach (var (pawn, severity) in await FirstRoomReads.HypothermiaAsync(s, "pawns", ct).ConfigureAwait(false))
            {
                if (severity > worst.Severity)
                {
                    worst = (pawn, severity);
                }
            }
            var rooms = await FirstRoomReads.RoofedRoomsAsync(s, "rooms", ct).ConfigureAwait(false);
            closed = rooms.Any(r => r.Center is { } c && RegionRules.RectRegion(interior).Contains(new Cell(c.X, c.Z)));
            var serious = worst.Severity >= DashEstimateRules.HypothermiaSerious;
            return (string.Create(CultureInfo.InvariantCulture, $"selected={first is not null} worst={worst.Pawn}:{worst.Severity:F3} closed={closed}"), closed || serious);
        }, cancellationToken).ConfigureAwait(false);

        s.Record("first-selection", first is null ? "none" : string.Join(' ', first.SelectedIds));
        if (worst.Severity >= DashEstimateRules.HypothermiaSerious)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{worst.Pawn} reached hypothermia {worst.Severity:F3} before the barracks closed"));
        }
        if (!closed)
        {
            throw new AcceptanceException("the barracks never stood roofed and closed");
        }
        var row = first ?? throw new AcceptanceException("no coupled selection started a planned room");
        var barracks = string.Create(CultureInfo.InvariantCulture, $"planned/room{BarracksId}");
        if (!row.SelectedIds.Any(id => id == barracks || id.StartsWith(barracks + "/", StringComparison.Ordinal)))
        {
            throw new AcceptanceException($"the first planned-room selection did not start the barracks: [{string.Join(' ', row.SelectedIds)}]");
        }
    }
}
