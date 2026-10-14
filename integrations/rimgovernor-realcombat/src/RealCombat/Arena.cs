using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimGovernor.RealCombat;

/// <summary>
/// One arena: a generated map swept clean, then fought once per seed. A seed fully determines a fight. The game's
/// Rand is counter based, so pushing the seed fixes every draw, provided nothing else moves between fights: thing ids
/// and the tick counter restart from fixed values, the fight's things are destroyed afterwards and the world pawns the
/// generator parked are removed.
/// </summary>
public sealed class Arena
{
    private const int FirstThingId = 100000;
    private const int StartTick = 60000;
    private const int SettleTicks = 120;
    private const int SettleSeed = 333;

    private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly RealFightRequest _req;
    private readonly Map _map;
    private readonly Faction _hostile;
    private readonly HashSet<Thing> _baselineThings;
    private readonly HashSet<Pawn> _baselineWorldPawns;
    private readonly FieldInfo[] _idFields = typeof(UniqueIDsManager).GetFields(Private).Where(f => f.FieldType == typeof(int)).ToArray();
    private readonly FieldInfo _tickField = typeof(TickManager).GetField("ticksGameInt", Private)!;

    public Arena(RealFightRequest req)
    {
        _req = req;
        _map = GameHost.BuildMap(req.Width, req.Height);
        _hostile = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => f.def.defName == req.HostileFaction)
            ?? throw new InvalidOperationException($"realcombat: the generated world has no faction {req.HostileFaction}");
        _hostile.SetRelationDirect(Faction.OfPlayer, FactionRelationKind.Hostile, false);
        Sweep();
        Furnish();
        _baselineThings = [.. _map.listerThings.AllThings];
        _baselineWorldPawns = [.. Find.WorldPawns.AllPawnsAliveOrDead];
    }

    // Everything the map generator scattered goes, the roofs come off (a collapse in the first ticks moves the stream),
    // and a few ticks let the remainder settle before the baseline is taken.
    private void Sweep()
    {
        using var scope = new RandScope(SettleSeed);
        for (var pass = 0; pass < 5; pass++)
        {
            foreach (var thing in _map.listerThings.AllThings.ToList())
            {
                if (!thing.Destroyed)
                {
                    thing.Destroy(DestroyMode.Vanish);
                }
            }
        }

        foreach (var cell in _map.AllCells.ToList())
        {
            if (_map.roofGrid.RoofAt(cell) != null)
            {
                _map.roofGrid.SetRoof(cell, null);
            }
        }

        for (var i = 0; i < SettleTicks; i++)
        {
            Find.TickManager.DoSingleTick();
        }

        foreach (var thing in _map.listerThings.AllThings.ToList())
        {
            if (!thing.Destroyed)
            {
                thing.Destroy(DestroyMode.Vanish);
            }
        }
    }

    // The request's terrain, weather and standing things become part of the baseline every fight returns to.
    private void Furnish()
    {
        if (_req.Terrain.Length > 0)
        {
            var terrain = DefDatabase<TerrainDef>.GetNamed(_req.Terrain);
            foreach (var cell in _map.AllCells.ToList())
            {
                _map.terrainGrid.SetTerrain(cell, terrain);
            }
        }

        if (_req.Weather.Length > 0)
        {
            var weather = DefDatabase<WeatherDef>.GetNamed(_req.Weather);
            _map.weatherManager.curWeather = weather;
            _map.weatherManager.lastWeather = weather;
            _map.weatherManager.curWeatherAge = 0;
        }

        foreach (var t in _req.Things)
        {
            var def = DefDatabase<ThingDef>.GetNamed(t.Def);
            var stuff = t.Stuff.Length > 0 ? DefDatabase<ThingDef>.GetNamed(t.Stuff) : null;
            GenSpawn.Spawn(ThingMaker.MakeThing(def, stuff), new IntVec3(t.X, 0, t.Z), _map);
        }
    }

    /// <summary>Plays the fight a seed fixes. Under Verify it plays it twice and reports a mismatch.</summary>
    public RealSeedOutcome Fight(int seed)
    {
        var first = FightOnce(seed);
        if (!_req.Verify)
        {
            return first;
        }

        var second = FightOnce(seed);
        return second with { Nondeterministic = !SameOutcome(first, second) };
    }

    private static bool SameOutcome(RealSeedOutcome a, RealSeedOutcome b) =>
        a.Winner == b.Winner && a.Ticks == b.Ticks && a.Pawns.SequenceEqual(b.Pawns);

    private RealSeedOutcome FightOnce(int seed)
    {
        foreach (var field in _idFields)
        {
            field.SetValue(Find.UniqueIDsManager, FirstThingId);
        }

        _tickField.SetValue(Find.TickManager, StartTick);
        HostPatches.Shots.Clear();
        HostPatches.Hits.Clear();
        HostPatches.FirstShot.Clear();
        using var scope = new RandScope(seed);
        var fighters = new List<(RealPawn Spec, Pawn Pawn)>();
        try
        {
            foreach (var spec in _req.Pawns)
            {
                var pawn = MakePawn(spec);
                GenSpawn.Spawn(pawn, new IntVec3(spec.X, 0, spec.Z), _map);
                if (pawn.drafter != null)
                {
                    pawn.drafter.Drafted = true; // the policy orders only drafted colonists
                }

                fighters.Add((spec, pawn));
            }

            var ticks = Play(fighters);
            return Outcome(seed, ticks, fighters);
        }
        finally
        {
            foreach (var thing in _map.listerThings.AllThings.ToList())
            {
                if (!_baselineThings.Contains(thing) && !thing.Destroyed)
                {
                    thing.Destroy(DestroyMode.Vanish);
                }
            }

            foreach (var parked in Find.WorldPawns.AllPawnsAliveOrDead.Where(p => !_baselineWorldPawns.Contains(p)).ToList())
            {
                Find.WorldPawns.RemovePawn(parked);
            }
        }
    }

    // Every pawn attacks the nearest opponent still standing and keeps at it until the side is done.
    /// <summary>Decides in place of the built-in nearest-target retargeting; null keeps the built-in.</summary>
    public IFightController? Controller { get; set; }

    private int Play(List<(RealPawn Spec, Pawn Pawn)> fighters)
    {
        var ticks = 0;
        var controller = Controller;
        controller?.Begin(_map);
        if (controller is null)
        {
            Retarget(fighters);
        }
        else
        {
            Raid(fighters);
        }

        while (ticks < _req.MaxTicks && Standing(fighters, "colonist") && Standing(fighters, "hostile"))
        {
            controller?.Decide(_map, ticks);
            Find.TickManager.DoSingleTick();
            ticks++;
            if (controller is null)
            {
                Retarget(fighters);
            }
        }

        return ticks;
    }

    // The hostiles behave as a raid does in the game: under the assault lord's AI, not a scripted target. A controller
    // plays the colonists only, so the other side must act on its own.
    private void Raid(List<(RealPawn Spec, Pawn Pawn)> fighters)
    {
        var raiders = fighters.Where(f => f.Spec.Side == "hostile").Select(f => f.Pawn).ToList();
        if (raiders.Count > 0)
        {
            var faction = raiders[0].Faction;
            LordMaker.MakeNewLord(faction, new LordJob_AssaultColony(faction, canKidnap: false, canTimeoutOrFlee: false, canSteal: false), _map, raiders);
        }
    }

    private static bool IsOut(Pawn p) => p.Dead || p.Downed || p.Destroyed || !p.Spawned;

    private static bool Standing(List<(RealPawn Spec, Pawn Pawn)> fighters, string side) =>
        fighters.Any(f => f.Spec.Side == side && !IsOut(f.Pawn));

    private static void Retarget(List<(RealPawn Spec, Pawn Pawn)> fighters)
    {
        foreach (var me in fighters)
        {
            if (IsOut(me.Pawn))
            {
                continue;
            }

            Pawn? best = null;
            var bestDistance = int.MaxValue;
            foreach (var other in fighters)
            {
                if (other.Spec.Side == me.Spec.Side || IsOut(other.Pawn))
                {
                    continue;
                }

                var d = me.Pawn.Position.DistanceToSquared(other.Pawn.Position);
                if (d < bestDistance)
                {
                    best = other.Pawn;
                    bestDistance = d;
                }
            }

            if (best is null)
            {
                continue;
            }

            var current = me.Pawn.CurJob;
            if (current?.def == JobDefOf.AttackStatic && current.targetA.Thing == best)
            {
                continue;
            }

            me.Pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.AttackStatic, best));
        }
    }

    private RealSeedOutcome Outcome(int seed, int ticks, List<(RealPawn Spec, Pawn Pawn)> fighters)
    {
        var colonists = Standing(fighters, "colonist");
        var hostiles = Standing(fighters, "hostile");
        return new RealSeedOutcome
        {
            Seed = seed,
            Ticks = ticks,
            Winner = colonists && !hostiles ? "colonist" : hostiles && !colonists ? "hostile" : "",
            Pawns =
            [
                .. fighters.Select(f => new RealPawnOutcome(
                    f.Spec.Id,
                    f.Pawn.Downed,
                    f.Pawn.Dead,
                    f.Pawn.health.summaryHealth.SummaryHealthPercent,
                    HostPatches.Shots.GetValueOrDefault(f.Pawn.thingIDNumber),
                    HostPatches.Hits.GetValueOrDefault(f.Pawn.thingIDNumber),
                    HostPatches.FirstShot.TryGetValue(f.Pawn.thingIDNumber, out var first) ? first - StartTick : -1)),
            ],
        };
    }

    // A healthy adult of the kind, stripped of whatever the generator rolled (gear, hediffs, traits) and given what
    // the request states. Unstated skills keep their rolled levels.
    private Pawn MakePawn(RealPawn spec)
    {
        var kind = DefDatabase<PawnKindDef>.GetNamed(spec.Kind);
        var faction = spec.Side == "colonist" ? Faction.OfPlayer : _hostile;
        var pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
            kind,
            faction,
            PawnGenerationContext.NonPlayer,
            -1,
            forceGenerateNewPawn: true,
            canGeneratePawnRelations: false,
            allowGay: false,
            allowFood: false,
            allowAddictions: false,
            fixedBiologicalAge: 30f,
            fixedChronologicalAge: 30f,
            forcedXenotype: XenotypeDefOf.Baseliner,
            forceNoIdeo: true,
            forbidAnyTitle: true,
            mustBeCapableOfViolence: true));
        pawn.apparel.DestroyAll();
        foreach (var hediff in pawn.health.hediffSet.hediffs.ToList())
        {
            pawn.health.RemoveHediff(hediff);
        }

        foreach (var trait in pawn.story.traits.allTraits.ToList())
        {
            pawn.story.traits.RemoveTrait(trait);
        }

        foreach (var (skill, level) in spec.Skills)
        {
            var record = pawn.skills.GetSkill(DefDatabase<SkillDef>.GetNamed(skill));
            record.Level = level;
            record.passion = Passion.None;
        }

        foreach (var t in spec.Traits)
        {
            pawn.story.traits.GainTrait(new Trait(DefDatabase<TraitDef>.GetNamed(t.Def), t.Degree), true);
        }

        foreach (var w in spec.Worn)
        {
            var stuff = w.Stuff.Length > 0 ? DefDatabase<ThingDef>.GetNamed(w.Stuff) : null;
            var apparel = (Apparel)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(w.Def), stuff);
            pawn.apparel.Wear(apparel, false);
        }

        pawn.equipment.DestroyAllEquipment();
        if (spec.Weapon.Length > 0)
        {
            var stuff = spec.WeaponStuff.Length > 0 ? DefDatabase<ThingDef>.GetNamed(spec.WeaponStuff) : null;
            pawn.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(spec.Weapon), stuff));
        }

        return pawn;
    }
}
