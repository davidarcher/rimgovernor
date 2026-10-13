using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Simulation.CombatMetrics;
using RimGovernor.Brain.Bridge.Contracts;
using Defs = RimGovernor.Protocol.Defs;

namespace RimGovernor.Brain.Simulation.Combat.Import;

/// <summary>Builds a <see cref="Fight"/> from a <see cref="Spec"/>, the native definition catalog and a stat source.</summary>
public static class FightImporter
{
    /// <summary>
    /// Builds the Fight for <paramref name="spec"/> from the catalog. Throws <see cref="UnsupportedScenarioException"/>
    /// when a def, stat or fact the sim needs is missing or outside what it models. <see cref="Fight.Scripted"/> is
    /// left for the caller. Stat reads go through <paramref name="stats"/>: the importer's pawn contexts are its own
    /// hypotheticals, so production passes the game-thread stat service and tests a <see cref="FakeStatSource"/>
    /// holding the recorded values.
    /// </summary>
    public static ImportResult Import(DefinitionCatalog catalog, IStatSource stats, Spec spec)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(spec);
        return new Importer(catalog, stats).Run(spec);
    }
}

/// <summary>One Import's catalog readers and its growing result.</summary>
internal sealed partial class Importer(DefinitionCatalog cat, IStatSource stats)
{
    private const string StatMaxHitPoints = "MaxHitPoints";

    private readonly List<ImportTrace> _trace = [];
    private readonly List<string> _unmodeled = [];

    // The one ranged damage def every weapon in the Spec deals; the injury and armor derivations read it (A25, A26).
    private DamageInfo? _dmg;

    // The weather def's maxRangeCap, negative for none.
    private float _weatherCap;

    internal static UnsupportedScenarioException Unsupported(string reason) => new(reason);

    private void Trace(string subject, string field, string source) => _trace.Add(new ImportTrace(subject, field, source));

    private static string At(Cell c) => $"({c.X}, {c.Z})";

    internal ImportResult Run(Spec spec)
    {
        if (spec.Width <= 0 || spec.Height <= 0 || spec.StepTicks <= 0 || spec.MaxTicks <= 0)
        {
            throw Unsupported($"map {spec.Width}x{spec.Height}, StepTicks {spec.StepTicks} and MaxTicks {spec.MaxTicks} must be positive");
        }
        var sc = World(spec);
        var weapons = Weapons(spec);
        var pawns = new List<PawnSpec>();
        var facts = new Dictionary<PawnId, PawnFacts>();
        for (var i = 0; i < spec.Pawns.Count; i++)
        {
            var p = spec.Pawns[i];
            var (ps, pf) = Pawn(spec, p, weapons[i]);
            pawns.Add(ps);
            facts[p.Id] = pf;
        }
        var fight = new Fight
        {
            Name = spec.Name,
            Scenario = sc with { Pawns = pawns },
            Layout = spec.Layout,
            Facts = facts,
            MaxTicks = spec.MaxTicks,
            Raid = spec.Raid is null ? null : Raid(spec),
        };
        return new ImportResult { Fight = fight, Trace = _trace, Unmodeled = _unmodeled };
    }

    // Builds the raid lord's EnemyConfig: the give-up range from LordJob_AssaultColony.AssaultTimeBeforeGiveUp and
    // the colony health total from WealthWatcher.HealthTotal (A32).
    private EnemyConfig Raid(Spec spec)
    {
        var raid = spec.Raid!;
        var cfg = new EnemyConfig
        {
            CanTimeoutOrFlee = raid.CanTimeoutOrFlee,
            Exit = raid.Exit,
            AutoFleeFraction = raid.AutoFleeFraction,
            FirstTargets = raid.FirstTargets,
        };
        if (!cfg.CanTimeoutOrFlee)
        {
            return cfg;
        }
        var constants = Constants();
        var span = constants.LordJobAssaultColony?.AssaultTimeBeforeGiveUp
            ?? throw Unsupported("catalog has no LordJob_AssaultColony.AssaultTimeBeforeGiveUp");
        cfg = cfg with { GiveUpMinTicks = span.Min, GiveUpMaxTicks = span.Max };
        Trace("raid", "GiveUpTicks", "game constant LordJob_AssaultColony.AssaultTimeBeforeGiveUp");

        // WealthWatcher.HealthTotal: the hit points of every player-owned artificial building plus 100 per free
        // colonist at full health.
        var total = 0.0;
        foreach (var p in spec.Pawns)
        {
            if (p.Side == Sides.Colonist)
            {
                total += 100;
            }
        }
        foreach (var t in spec.Things)
        {
            var row = cat.ThingDef(t.Def);
            if (row?.Category != Defs.ThingCategory.Building || row.Building?.IsNaturalRock == true)
            {
                continue;
            }
            total += ThingStat(StatMaxHitPoints, t.Def, "", $"thing \"{t.Def}\" stat {StatMaxHitPoints}");
        }
        Trace("raid", "ColonyHealthTotal", "100 per colonist plus stat " + StatMaxHitPoints + " of each artificial building ThingDef");
        return cfg with { ColonyHealthTotal = Fact.Known(total) };
    }

    private Defs.GameConstants Constants()
    {
        try
        {
            return cat.GameConstants ?? throw new DomainException("catalog has no game constants");
        }
        catch (DomainException e)
        {
            throw Unsupported("game constants: " + e.Message);
        }
    }

    // Reads a stat of a ThingDef, turning a source failure into an unsupported scenario that names the context.
    private double ThingStat(string stat, string def, string stuff, string context)
    {
        try
        {
            return stats.Value(stat, StatSubject.ForThing(def, stuff));
        }
        catch (StatException e)
        {
            throw Unsupported($"{context}: {e.Message}");
        }
    }

    // Fills the map: terrain palette, thing palette and grid, weather and the friendly fire factor.
    private Scenario World(Spec spec)
    {
        var terrain = cat.TerrainDef(spec.Terrain) ?? throw Unsupported($"catalog has no terrain def \"{spec.Terrain}\"");
        var terrains = new List<TerrainDef>
        {
            new()
            {
                Name = spec.Terrain,
                PathCost = terrain.PathCost,
                Passable = terrain.Passability != Defs.Traversability.Impassable,
            },
        };
        Trace("map", "Terrain", "TerrainDef " + spec.Terrain + " pathCost, passability");

        var n = spec.Width * spec.Height;
        var things = new List<ThingDef>();
        var index = new Dictionary<string, byte>(StringComparer.Ordinal);
        byte[]? thingIds = null;
        foreach (var t in spec.Things)
        {
            var c = t.Cell;
            if (c.X < 0 || c.Z < 0 || c.X >= spec.Width || c.Z >= spec.Height)
            {
                throw Unsupported($"thing \"{t.Def}\" at {At(c)} is off the {spec.Width}x{spec.Height} map");
            }
            if (!index.TryGetValue(t.Def, out var id))
            {
                var def = ThingDefOf(t.Def);
                if (things.Count >= 255)
                {
                    throw Unsupported("more than 255 distinct thing defs");
                }
                things.Add(def);
                id = (byte)things.Count;
                index[t.Def] = id;
                Trace("map", "Things." + t.Def, "ThingDef " + t.Def + " fillPercent, passability, pathCost, thingClass");
            }
            thingIds ??= new byte[n];
            var at = (c.Z * spec.Width) + c.X;
            if (thingIds[at] != 0)
            {
                throw Unsupported($"two things on cell {At(c)} (\"{things[thingIds[at] - 1].Name}\" and \"{t.Def}\"): a cell holds one");
            }
            thingIds[at] = id;
        }

        var weather = cat.DefRow<Defs.WeatherDef>(spec.Weather) ?? throw Unsupported($"catalog has no weather def \"{spec.Weather}\"");
        _weatherCap = weather.MaxRangeCap;
        if (weather.MaxRangeCap >= 0)
        {
            // A range cap would shorten every weapon; the weapons derivation reads it.
            Trace("map", "WeatherRangeCap", "WeatherDef " + spec.Weather + " maxRangeCap");
        }
        Trace("map", "WeatherMoveSpeed", "WeatherDef " + spec.Weather + " moveSpeedMultiplier");
        Trace("map", "WeatherAccuracy", "WeatherDef " + spec.Weather + " accuracyMultiplier");
        if (!spec.FriendlyFireChanceFactor.IsKnown)
        {
            throw Unsupported("friendly fire chance factor unknown");
        }
        return new Scenario
        {
            Width = spec.Width,
            Height = spec.Height,
            StepTicks = spec.StepTicks,
            Terrain = terrains,
            Things = things,
            ThingIds = thingIds,
            WeatherMoveSpeed = Fact.Known((double)weather.MoveSpeedMultiplier),
            WeatherAccuracy = Fact.Known((double)weather.AccuracyMultiplier),
            FriendlyFireChanceFactor = spec.FriendlyFireChanceFactor,
        };
    }

    // ThingDef.Fillage: the fill category from fillPercent.
    internal static Fill Fillage(float percent)
    {
        if (percent < 0.01f)
        {
            return Fill.None;
        }
        return percent > 0.99f ? Fill.Full : Fill.Partial;
    }

    // Reads the values the sim wants of a thing def.
    private ThingDef ThingDefOf(string name)
    {
        var row = cat.ThingDef(name) ?? throw Unsupported($"catalog has no thing def \"{name}\"");
        if (!string.IsNullOrEmpty(row.Building?.TurretGunDef))
        {
            throw Unsupported($"thing \"{name}\" is a turret, which the sim does not fire");
        }
        var passability = row.Passability switch
        {
            Defs.Traversability.Standable => Passability.Standable,
            Defs.Traversability.PassThroughOnly => Passability.PassThroughOnly,
            Defs.Traversability.Impassable => Passability.Impassable,
            _ => throw Unsupported($"thing \"{name}\" has passability {row.Passability}"),
        };
        var door = false;
        if (!string.IsNullOrEmpty(row.ThingClass))
        {
            door = ClassIsA(row.ThingClass, "RimWorld.Building_Door", $"thing \"{name}\" class \"{row.ThingClass}\"");
        }
        return new ThingDef
        {
            Name = name,
            Fillage = Fillage(row.FillPercent),
            FillPercent = row.FillPercent,
            PathCost = row.PathCost,
            Passability = passability,
            Door = door,
        };
    }

    private bool ClassIsA(string cls, string ancestor, string context)
    {
        try
        {
            return cat.ClassIsA(cls, ancestor);
        }
        catch (DomainException e)
        {
            throw Unsupported($"{context}: {e.Message}");
        }
    }
}
