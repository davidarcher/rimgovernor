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
    private const int FirstStandingId = 1000;
    private const int StartTick = 60000;
    private const int SettleTicks = 120;
    private const int SettleSeed = 333;

    private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly RealFightRequest _req;
    private readonly Action<Map>? _stage;
    private readonly Map _map;
    private readonly Faction _hostile;
    private readonly WeatherDef _weather;

    // This fight's departures: the pawns whose departure has been applied (they no longer count as standing or as targets)
    // and the tick each was applied at, by pawn id.
    private readonly HashSet<Pawn> _departing = [];
    private readonly Dictionary<string, int> _applied = [];
    private readonly Dictionary<Pawn, IntVec3> _fleeing = [];
    private readonly HashSet<Thing> _baselineThings;
    private readonly HashSet<Pawn> _baselineWorldPawns;
    private readonly float[] _baselineSnow;
    private readonly FieldInfo[] _idFields = typeof(UniqueIDsManager).GetFields(Private).Where(f => f.FieldType == typeof(int)).ToArray();
    private readonly FieldInfo _tickField = typeof(TickManager).GetField("ticksGameInt", Private)!;

    /// <param name="req">The arena.</param>
    /// <param name="stage">Builds standing structures on the map after the request's things, before the baseline (a defense compound the fight is played in); they are part of every fight's start.</param>
    public Arena(RealFightRequest req, Action<Map>? stage = null)
    {
        RequestCheck.Validate(req);
        _req = req;
        _stage = stage;
        _map = GameHost.BuildMap(req.Width, req.Height);
        _hostile = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => f.def.defName == req.HostileFaction)
            ?? throw new InvalidOperationException($"realcombat: the generated world has no faction {req.HostileFaction}");
        _hostile.SetRelationDirect(Faction.OfPlayer, FactionRelationKind.Hostile, false);
        Sweep();
        Furnish();
        _weather = _map.weatherManager.curWeather;
        _readback = ReadbackCheck.Arena(req, _map, _hostile);
        _baselineThings = [.. _map.listerThings.AllThings];
        _baselineWorldPawns = [.. Find.WorldPawns.AllPawnsAliveOrDead];
        _baselineSnow = _map.AllCells.Select(c => _map.snowGrid.GetDepth(c)).ToArray();
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
        // Standing things take fixed ids: the game seeds some choices (which way a raider trashes a wall) from a thing's id.
        foreach (var field in _idFields)
        {
            field.SetValue(Find.UniqueIDsManager, FirstStandingId);
        }

        foreach (var cell in _map.AllCells.ToList())
        {
            var (terrain, roof) = RequestCheck.GroundAt(_req, cell.x, cell.z);
            if (terrain.Length > 0)
            {
                _map.terrainGrid.SetTerrain(cell, DefDatabase<TerrainDef>.GetNamed(terrain));
            }

            if (roof.Length > 0)
            {
                _map.roofGrid.SetRoof(cell, DefDatabase<RoofDef>.GetNamed(roof));
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

        if (_stage is not null)
        {
            using var scope = new RandScope(SettleSeed);
            _stage(_map);
        }
    }

    private RealReadback _readback;

    /// <summary>What the game holds for the request, read back before the first tick; the pawns are those of the first fight played.</summary>
    public RealReadback Readback => _readback;

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
        RestoreSnow();

        // The weather ages and rolls a new one on its own clock, so each fight starts it afresh.
        _map.weatherManager.curWeather = _weather;
        _map.weatherManager.lastWeather = _weather;
        _map.weatherManager.curWeatherAge = 0;
        HostPatches.Shots.Clear();
        HostPatches.Hits.Clear();
        HostPatches.FirstShot.Clear();
        HostPatches.LastShot.Clear();
        _departing.Clear();
        _applied.Clear();
        _fleeing.Clear();
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

            var read = fighters.Select((f, i) => ReadbackCheck.Pawn(f.Spec, f.Pawn, i)).ToList();
            if (_readback.Pawns.Count == 0)
            {
                _readback = _readback with { Pawns = read };
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

    // Snow depth drifts as a fight runs (it melts a little each tick), and a pawn walking through snow is slowed by it, so a
    // later fight in the arena would start from a different ground than the first. Every fight starts from the baseline's.
    private void RestoreSnow()
    {
        var i = 0;
        foreach (var cell in _map.AllCells)
        {
            _map.snowGrid.SetDepth(cell, _baselineSnow[i++]);
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

        while (ticks < _req.MaxTicks && Standing(fighters, "colonist") && Opposed(fighters))
        {
            Depart(fighters, ticks);
            if (!Standing(fighters, "colonist") || !Opposed(fighters))
            {
                break;
            }

            controller?.Decide(_map, ticks);
            Hunt(fighters);

            // Projectiles launch from the caster's draw position, which only the render pass advances: headless, a pawn
            // that has walked would still shoot from the cell it spawned in.
            foreach (var pawn in _map.mapPawns.AllPawnsSpawned)
            {
                pawn.Drawer.tweener.ResetTweenedPosToRoot();
            }

            Find.TickManager.DoSingleTick();
            ticks++;
            if (controller is null)
            {
                Retarget(fighters);
            }
        }

        return ticks;
    }

    // Applies the departures due at this tick, in request order, before the tick runs. A pawn already out (down, dead,
    // gone) cannot depart: its departure stays unapplied. A departed pawn is dropped from its lord and, for a colonist,
    // undrafted, so neither the assault lord nor the controller sends it back into the fight.
    private void Depart(List<(RealPawn Spec, Pawn Pawn)> fighters, int tick)
    {
        // A fleeing pawn exits the map once it stands on its edge cell. The game's own exit-on-arrival toil would form a
        // world caravan for a colonist, state that carries from one fight to the next; ExitMap without one does not.
        foreach (var (pawn, edge) in _fleeing.ToList())
        {
            if (pawn.Spawned && pawn.Position == edge)
            {
                _fleeing.Remove(pawn);
                pawn.ExitMap(false, Rot4.Invalid);
            }
        }

        foreach (var d in _req.Departures)
        {
            if (d.Tick != tick)
            {
                continue;
            }

            var pawn = fighters.First(f => f.Spec.Id == d.Id).Pawn;
            if (IsOut(pawn))
            {
                continue;
            }

            pawn.GetLord()?.Notify_PawnLost(pawn, PawnLostCondition.ExitedMap);
            if (pawn.drafter != null)
            {
                pawn.drafter.Drafted = false;
            }

            if (d.Kind == RealDeparture.Leave)
            {
                pawn.jobs.StopAll();
                pawn.ExitMap(false, Rot4.Invalid);
            }
            else
            {
                var edge = EdgeCell(pawn);
                if (edge is null)
                {
                    continue; // nowhere to run to: the flight cannot be applied
                }

                var job = JobMaker.MakeJob(JobDefOf.Goto, edge.Value);
                _fleeing[pawn] = edge.Value;
                job.locomotionUrgency = LocomotionUrgency.Sprint;
                pawn.jobs.StartJob(job, JobCondition.InterruptForced);
            }

            _departing.Add(pawn);
            _applied[d.Id] = tick;
        }
    }

    // The nearest reachable cell on the map edge, by straight-line distance; ties break in a fixed order.
    private IntVec3? EdgeCell(Pawn pawn)
    {
        var at = pawn.Position;
        var candidates = new[]
        {
            new IntVec3(at.x, 0, 0),
            new IntVec3(at.x, 0, _map.Size.z - 1),
            new IntVec3(0, 0, at.z),
            new IntVec3(_map.Size.x - 1, 0, at.z),
        };
        foreach (var cell in candidates.OrderBy(c => c.DistanceToSquared(at)))
        {
            if (cell.Standable(_map) && pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly))
            {
                return cell;
            }
        }

        return null;
    }

    // The hostiles behave as a raid does in the game: under the assault lord's AI, not a scripted target. A controller
    // plays the colonists only, so the other side must act on its own.
    private void Raid(List<(RealPawn Spec, Pawn Pawn)> fighters)
    {
        var raiders = fighters.Where(f => f.Spec.Side == "hostile").Select(f => f.Pawn).ToList();
        if (raiders.Count > 0 && raiders[0].Faction == Faction.OfInsects && _map.listerThings.ThingsOfDef(ThingDefOf.Hive).FirstOrDefault() is Hive hive)
        {
            // Insects guard their hive as an infestation's do: not aggressive, they defend until provoked.
            var spawner = hive.PawnSpawner;
            var lord = spawner.Lord ?? CompSpawnerPawn.CreateNewLord(hive, false, 20f, typeof(LordJob_DefendAndExpandHive));
            foreach (var insect in raiders)
            {
                lord.AddPawn(insect);
            }
        }
        else if (raiders.Count > 0)
        {
            var faction = raiders[0].Faction;
            LordMaker.MakeNewLord(faction, new LordJob_AssaultColony(faction, canKidnap: false, canTimeoutOrFlee: false, canSteal: false), _map, raiders);
        }
    }

    private bool IsOut(Pawn p) => p.Dead || p.Downed || p.Destroyed || !p.Spawned || _departing.Contains(p);

    // Wild prey is out only when downed or dead: prey that fled (even off the map) is alive and the fight runs on.
    private bool IsOut(RealPawn spec, Pawn p) => spec.Side == "wild" ? p.Dead || p.Downed : IsOut(p);

    private bool Standing(List<(RealPawn Spec, Pawn Pawn)> fighters, string side) =>
        fighters.Any(f => f.Spec.Side == side && !IsOut(f.Spec, f.Pawn));

    // The colonists' opposition: the hostiles and the wild prey they hunt.
    private bool Opposed(List<(RealPawn Spec, Pawn Pawn)> fighters) => Standing(fighters, "hostile") || Standing(fighters, "wild");

    // A hunter acts through the game's own Hunt job against the prey it names, re-ordered whenever the job has ended
    // (finished, failed on its 5000 tick cap) while the prey is still up. The prey itself is never ordered.
    private void Hunt(List<(RealPawn Spec, Pawn Pawn)> fighters)
    {
        foreach (var hunter in fighters.Where(f => f.Spec.Hunt.Length > 0 && !IsOut(f.Spec, f.Pawn)))
        {
            var prey = fighters.First(f => f.Spec.Id == hunter.Spec.Hunt).Pawn;
            if (prey.Dead || prey.Downed || !prey.Spawned)
            {
                continue;
            }

            var current = hunter.Pawn.CurJob;
            if (current?.def == JobDefOf.Hunt && current.targetA.Thing == prey)
            {
                continue;
            }

            var job = JobMaker.MakeJob(JobDefOf.Hunt, prey);
            job.ignoreDesignations = true; // the game's hunt job also requires a Hunt designation; the request is the order
            hunter.Pawn.jobs.TryTakeOrderedJob(job);
        }
    }

    private void Retarget(List<(RealPawn Spec, Pawn Pawn)> fighters)
    {
        foreach (var me in fighters)
        {
            // Wild prey acts under the animal AI and a hunter under its Hunt job; neither is retargeted.
            if (IsOut(me.Spec, me.Pawn) || me.Spec.Side == "wild" || me.Spec.Hunt.Length > 0)
            {
                continue;
            }

            Pawn? best = null;
            var bestDistance = int.MaxValue;
            foreach (var other in fighters)
            {
                if (other.Spec.Side == me.Spec.Side || IsOut(other.Spec, other.Pawn) || (other.Spec.Side == "wild" && me.Spec.Side == "hostile"))
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
        var hostiles = Opposed(fighters);
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
                    HostPatches.FirstShot.TryGetValue(f.Pawn.thingIDNumber, out var first) ? first - StartTick : -1,
                    HostPatches.LastShot.TryGetValue(f.Pawn.thingIDNumber, out var last) ? last - StartTick : -1,
                    _applied.GetValueOrDefault(f.Spec.Id, -1),
                    _applied.ContainsKey(f.Spec.Id) && !f.Pawn.Dead && !f.Pawn.Spawned)),
            ],
        };
    }

    private static void ClearHealth(Pawn pawn)
    {
        foreach (var hediff in pawn.health.hediffSet.hediffs.ToList())
        {
            pawn.health.RemoveHediff(hediff);
        }
    }

    // Missing parts first, then the hediffs on their parts. Parts resolve by label against the race's body.
    private static void ApplyHealth(Pawn pawn, RealPawn spec)
    {
        var parts = pawn.RaceProps.body.AllParts;
        foreach (var label in spec.Missing)
        {
            var part = parts.Single(r => r.Label == label);
            var missing = (Hediff_MissingPart)HediffMaker.MakeHediff(HediffDefOf.MissingBodyPart, pawn, part);
            missing.IsFresh = false;
            pawn.health.AddHediff(missing, part);
        }

        foreach (var h in spec.Hediffs)
        {
            var part = h.Part.Length > 0 ? parts.Single(r => r.Label == h.Part) : null;
            var hediff = HediffMaker.MakeHediff(DefDatabase<HediffDef>.GetNamed(h.Def), pawn, part);
            hediff.Severity = (float)h.Severity;
            pawn.health.AddHediff(hediff, part);
        }
    }

    // A pawn of the kind at the requested age and xenotype, stripped of whatever the generator rolled (gear, hediffs,
    // traits) and given what the request states: the request's hediffs and missing parts are its whole health. Unstated
    // skills keep their rolled levels. The request was validated, so every def below exists.
    private Pawn MakePawn(RealPawn spec)
    {
        var kind = DefDatabase<PawnKindDef>.GetNamed(spec.Kind);
        var faction = spec.Side == "colonist" ? Faction.OfPlayer : spec.Side == "wild" ? null : _hostile;
        if (!kind.RaceProps.Humanlike)
        {
            // An animal or insect has no gear, traits or skills to strip.
            var animal = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, faction, PawnGenerationContext.NonPlayer, -1, forceGenerateNewPawn: true, canGeneratePawnRelations: false));
            ClearHealth(animal);
            ApplyHealth(animal, spec);
            return animal;
        }

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
            fixedBiologicalAge: (float)spec.Age,
            fixedChronologicalAge: (float)spec.Age,
            forcedXenotype: DefDatabase<XenotypeDef>.GetNamed(spec.Xenotype),
            forceNoIdeo: true,
            forbidAnyTitle: true,
            mustBeCapableOfViolence: true));
        pawn.apparel.DestroyAll();
        ClearHealth(pawn);
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
            Condition(apparel, w.Quality, w.HitPoints);
            pawn.apparel.Wear(apparel, false);
        }

        ApplyHealth(pawn, spec);

        pawn.inventory.innerContainer.ClearAndDestroyContents(); // a raider's generated medicine is not part of the request
        foreach (var c in spec.Carried)
        {
            pawn.inventory.innerContainer.TryAdd(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(c)));
        }

        if (spec.Psycasts.Count > 0)
        {
            pawn.ChangePsylinkLevel(1, false);
            foreach (var cast in spec.Psycasts)
            {
                pawn.abilities.GainAbility(DefDatabase<AbilityDef>.GetNamed(cast));
            }

            pawn.psychicEntropy.OffsetPsyfocusDirectly(1f);
        }

        if (spec.Title.Length > 0)
        {
            var empire = Find.FactionManager.FirstFactionOfDef(FactionDefOf.Empire)
                ?? throw new InvalidOperationException("realcombat: the world has no Empire for a royal title");
            pawn.royalty.SetTitle(empire, DefDatabase<RoyalTitleDef>.GetNamed(spec.Title), false, false, false);
            pawn.royalty.SetFavor(empire, spec.Favor, false);
            foreach (var permit in spec.Permits)
            {
                pawn.royalty.AddPermit(DefDatabase<RoyalTitlePermitDef>.GetNamed(permit), empire);
            }
        }

        pawn.equipment.DestroyAllEquipment();
        if (spec.Weapon.Length > 0)
        {
            var stuff = spec.WeaponStuff.Length > 0 ? DefDatabase<ThingDef>.GetNamed(spec.WeaponStuff) : null;
            var weapon = (ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(spec.Weapon), stuff);
            Condition(weapon, spec.WeaponQuality, spec.WeaponHitPoints);
            pawn.equipment.AddEquipment(weapon);
        }

        return pawn;
    }

    // The stated quality and hit points, as made; unstated quality keeps the rolled one and 0 hit points is full condition.
    private static void Condition(Thing thing, string quality, int hitPoints)
    {
        if (quality.Length > 0)
        {
            thing.TryGetComp<CompQuality>().SetQuality(Enum.Parse<QualityCategory>(quality), ArtGenerationContext.Outsider);
        }

        if (hitPoints > 0)
        {
            thing.HitPoints = hitPoints;
        }
    }
}
