using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimWorld;
using Verse;
using Building = RimGovernor.Brain.Domain.Building;
using Cell = RimGovernor.Brain.Domain.Cell;

namespace RimGovernor.RealCombat.Native;

/// <summary>
/// A walled compound with the killbox the colony's own planner lays out in it, built at once. The ring wall closes the
/// map except for the killbox's opening; the census of the map (what the live game reads off its cell mirror) goes to
/// <see cref="DefenseLayout.DefenseLayouts"/>, the planner that proposes the funnel, trap corridor and firing line in
/// play; every building it proposes is placed instantly, so a fight starts on the finished defense. The layout the
/// policy holds in the fight (<see cref="Layout"/>) is read off the planner's result the way the defense round reads it.
/// </summary>
public sealed class DefenseStage(Rotation toward, int defenders, double minRange)
{
    private static readonly Cell[] Directions = [new(0, 1), new(1, 0), new(0, -1), new(-1, 0)];

    /// <summary>The layout the policy holds, or none when the planner refused the site.</summary>
    public CombatLayout? Layout { get; private set; }

    /// <summary>False leaves the planner's spike traps unplaced, so the fight reaches the firing line.</summary>
    public bool Traps { get; init; } = true;

    /// <summary>The planner's refusal, or empty.</summary>
    public string Failure { get; private set; } = "";

    /// <summary>The planner's result.</summary>
    public DefenseLayout? Planned { get; private set; }

    /// <summary>The cell the bed stands on, or null before it is placed.</summary>
    public Cell? BedCell { get; private set; }

    /// <summary>The cell behind the killbox's back wall, where the defenders start.</summary>
    public Cell Home { get; private set; }

    /// <summary>The cell the raiders reach first, outside the ring.</summary>
    public Cell Entry { get; private set; }

    /// <summary>Builds the ring wall, plans the killbox and places what it proposes.</summary>
    public void Raise(Map map)
    {
        var bounds = new Bounds(map.Size.x, map.Size.z);
        var (killbox, region, home, ok) = LayoutPlan.LayoutKillbox(LayoutPlan.OpeningPlan(toward), bounds);
        if (!ok)
        {
            Failure = "no killbox opening in a map this size";
            return;
        }

        Home = home;
        Entry = killbox.Entry;
        var d = Directions[(int)killbox.Toward];
        var p = new Cell(-d.Z, d.X);
        var names = DefenseLayoutPlanner.DefenseDefinitionNames;

        // The ring: PerimeterThick rows from the entry inward, across the whole map but for the opening.
        var ring = new List<Cell>();
        var openLow = -killbox.Width / 2;
        for (var row = 0; row < LayoutPerimeterRules.PerimeterThick; row++)
        {
            for (var a = -Math.Max(bounds.Width, bounds.Height); a <= Math.Max(bounds.Width, bounds.Height); a++)
            {
                if (a >= openLow && a < openLow + killbox.Width)
                {
                    continue;
                }

                var c = new Cell(Entry.X + (d.X * row) + (p.X * a), Entry.Z + (d.Z * row) + (p.Z * a));
                if (c.X >= 0 && c.Z >= 0 && c.X < bounds.Width && c.Z < bounds.Height)
                {
                    ring.Add(c);
                }
            }
        }

        foreach (var c in ring)
        {
            Place(map, new Building(names.Wall, c, Rotation.North, names.WallStuff));
        }

        killbox = killbox with { Walled = EquatableArray.From(ring) };
        var cells = new List<DefenseCell>();
        for (var x = region.X; x < region.X + region.Width; x++)
        {
            for (var z = region.Z; z < region.Z + region.Height; z++)
            {
                cells.Add(Census(map, new IntVec3(x, 0, z), names));
            }
        }

        var request = new DefenseRequest
        {
            Bounds = bounds,
            Region = region,
            Home = Home,
            Cells = EquatableArray.From(cells),
            Definitions = names,
            MinRange = minRange,
            Defenders = defenders,
            Killbox = killbox,
        };
        try
        {
            Planned = DefenseLayout.DefenseLayouts(request);
        }
        catch (DefenseException ex)
        {
            Failure = ex.Message;
            return;
        }

        foreach (var tier in Planned.Tiers)
        {
            foreach (var b in tier.Buildings)
            {
                if (!Traps && b.Definition == names.Trap)
                {
                    continue;
                }

                Place(map, b);
            }
        }

        Bed(map);
        Layout = new CombatLayout
        {
            Firing = [.. Planned.Firing.Select(f => f.Cell)],
            Retreat = [.. Planned.Firing.Select(f => f.Retreat)],
            Toward = Planned.Toward,
            Choke = Planned.TrapLane.Count > 0 ? new CombatChoke.Corridor(Planned.TrapLane[^1]) : new CombatChoke.NoCorridor(),
        };
    }

    // The colony's bed, behind the back wall: a rescue carries a downed colonist to one, and the order refuses without.
    // The nearest free spot to the defenders' start, three or more cells off so it stays out of their way.
    private void Bed(Map map)
    {
        var def = DefDatabase<ThingDef>.GetNamed("Bed");
        var at = new IntVec3(Home.X, 0, Home.Z);
        foreach (var c in GenRadial.RadialCellsAround(at, 12f, false))
        {
            if (c.DistanceTo(at) < 3f || !c.InBounds(map))
            {
                continue;
            }

            var rect = GenAdj.OccupiedRect(c, Rot4.North, def.size);
            if (!rect.Cells.All(r => r.InBounds(map) && r.Standable(map) && r.GetEdifice(map) == null && r.GetFirstItem(map) == null && r.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)))
            {
                continue;
            }

            var bed = ThingMaker.MakeThing(def, DefDatabase<ThingDef>.GetNamed("WoodLog"));
            bed.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(bed, c, map, Rot4.North);
            BedCell = new Cell(c.x, c.z);
            return;
        }

        Failure = "no room for a bed behind the back wall";
    }

    // The census row the live game's cell mirror reads for a cell.
    private static DefenseCell Census(Map map, IntVec3 c, DefenseDefinitions names)
    {
        var edifice = c.GetEdifice(map);
        var terrain = c.GetTerrain(map);
        var wall = DefDatabase<ThingDef>.GetNamed(names.Wall);
        var trap = DefDatabase<ThingDef>.GetNamed(names.Trap);
        var cover = c.GetCover(map);
        return new DefenseCell
        {
            Cell = new Cell(c.x, c.z),
            Walkable = c.Walkable(map),
            Passable = !c.Impassable(map),
            PlayerOwned = edifice?.Faction == Faction.OfPlayer,
            NaturalRock = edifice?.def.building?.isNaturalRock == true,
            EdgeReachable = map.reachability.CanReachMapEdge(c, TraverseParms.For(TraverseMode.PassDoors)),
            Door = edifice is Building_Door,
            Roofed = c.Roofed(map),
            WallSupport = terrain.affordances.Contains(wall.terrainAffordanceNeeded ?? TerrainAffordanceDefOf.Light),
            TrapSupport = terrain.affordances.Contains(trap.terrainAffordanceNeeded ?? TerrainAffordanceDefOf.Light),
            CoverFill = cover == null ? 0d : cover.def.fillPercent,
            Edifice = edifice?.def.defName ?? "",
        };
    }

    // A building stands at once; a floor is laid as terrain.
    internal static void Place(Map map, Building b)
    {
        var at = new IntVec3(b.Cell.X, 0, b.Cell.Z);
        var thing = DefDatabase<ThingDef>.GetNamedSilentFail(b.Definition);
        if (thing is null)
        {
            if (DefDatabase<TerrainDef>.GetNamedSilentFail(b.Definition) is { } floor)
            {
                map.terrainGrid.SetTerrain(at, floor);
                return;
            }

            throw new InvalidOperationException($"realcombat: the game has no def {b.Definition}");
        }

        var stuff = b.Stuff.Length > 0 ? DefDatabase<ThingDef>.GetNamed(b.Stuff) : null;
        var made = ThingMaker.MakeThing(thing, stuff);
        if (made.def.CanHaveFaction)
        {
            made.SetFaction(Faction.OfPlayer);
        }

        GenSpawn.Spawn(made, at, map, new Rot4((int)b.Rotation));
    }
}
