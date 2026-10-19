using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using Planner = RimGovernor.Brain.BuildingRuntime.RoundsDefenseLayoutPlanner;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The perimeter recut is one census diff every review (#3585): what the record's perimeter tiers own that the cut no
/// longer wants, and the census shows standing, becomes removal tiers ordered by cell and kind; nothing is stored to
/// stage them.
/// </summary>
public sealed class DefensePerimeterRecutTests
{
    private static readonly Bounds MapBounds = new(200, 200);

    private static MapSurvey Survey(Func<int, int, SurveyCell> cell)
    {
        var cells = new List<SurveyCell>(MapBounds.Width * MapBounds.Height);
        for (var z = 0; z < MapBounds.Height; z++)
        {
            for (var x = 0; x < MapBounds.Width; x++)
            {
                cells.Add(cell(x, z) with { Cell = new Cell(x, z) });
            }
        }
        return new MapSurvey { Bounds = MapBounds, Cells = cells };
    }

    /// <summary>Marsh bands across the map that a moisture pump dries (wet), or firm ground (dried).</summary>
    private static MapSurvey Marsh(bool dried) => Survey((x, z) => !dried && z % 40 < 4
        ? new SurveyCell { Walkable = true, Footing = Footing.Light, Bridgeable = true, Dries = true, Fertility = 1 }
        : new SurveyCell { Walkable = true, Fertility = 1 });

    /// <summary>A small hand-placed core (shelter, workshop, kitchen in a row at the middle) with the perimeter laid over it.</summary>
    private static LayoutPlan RingedPlan(MapSurvey s)
    {
        PlannedRoom Room(PlannedRole role, int x, int z, int w, int h) => new()
        {
            Role = role,
            Region = RegionRules.RectRegion(new Rectangle(x, z, w, h)),
            Door = new Cell(x + (w / 2), z + h + 1),
            DoorRot = Rotation.South,
        };
        int cx = (s.Bounds.Width / 2) - 12, cz = (s.Bounds.Height / 2) - 3;
        var plan = new LayoutPlan
        {
            Zones = LayoutZoningRules.Zone(s),
            Rooms = [Room(PlannedRole.Shelter, cx, cz, 6, 5), Room(PlannedRole.Workshop, cx + 9, cz, 6, 5), Room(PlannedRole.Kitchen, cx + 18, cz, 6, 5)],
        };
        plan = LayoutPlan.PlanPerimeter(plan.WithRoomIDs(), s).WithRoomIDs();
        Assert.True(plan.Ring().Ok, "no ring");
        return plan;
    }

    /// <summary>A record anchored on the plan's killbox with one trap corridor tier.</summary>
    private static DefenseLayoutRecord Record(LayoutPlan plan)
    {
        var (k, _, _, ok) = LayoutPlan.LayoutKillbox(plan, MapBounds);
        var (id, digest, found) = LayoutPlan.KillboxNode(plan);
        Assert.True(ok && found, "no killbox");
        return new DefenseLayoutRecord
        {
            World = new RimGovernor.Brain.Store.World(new("c"), new("l"), new(0)),
            Killbox = id,
            KillboxDigest = digest,
            Tiers = [new DefenseTierRecord { Name = DefenseLayout.TierTrapCorridor, Buildings = [new DefenseBuilding { Definition = "TrapSpike", Cell = k.Entry, Rotation = Rotation.North, Stuff = "WoodLog" }] }],
        };
    }

    private static DefenseCut Cut(LayoutPlan plan, string bridge, IReadOnlyList<Cell> transmitters, double spare, IReadOnlyList<PerimeterSection>? prison = null) =>
        new() { Plan = plan, Bridge = bridge, Transmitters = transmitters, Spare = spare, Prison = prison ?? [] };

    private static IReadOnlyList<Cell> Transmitters(LayoutPlan plan) => [new Cell(plan.Rooms[0].Region.Bounds().X, plan.Rooms[0].Region.Bounds().Z)];

    /// <summary>A census in which the buildings of the kept tiers stand.</summary>
    private static DefenseCensus Standing(DefenseLayoutRecord record, Func<DefenseTierRecord, bool> keep)
    {
        var c = new DefenseCensus();
        foreach (var t in record.Tiers.Where(keep))
        {
            foreach (var b in t.Buildings)
            {
                if (b.Definition == Planner.DefenseConduitDefinition)
                {
                    c.Conduits[b.Cell] = true;
                }
                else if (Planner.DefenseTerrain(b.Definition))
                {
                    c.Terrain[b.Cell] = b.Definition;
                }
                else
                {
                    c.Edifice[b.Cell] = b.Definition;
                }
            }
        }
        return c;
    }

    private static HashSet<string> Keys(DefenseLayoutRecord record, Func<DefenseTierRecord, bool> keep) =>
        [.. record.Tiers.Where(keep).SelectMany(t => t.Buildings).Select(Planner.DefenseBuildingKey)];

    private static bool Ahead(DefenseTierRecord t) => t.Remove && t.Name.Value[Planner.DefenseRemovalPrefix.Length + 1] == 'b';

    private static bool Lift(DefenseTierRecord t) => t.Remove && t.Name.Value[Planner.DefenseRemovalPrefix.Length] == 'u';

    /// <summary>
    /// The ring replaced (#2924) goes through the same diff: the old ring's walls on cells the replacement builds on come
    /// down ahead of it, the rest of the old ring and the dropped pumps behind it; nothing the new ring wants is taken
    /// down, nothing the old ring owned is stranded, and once the old ring is gone the record holds none of its nodes.
    /// </summary>
    [Fact]
    public void ARingReplacementRemovesTheOldRingAroundTheNewOne()
    {
        var plan = RingedPlan(Marsh(false));
        var transmitters = Transmitters(plan);
        var record = Record(plan);
        Assert.True(Planner.DefenseRecutPerimeter(record, Cut(plan, LayoutDefenseRules.PerimeterBridge, transmitters, 1e6), null));
        var oldRing = Standing(record, _ => true);
        Planner.DefenseTierCensusUpdate(record, oldRing);
        var oldKeys = Keys(record, t => LayoutDefenseRules.IsPerimeterTier(t.Name));

        var dried = LayoutPlan.LayPerimeter(plan, plan, Marsh(true), record.PerimeterHold());
        var (ring, ringOk) = dried.Ring();
        Assert.True(ringOk && ring.ReplacesSites.Count > 0, "no replacement ring");
        var cut = Cut(dried, LayoutDefenseRules.PerimeterBridge, transmitters, 1e6);
        Assert.True(Planner.DefenseRecutPerimeter(record, cut, oldRing));
        Assert.Equal(DefenseLayout.TierTrapCorridor, record.Tiers[0].Name);
        Assert.False(Planner.DefenseRecutPerimeter(record, cut, oldRing), "a second review of the same ground changed the record");

        var wanted = Keys(record, t => !t.Remove);
        var removed = Keys(record, t => t.Remove);
        Assert.Contains(removed, k => k.EndsWith("/" + LayoutDefenseRules.PerimeterLightStuff, StringComparison.Ordinal));
        Assert.Contains(removed, k => k.StartsWith(Planner.DefenseMoisturePump + "@", StringComparison.Ordinal));
        Assert.Empty(removed.Intersect(wanted));
        Assert.All(oldKeys.Where(k => !k.StartsWith(Planner.DefenseConduitDefinition + "@", StringComparison.Ordinal)), k => Assert.True(wanted.Contains(k) || removed.Contains(k), "stranded " + k));
        var freshCells = record.Tiers.Where(t => !t.Remove && LayoutDefenseRules.IsPerimeterTier(t.Name)).SelectMany(t => t.Buildings).Select(b => b.Cell).ToHashSet();
        var seenFresh = false;
        foreach (var t in record.Tiers.Skip(1))
        {
            seenFresh = seenFresh || !t.Remove;
            Assert.True(!t.Remove || Ahead(t) == !seenFresh, "removal on the wrong side of the new sections: " + t.Name.Value);
            Assert.All(t.Buildings, b => Assert.True(!t.Remove || freshCells.Contains(b.Cell) == Ahead(t), "misstaged " + b));
        }

        // The removals ahead are done: they leave, the old ring behind the new one still stands.
        var cleared = record.Tiers.Where(Ahead).SelectMany(t => t.Buildings).Select(b => b.Cell).ToHashSet();
        Assert.NotEmpty(cleared);
        var standingOld = new DefenseCensus
        {
            Edifice = oldRing.Edifice.Where(kv => !cleared.Contains(kv.Key)).ToDictionary(),
            Terrain = oldRing.Terrain.Where(kv => !cleared.Contains(kv.Key)).ToDictionary(),
            Conduits = oldRing.Conduits,
        };
        Planner.DefenseRecutPerimeter(record, cut, standingOld);
        Assert.DoesNotContain(record.Tiers, Ahead);
        Assert.Contains(record.Tiers, t => t.Remove);

        // The new ring closes and the old stretch comes down: no removal is left, and no tier names the old nodes.
        var closed = Standing(record, t => !t.Remove);
        Planner.DefenseRecutPerimeter(record, cut, closed);
        Planner.DefenseTierCensusUpdate(record, closed);
        Assert.DoesNotContain(record.Tiers, t => t.Remove);
        Assert.True(LayoutPlan.PerimeterRetirable(dried, record.PerimeterHold().Held), "the old ring is still held");
    }

    /// <summary>
    /// Heavy bridges researched over a built ring (the Go TestDefenseRecutPerimeterHeavyBridges): the wooden wall on
    /// each plain bridge is deconstructed, then the bridge lifted, then the heavy bridge and its stone wall laid.
    /// </summary>
    [Fact]
    public void HeavyBridgesReplaceTheWoodenWallOnABuiltRing()
    {
        var plan = RingedPlan(Survey((x, z) => z % 40 < 4
            ? new SurveyCell { Walkable = true, Footing = Footing.None, Bridgeable = true }
            : new SurveyCell { Walkable = true, Fertility = 1 }));
        var record = Record(plan);
        Planner.DefenseRecutPerimeter(record, Cut(plan, LayoutDefenseRules.PerimeterBridge, [], 0), null);
        var built = Keys(record, _ => true);
        var census = Standing(record, _ => true);
        Assert.True(Planner.DefenseRecutPerimeter(record, Cut(plan, LayoutDefenseRules.PerimeterHeavyBridge, [], 0), census), "heavy bridges recut nothing");

        var stage = 0; // 0 deconstruct, 1 lift, 2 build
        int walls = 0, lifted = 0;
        foreach (var t in record.Tiers.Skip(1))
        {
            Assert.True(!t.Remove || Ahead(t), "a removal behind the heavy bridges: " + t.Name.Value);
            var next = !t.Remove ? 2 : Lift(t) ? 1 : 0;
            Assert.True(next >= stage, "out of order: " + t.Name.Value);
            stage = next;
            foreach (var b in t.Buildings)
            {
                switch (stage)
                {
                    case 0:
                        Assert.True(b.Stuff == LayoutDefenseRules.PerimeterLightStuff && built.Contains(Planner.DefenseBuildingKey(b)), "deconstructs " + b);
                        walls++;
                        break;
                    case 1:
                        Assert.Equal(LayoutDefenseRules.PerimeterBridge, b.Definition);
                        lifted++;
                        break;
                    default:
                        Assert.True(b.Definition != LayoutDefenseRules.PerimeterBridge && (b.Stuff != LayoutDefenseRules.PerimeterLightStuff || b.Definition == Planner.DefenseDefinitionNames.Fence), "builds " + b);
                        break;
                }
            }
        }
        Assert.True(lifted > 0 && walls >= lifted, $"lifted {lifted}, walls {walls}");
    }

    /// <summary>
    /// A section the cut drops (prison turrets the spare power no longer covers) is removed behind the cut's sections,
    /// its conduit left; a standing wall no perimeter tier owns is never touched.
    /// </summary>
    [Fact]
    public void ADroppedSectionIsRemovedAndAnUnownedWallIsNot()
    {
        var plan = RingedPlan(Survey((_, _) => new SurveyCell { Walkable = true, Fertility = 1 }));
        var record = Record(plan);
        var inside = plan.Rooms[1].Region.Bounds();
        Cell turret = new(inside.X, inside.Z), conduit = new(inside.X + 1, inside.Z), unowned = new(inside.X + 2, inside.Z);
        var prison = new PerimeterSection
        {
            Name = new DefenseTierName(PrisonLayoutRules.TierPrisonTurretPrefix + "00"),
            Buildings = [new Building("Turret_MiniTurret", turret, Rotation.North, "Steel"), new Building(Planner.DefenseConduitDefinition, conduit, Rotation.North, "")],
        };
        Planner.DefenseRecutPerimeter(record, Cut(plan, LayoutDefenseRules.PerimeterBridge, [], 0, [prison]), null);
        var census = Standing(record, _ => true);
        census.Edifice[unowned] = "Wall";

        Assert.True(Planner.DefenseRecutPerimeter(record, Cut(plan, LayoutDefenseRules.PerimeterBridge, [], 0), census));
        var removal = Assert.Single(record.Tiers, t => t.Remove);
        Assert.Equal(Planner.DefenseRemovalPrefix + "xa-prison-00", removal.Name.Value);
        Assert.Equal(turret, Assert.Single(removal.Buildings).Cell);
        Assert.Same(removal, record.Tiers[^1]);
        Assert.DoesNotContain(record.Tiers, t => t.Name == prison.Name);

        // Deconstructed: the removal leaves and the next review stands nothing new.
        census.Edifice.Remove(turret);
        Planner.DefenseTierCensusUpdate(record, census);
        Assert.False(Planner.DefenseRecutPerimeter(record, Cut(plan, LayoutDefenseRules.PerimeterBridge, [], 0), census));
        Assert.DoesNotContain(record.Tiers, t => t.Remove);
    }

    /// <summary>
    /// A new pump is cut only while the spare power covers its 150 W; a placed pump keeps its slot, so a dip in spare
    /// power (the pumps draw it themselves) neither drops nor deconstructs it.
    /// </summary>
    [Fact]
    public void APlacedPumpKeepsItsSlotThroughASparePowerDip()
    {
        var plan = RingedPlan(Marsh(false));
        var transmitters = Transmitters(plan);
        int Pumps(DefenseLayoutRecord r) => Keys(r, t => !t.Remove).Count(k => k.StartsWith(Planner.DefenseMoisturePump + "@", StringComparison.Ordinal));

        var dry = Record(plan);
        Planner.DefenseRecutPerimeter(dry, Cut(plan, LayoutDefenseRules.PerimeterBridge, transmitters, 149), null);
        Assert.Equal(0, Pumps(dry));

        var record = Record(plan);
        Planner.DefenseRecutPerimeter(record, Cut(plan, LayoutDefenseRules.PerimeterBridge, transmitters, 1e6), null);
        var placed = Pumps(record);
        Assert.True(placed > 0, "no pump cut");
        var census = Standing(record, _ => true);
        Planner.DefenseTierCensusUpdate(record, census);

        Assert.False(Planner.DefenseRecutPerimeter(record, Cut(plan, LayoutDefenseRules.PerimeterBridge, transmitters, 0), census), "the dip changed the record");
        Assert.Equal(placed, Pumps(record));
        Assert.DoesNotContain(record.Tiers, t => t.Remove);
    }
}
