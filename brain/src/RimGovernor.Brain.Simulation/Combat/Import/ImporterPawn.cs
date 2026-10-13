using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Simulation.CombatMetrics;

namespace RimGovernor.Brain.Simulation.Combat.Import;

// One combatant: its race, the baseline-adult stat context, the stats read through it, armor, melee power.
internal sealed partial class Importer
{
    // Pawn stat names.
    private const string StatMoveSpeed = "MoveSpeed";
    private const string StatPainShock = "PainShockThreshold";
    private const string StatIncomingDamage = "IncomingDamageFactor";
    private const string StatShootAccuracy = "ShootingAccuracyPawn";
    private const string StatAimingDelay = "AimingDelayFactor";
    private const string StatRangedCooldown = "RangedCooldownFactor";
    private const string StatShootingTouch = "ShootingAccuracyFactor_Touch";
    private const string StatShootingShort = "ShootingAccuracyFactor_Short";
    private const string StatShootingMedium = "ShootingAccuracyFactor_Medium";
    private const string StatShootingLong = "ShootingAccuracyFactor_Long";
    private const string SkillMelee = "Melee";
    private const string SkillShooting = "Shooting";
    private const string LethalSeverityScale = "HealthTuning.BaseTotalDamageLethalThreshold";

    /// <summary>A pawn kind's race resolved to the adult the Spec describes.</summary>
    private sealed record Race
    {
        public string Name { get; init; } = "";

        public required Verse.ThingDef Def { get; init; }

        public string LifeStage { get; init; } = "";

        public long AgeTicks { get; init; }

        public double BodySize { get; init; }

        public double HealthSize { get; init; }
    }

    private Race RaceOf(string kind)
    {
        var kd = cat.DefRow<Verse.PawnKindDef>(kind) ?? throw Unsupported($"catalog has no pawn kind \"{kind}\"");
        var def = kd.race;
        var name = def?.defName ?? "";
        var props = def?.race;
        if (def is null || props is null || props.intelligence != Verse.Intelligence.Humanlike)
        {
            throw Unsupported($"pawn kind \"{kind}\" race \"{name}\" is not a humanlike race, which is all the sim's pawn baseline covers");
        }
        var stages = props.lifeStageAges;
        if (stages is null || stages.Count == 0)
        {
            throw Unsupported($"race \"{name}\" has no life stages");
        }
        var adult = stages[^1];
        var stage = adult.def ?? throw Unsupported($"race \"{name}\" has a life stage with no def");
        return new Race
        {
            Name = name,
            Def = def,
            LifeStage = stage.defName,
            AgeTicks = (long)((double)adult.minAge * (double)GameTime.TicksPerYear),
            BodySize = (double)props.baseBodySize * (double)stage.bodySizeFactor,
            HealthSize = (double)props.baseHealthScale * (double)stage.healthScaleFactor,
        };
    }

    // The stat context of a healthy adult with no hediffs and normal needs, carrying only the given traits: the
    // state a lab-staged pawn is in (A28). Skills are the Spec's; every pawn capacity is at full level. The context
    // is stated as values, never read from a game pawn.
    private StatContext BaselineAdult(Race r, string kind, IReadOnlyDictionary<string, int> skills, IReadOnlyList<SpecTrait> traits)
    {
        var capacities = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var name in CapacityNames())
        {
            capacities[name] = 1;
        }
        if (capacities.Count == 0)
        {
            throw Unsupported("catalog has no pawn capacity defs");
        }
        var statTraits = new List<StatTrait>();
        foreach (var t in traits)
        {
            statTraits.Add(new StatTrait(t.Def, t.Degree));
        }
        var pawn = new PawnStatState
        {
            AgeBiologicalTicks = Fact.Known(r.AgeTicks),
            Female = Fact.Known(false),
            LifeStage = Fact.Known(r.LifeStage),
            KindDef = Fact.Known(kind),
            SkillLevels = Fact.Known(skills),
            Capacities = Fact.Known<IReadOnlyDictionary<string, float>>(capacities),
            Traits = Fact.Known(EquatableArray.From(statTraits)),
            Hediffs = Fact.Known(EquatableArray.From(Array.Empty<StatHediff>())),
            Genes = Fact.Known(EquatableArray.From(Array.Empty<string>())),
            Precepts = Fact.Known(EquatableArray.From(Array.Empty<string>())),
            PainTotal = Fact.Known(0f),
            MoodLevel = Fact.Known(0f),
            MutantDef = Fact.Known(""),
            IsSlave = Fact.Known(false),
        };
        return new StatContext { Pawn = pawn, Spawned = Fact.Known(true), Roofed = Fact.Known(false) };
    }

    // The chance Pawn_HealthTracker.CheckForStateChange rolls when a non-player, non-prisoner humanlike becomes
    // downable from violence: the pawn kind's forceDeathOnDowned (always) or overrideDeathOnDownedChance, else the
    // HealthTuning population-intent curve (the wavering-prisoners one unless the difficulty is unwavering) times
    // the difficulty's enemyDeathOnDownedChanceFactor. The returned string is the trace source.
    private (double Chance, string Source) DeathOnDownedOf(Spec spec, string kind)
    {
        var kd = cat.DefRow<Verse.PawnKindDef>(kind) ?? throw Unsupported($"catalog has no pawn kind \"{kind}\"");
        if (kd.forceDeathOnDowned)
        {
            return (1, "PawnKindDef " + kind + " forceDeathOnDowned");
        }
        if (kd.overrideDeathOnDownedChance is { } overrideChance)
        {
            return (overrideChance, "PawnKindDef " + kind + " overrideDeathOnDownedChance");
        }
        if (!spec.DeathOnDowned.TryGetValue(out var input))
        {
            throw Unsupported("death-on-downed inputs (population intent, difficulty) unknown");
        }
        if (input.EnemyDeathOnDownedChanceFactor < 0)
        {
            throw Unsupported($"enemyDeathOnDownedChanceFactor {input.EnemyDeathOnDownedChanceFactor.ToString(CultureInfo.InvariantCulture)} is negative");
        }
        var curve = Verse.HealthTuning.DeathOnDownedChance_NonColonyHumanlikeFromPopulationIntentCurve_WaveringPrisoners;
        var name = "..._WaveringPrisoners";
        if (input.UnwaveringPrisoners)
        {
            curve = Verse.HealthTuning.DeathOnDownedChance_NonColonyHumanlikeFromPopulationIntentCurve;
            name = "";
        }
        var v = EvaluateCurve(curve, (float)input.PopulationIntent);
        var chance = Math.Min((double)v * input.EnemyDeathOnDownedChanceFactor, 1);
        return (chance, "HealthTuning.DeathOnDownedChance_NonColonyHumanlikeFromPopulationIntentCurve" + name + " at population intent * difficulty enemyDeathOnDownedChanceFactor");
    }

    // SimpleCurve.Evaluate in float32 (stateval.EvaluateCurve): clamps to the end values, interpolates in between.
    private static float EvaluateCurve(Verse.SimpleCurve? curve, float x)
    {
        var points = curve?.Points;
        if (points is null || points.Count == 0)
        {
            throw Unsupported("HealthTuning death-on-downed curve: evaluating a curve with no points");
        }
        var (x0, y0) = (points[0].Loc.x, points[0].Loc.y);
        if (x <= x0)
        {
            return y0;
        }
        var (xl, yl) = (points[^1].Loc.x, points[^1].Loc.y);
        if (x >= xl)
        {
            return yl;
        }
        var (ax, ay) = (x0, y0);
        var (bx, by) = (xl, yl);
        for (var i = 0; i < points.Count; i++)
        {
            var (px, py) = (points[i].Loc.x, points[i].Loc.y);
            if (x <= px)
            {
                (bx, by) = (px, py);
                if (i > 0)
                {
                    (ax, ay) = (points[i - 1].Loc.x, points[i - 1].Loc.y);
                }
                break;
            }
        }
        var t = (float)((float)(x - ax) / (float)(bx - ax));
        return Lerp32(ay, by, t);
    }

    // Mathf.Lerp: t clamped to 0..1, evaluated as a + (b - a) * t with the product exact in double (the game's
    // fused multiply-add).
    private static float Lerp32(float a, float b, float t)
    {
        if (t < 0)
        {
            t = 0;
        }
        else if (t > 1)
        {
            t = 1;
        }
        return (float)((double)a + ((double)(float)(b - a) * (double)t));
    }

    // Names a pawn's traits for a trace source, empty for none.
    private static string TraitSource(IReadOnlyList<SpecTrait> traits)
    {
        if (traits.Count == 0)
        {
            return "";
        }
        var s = " with traits";
        foreach (var t in traits)
        {
            s += $" {t.Def}:{t.Degree}";
        }
        return s;
    }

    // Derives one combatant and the view facts about it.
    private (PawnSpec Spec, PawnFacts Facts) Pawn(Spec spec, SpecPawn p, ResolvedWeapon w)
    {
        var id = p.Id.Value;
        if (p.Side != Sides.Colonist && p.Side != Sides.Hostile)
        {
            throw Unsupported($"pawn \"{id}\" side \"{p.Side}\"");
        }
        if (p.Skills is null)
        {
            throw Unsupported($"pawn \"{id}\" skills unknown");
        }
        if (!p.Worn.TryGetValue(out var worn))
        {
            throw Unsupported($"pawn \"{id}\" worn apparel unknown");
        }
        var r = RaceOf(p.Kind);
        var ctx = BaselineAdult(r, p.Kind, p.Skills, p.Traits);

        double Stat(string name)
        {
            try
            {
                return stats.Value(name, new StatSubject(r.Name, Context: ctx));
            }
            catch (StatException e)
            {
                throw Unsupported($"pawn \"{id}\" stat {name}: {e.Message}");
            }
        }

        var speed = Stat(StatMoveSpeed);
        Trace(id, "MoveSpeed", "stat " + StatMoveSpeed + " of race " + r.Name + TraitSource(p.Traits));

        var shock = Stat(StatPainShock);
        var incoming = Stat(StatIncomingDamage);
        if (p.Side == Sides.Colonist)
        {
            // A player-faction pawn's would-be instant kill is turned into a survivable injury below this chance;
            // the per-part model has no such protection (A39).
            if (!spec.AllowInstantKillChance.TryGetValue(out var chance))
            {
                throw Unsupported($"pawn \"{id}\": the difficulty's allowInstantKillChance is unknown");
            }
            if (chance < 1)
            {
                throw Unsupported($"pawn \"{id}\": allowInstantKillChance {chance.ToString(CultureInfo.InvariantCulture)} protects colonists from instant kills, which the sim does not model");
            }
        }
        var kd = cat.DefRow<Verse.PawnKindDef>(p.Kind) ?? throw Unsupported($"catalog has no pawn kind \"{p.Kind}\"");
        var body = BodyOf(r.Def.race, r.HealthSize, shock, incoming, kd.ignoresPainShock);
        var dmg = _dmg ?? throw Unsupported("no damage def settled before the pawns are derived");
        var deathOnDowned = 0.0;
        if (p.Side == Sides.Hostile)
        {
            var (chance, source) = DeathOnDownedOf(spec, p.Kind);
            deathOnDowned = chance;
            Trace(id, "DeathOnDownedChance", source);
        }
        else
        {
            Trace(id, "DeathOnDownedChance", "Pawn_HealthTracker.CheckForStateChange: a player-faction pawn is never rolled");
        }
        Trace(id, "BodySize", "race " + r.Name + " baseBodySize * LifeStageDef " + r.LifeStage + " bodySizeFactor");
        Trace(id, "CanBenefitFromCover", "ThingDef " + r.Name + " category");
        Trace(id, "Body", "BodyDef " + r.Def.race.body?.defName + " parts, coverage, depth and tags, BodyPartDef hitPoints, hitChanceFactors, solid and destroyableByDamage * healthScale of race " + r.Name
            + "; DamageDef " + dmg.Def + " harmAllLayersUntilOutside and overkillPctToDestroyPart; HediffDef " + dmg.Hediff + " and " + MissingPartHediff + " injuryProps.painPerSeverity; PawnCapacityDef workers; stat "
            + StatPainShock + " and " + StatIncomingDamage + "; " + LethalSeverityScale + " * healthScale");

        // Armor: the worn apparel's ratings outermost first, then the natural one (A26). No armor category on the
        // damage means nothing is rated.
        var layers = new List<double>();
        if (dmg.ArmorStat.Length != 0)
        {
            foreach (var wear in worn)
            {
                layers.Add(ThingStat(dmg.ArmorStat, wear.Def, wear.Stuff, $"pawn \"{id}\" apparel \"{wear.Def}\" stat {dmg.ArmorStat}"));
                Trace(id, "Armor." + wear.Def, "stat " + dmg.ArmorStat + " of ThingDef " + wear.Def);
            }
            layers.Add(Stat(dmg.ArmorStat));
            Trace(id, "Armor.natural", "stat " + dmg.ArmorStat + " of race " + r.Name);
        }
        var composite = 1.0;
        foreach (var rating in layers)
        {
            composite *= 1 - rating;
        }
        var armor = 1 - composite;
        Trace(id, "Facts.Armor", "armor layers combined as 1 - product(1 - rating)");

        var shooter = new ShooterStats();
        if (w.Ranged is not null)
        {
            shooter = new ShooterStats
            {
                ShootingAccuracyPawn = Read(StatShootAccuracy),
                AccuracyTouch = Read(StatShootingTouch),
                AccuracyShort = Read(StatShootingShort),
                AccuracyMedium = Read(StatShootingMedium),
                AccuracyLong = Read(StatShootingLong),
                AimingDelayFactor = Read(StatAimingDelay),
                RangedCooldownFactor = Read(StatRangedCooldown),
            };
        }

        Fact<double> Read(string name)
        {
            var v = Stat(name);
            Trace(id, "Shooter." + name, "stat " + name + " of race " + r.Name);
            return Fact.Known(v);
        }

        var combat = new CombatSpec
        {
            BodySize = Fact.Known(r.BodySize),
            CanBenefitFromCover = r.Def.category == Verse.ThingCategory.Pawn,
            Body = body,
            DeathOnDownedChance = deathOnDowned,
            Armor = Fact.Known<IReadOnlyList<double>>(layers),
            Weapon = w.Ranged,
            Shooter = shooter,
        };

        // Melee power: the catalog's melee DPS of the wielded weapon, else of the race's natural tools, at full
        // health (A29).
        var meleeSource = "natural tools of race " + r.Name;
        double meleeDps;
        if (w.Melee)
        {
            meleeSource = "ThingDef " + p.Weapon + " tools";
            meleeDps = w.MeleeDps;
        }
        else
        {
            try
            {
                meleeDps = cat.WeaponOf(r.Name).DPS;
            }
            catch (DomainException e)
            {
                throw Unsupported($"pawn \"{id}\" natural melee: {e.Message}");
            }
        }

        // policy.FrontLine for a pawn with no traits: the melee line is whoever wields no ranged weapon and is at
        // least as good at melee as at shooting.
        if (!p.Skills.TryGetValue(SkillMelee, out var melee) || !p.Skills.TryGetValue(SkillShooting, out var shooting))
        {
            throw Unsupported($"pawn \"{id}\" has no {SkillMelee} or {SkillShooting} skill level for the front line split");
        }
        Trace(id, "Facts.FrontLine", "no ranged weapon and skill " + SkillMelee + " >= " + SkillShooting);
        var facts = new PawnFacts
        {
            Kind = p.Kind,
            FrontLine = w.Ranged is null && melee >= shooting,
            Armor = Fact.Known(armor),
            MeleePower = Fact.Known(meleeDps),
            LordJobClass = p.LordJobClass,
            LordToilClass = p.LordToilClass,
        };
        Trace(id, "Facts.MeleePower", "bridge WeaponOf DPS of " + meleeSource);
        return (new PawnSpec { Id = p.Id, Pos = p.Cell, Side = p.Side, MoveSpeed = Fact.Known(speed), Combat = combat }, facts);
    }
}
