namespace RimGovernor.Brain.Policy;

public enum GearSlot
{
    [WireName("")]
    Unset = 0,

    [WireName("skin-torso")]
    SkinTorso,

    [WireName("skin-legs")]
    SkinLegs,

    [WireName("middle-torso")]
    MiddleTorso,

    [WireName("outer")]
    Outer,

    [WireName("belt")]
    Belt,

    [WireName("headgear")]
    Headgear,

    [WireName("primary")]
    Primary,
}

public enum GearRole
{
    [WireName("")]
    Unset = 0,

    [WireName("worker")]
    Worker,

    [WireName("soldier")]
    Soldier,

    [WireName("hunter")]
    Hunter,

    [WireName("crafter/indoor")]
    Indoor,

    [WireName("child")]
    Child,

    [WireName("slave")]
    Slave,

    [WireName("prisoner")]
    Prisoner,

    [WireName("non-combatant")]
    NonCombatant,
}

/// <summary>
/// Reuses the work planner's priorities, skills and trait facts. Explicit social/developmental status and squad
/// membership take precedence. Prisoner and UnrevealedCreepjoiner (a creepjoiner whose downside is not yet
/// revealed) dress under the same constraint mode as a slave.
/// </summary>
public sealed record GearRoleInput
{
    public WorkPawn Work { get; init; } = new();
    public bool Child { get; init; }
    public bool Slave { get; init; }
    public bool IncapableOfViolence { get; init; }
    public bool DraftedSquad { get; init; }
    public bool Prisoner { get; init; }
    public bool UnrevealedCreepjoiner { get; init; }
}

/// <summary>Where a gear option comes from; the declaration order is the planner's tie-break rank (worn first).</summary>
public enum GearSource
{
    [WireName("")]
    Unset = 0,

    [WireName("worn")]
    Worn,

    [WireName("loose")]
    Loose,

    [WireName("stored")]
    Stored,

    [WireName("bill")]
    Bill,
}

/// <summary>
/// Stats are for this def x stuff at Normal quality, before condition. The provider resolves native material
/// factors, eligibility and production gates; the model never invents materials, recipes or modded definition stats.
/// </summary>
public sealed record GearOption
{
    public string ID { get; init; } = "";
    public Resource Definition { get; init; }
    public Resource Stuff { get; init; }

    /// <summary>Awful=0 through Legendary=6.</summary>
    public int Quality { get; init; }

    public GearSlot Slot { get; init; }
    public EquatableArray<string> Layers { get; init; }
    public EquatableArray<string> Groups { get; init; }
    public GearSource Source { get; init; }
    public double Condition { get; init; }
    public double Sharp { get; init; }
    public double Blunt { get; init; }
    public double Cold { get; init; }
    public double Heat { get; init; }
    public double MoveSpeed { get; init; }
    public double Cost { get; init; }
    public double Range { get; init; }

    /// <summary>The def x stuff MaxHitPoints stat (zero when the catalog hides it).</summary>
    public double MaxHitPoints { get; init; }

    /// <summary>The def's hit points lost per day worn (ApparelProperties.wearPerDay; zero is no wear).</summary>
    public double WearPerDay { get; init; }

    public bool Tainted { get; init; }
    public bool Locked { get; init; }
    public bool Shield { get; init; }
    public bool Ranged { get; init; }

    /// <summary>The def's slaveApparel flag: only a slave may wear it.</summary>
    public bool SlaveOnly { get; init; }

    /// <summary>A psychic foil helmet: utility gear the model plans only on evidence (a psychic-drone letter).</summary>
    public bool Psychic { get; init; }

    /// <summary>A smokepop belt: utility gear the model never plans.</summary>
    public bool Smokepop { get; init; }

    /// <summary>A primary option's def rows.</summary>
    public WeaponDef Weapon { get; init; } = new();

    /// <summary>The ResearchProjectDefs the option's recipe requires.</summary>
    public EquatableArray<string> Research { get; init; }

    /// <summary>Its materials for a bill source (the armor ladder's steel, plasteel and components).</summary>
    public EquatableArray<Amount> Ingredients { get; init; }
}

public sealed record GearLoadoutInput
{
    public GearClimate? Climate { get; init; }
    public GearRoleInput Role { get; init; } = new();
    public bool Female { get; init; }
    public bool Nudist { get; init; }
    public bool TaintFree { get; init; }

    /// <summary>The finished research census; an option is eligible only once every project its Research names is finished.</summary>
    public EquatableArray<string> Research { get; init; }

    /// <summary>Whether a psychic-drone letter has been seen.</summary>
    public bool PsychicDrone { get; init; }

    /// <summary>
    /// The colonist's personal wealth share: an upgrade gap whose market-value delta it does not cover is dropped
    /// from the plan. The zero value is ungated; necessities are never charged.
    /// </summary>
    public PersonalShare Share { get; init; } = new();

    public double Ambient { get; init; }
    public double ComfortableMin { get; init; }
    public double ComfortableMax { get; init; }
    public EquatableArray<GearOption> Worn { get; init; }

    /// <summary>Options have passed outfit/body/stage and resource-policy eligibility. Loose/stored IDs identify physical items; bill IDs identify products.</summary>
    public EquatableArray<GearOption> Options { get; init; }

    /// <summary>Throws <see cref="DomainException"/> where Go returned an error.</summary>
    public void Validate()
    {
        Climate.Validate();
        foreach (var n in new[] { Ambient, ComfortableMin, ComfortableMax })
        {
            DomainException.Require(double.IsFinite(n), "invalid gear temperature");
        }
        DomainException.Require(ComfortableMin <= ComfortableMax, "invalid gear comfort band");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in Worn.Concat(Options))
        {
            DomainException.Require(
                PolicyFunctions.FoodId(o.ID)
                    && ids.Add(o.ID)
                    && PolicyFunctions.ValidResource(o.Definition)
                    && (string.IsNullOrEmpty(o.Stuff.Value) || PolicyFunctions.ValidResource(o.Stuff))
                    && o.Quality >= 0
                    && o.Quality <= 6,
                "invalid gear option identity"
            );
            DomainException.Require(o.Slot is >= GearSlot.SkinTorso and <= GearSlot.Primary, "invalid gear slot");
            DomainException.Require(o.Source is >= GearSource.Worn and <= GearSource.Bill, "invalid gear source");
            foreach (var n in new[] { o.Condition, o.Sharp, o.Blunt, o.Cold, o.Heat, o.Cost, o.Range, o.MaxHitPoints, o.WearPerDay })
            {
                DomainException.Require(PolicyFunctions.FoodNumber(n), "invalid gear stat");
            }
            DomainException.Require(o.Condition <= 1 && double.IsFinite(o.MoveSpeed), "invalid gear condition or speed");
            foreach (var project in o.Research)
            {
                DomainException.Require(PolicyFunctions.ValidResource(new Resource(project)), "invalid gear research");
            }
            foreach (var a in o.Ingredients)
            {
                DomainException.Require(PolicyFunctions.ValidResource(a.Resource) && a.Count > 0, "invalid gear ingredient");
            }
            foreach (var list in new[] { o.Layers, o.Groups })
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var v in list)
                {
                    DomainException.Require(PolicyFunctions.FoodId(v) && seen.Add(v), "invalid gear coverage");
                }
            }
        }
        foreach (var project in Research)
        {
            DomainException.Require(PolicyFunctions.ValidResource(new Resource(project)), "invalid gear research");
        }
        foreach (var o in Options)
        {
            DomainException.Require(o.Source != GearSource.Worn && !o.Locked, "invalid unworn gear option");
        }
        for (var i = 0; i < Worn.Count; i++)
        {
            var o = Worn[i];
            DomainException.Require(o.Source == GearSource.Worn, "invalid worn gear source");
            for (var j = 0; j < i; j++)
            {
                DomainException.Require(!PolicyFunctions.GearConflicts(o, Worn[j]), "conflicting worn gear");
            }
        }
    }
}

public sealed record GearGap
{
    public GearSlot Slot { get; init; }
    public GearOption Wanted { get; init; } = new();
    public GearSource Source { get; init; }
    public double Gain { get; init; }
}

public sealed record GearLoadout
{
    public PawnId Pawn { get; init; }
    public GearRole Role { get; init; }
    public EquatableArray<GearOption> Target { get; init; }
    public EquatableArray<GearGap> Gaps { get; init; }
    public double Score { get; init; }

    /// <summary>Set when the exact search hit GearSearchBudget: Target is the best ensemble found within it, not a proven optimum.</summary>
    public bool SearchBudgetExhausted { get; init; }
}

public sealed record GearDemand(Resource Definition, Resource Stuff, int Count);

public static partial class PolicyFunctions
{
    /// <summary>The gear slots in search order.</summary>
    private static readonly GearSlot[] GearSlots =
    [
        GearSlot.SkinTorso,
        GearSlot.SkinLegs,
        GearSlot.MiddleTorso,
        GearSlot.Outer,
        GearSlot.Belt,
        GearSlot.Headgear,
        GearSlot.Primary,
    ];

    public static GearRole DeriveGearRole(GearRoleInput p)
    {
        if (p.Child)
        {
            return GearRole.Child;
        }
        if (p.Slave)
        {
            return GearRole.Slave;
        }
        if (p.Prisoner || p.UnrevealedCreepjoiner)
        {
            return GearRole.Prisoner;
        }
        var incapableOfViolence = p.IncapableOfViolence;
        foreach (var w in p.Work.Incapable.ValueOrDefault)
        {
            if (w == WorkType.Violent)
            {
                incapableOfViolence = true;
            }
        }
        if (incapableOfViolence)
        {
            return GearRole.NonCombatant;
        }
        if (p.DraftedSquad)
        {
            return GearRole.Soldier;
        }
        // The role with the most work types at the pawn's best priority wins. A tie goes to the role holding the
        // pawn's best skill among those work types (level, passion breaking a level tie), then worker, hunter,
        // indoor, so a generalist whose every work type shares one priority (a fresh tribal pawn) stays a worker
        // unless a skill marks it otherwise.
        var skills = new Dictionary<string, int>(StringComparer.Ordinal);
        if (p.Work.Skills.TryGetValue(out var known))
        {
            foreach (var s in known)
            {
                if (!s.Disabled)
                {
                    skills[s.Name] = s.Level * 3 + GearPassionRank(s.Passion);
                }
            }
        }
        var best = 5;
        var counts = new Dictionary<GearRole, int>();
        var skill = new Dictionary<GearRole, int>();
        foreach (var w in p.Work.Work.ValueOrDefault)
        {
            if (w.Disabled || w.Priority <= 0 || w.Priority > DomainConstants.MaxPriority)
            {
                continue;
            }
            var r = GearWorkRole(w.Work);
            if (w.Priority < best)
            {
                best = w.Priority;
                counts = [];
                skill = [];
            }
            if (w.Priority == best)
            {
                counts[r] = counts.GetValueOrDefault(r) + 1;
                skill[r] = Math.Max(skill.GetValueOrDefault(r), skills.GetValueOrDefault(w.Skill ?? ""));
            }
        }
        var role = GearRole.Worker;
        foreach (var r in new[] { GearRole.Hunter, GearRole.Indoor })
        {
            if (counts.GetValueOrDefault(r) > counts.GetValueOrDefault(role) || (counts.GetValueOrDefault(r) == counts.GetValueOrDefault(role) && skill.GetValueOrDefault(r) > skill.GetValueOrDefault(role)))
            {
                role = r;
            }
        }
        return role;
    }

    /// <summary>
    /// The gear role a work type dresses for, per the wiki's work descriptions: hunters need a ranged weapon;
    /// benches, beds and research happen indoors; everything else (building, mining, growing, hauling, handling,
    /// fishing) is outdoor labour.
    /// </summary>
    private static GearRole GearWorkRole(WorkType w) =>
        w switch
        {
            WorkType.Hunting => GearRole.Hunter,
            WorkType.Crafting or WorkType.Tailoring or WorkType.Smithing or WorkType.Research or WorkType.Art or WorkType.Cooking or WorkType.Doctor or WorkType.Warden => GearRole.Indoor,
            _ => GearRole.Worker,
        };

    internal static int GearPassionRank(string? passion) =>
        passion switch
        {
            "Minor" => 1,
            "Major" => 2,
            _ => 0,
        };

    /// <summary>Armor and insulation multipliers of a quality; zero outside Awful..Legendary.</summary>
    public static (double Armor, double Insulation) GearQualityMultipliers(int quality)
    {
        if (quality < 0 || quality > 6)
        {
            return (0, 0);
        }
        return (new[] { .6, .8, 1, 1.15, 1.3, 1.45, 1.8 }[quality], new[] { .8, .9, 1, 1.1, 1.2, 1.5, 1.8 }[quality]);
    }

    public static double GearGapThreshold(GearRole role) =>
        role switch
        {
            GearRole.Soldier => .05,
            GearRole.Hunter => .1,
            GearRole.Slave or GearRole.Prisoner => .5,
            _ => .2,
        };

    /// <summary>
    /// The constraint mode for pawns whose downside or standing the bot does not weigh: slaves, prisoners and
    /// unrevealed creepjoiners. The pick covers legs and torso, meets min(thermal need, item capacity), then takes
    /// the lowest cost; see GearConstrainedScore.
    /// </summary>
    private static bool GearConstrained(GearRole role) => role is GearRole.Slave or GearRole.Prisoner;

    // Constraint tiers, each dominating the one below it and any sum of costs: coverage, then mood (a tainted
    // garment the wearer minds), then degrees of unmet thermal need.
    private const double GearCoveragePenalty = 1e9;
    private const double GearMoodPenalty = 1e8;
    private const double GearThermalPenalty = 1e6;

    private static bool GearMelee(GearLoadoutInput p)
    {
        foreach (var t in p.Role.Work.Traits.ValueOrDefault)
        {
            if (t.Effects.MeleeOnly)
            {
                return true;
            }
        }
        int melee = 0,
            shooting = 0;
        foreach (var s in p.Role.Work.Skills.ValueOrDefault)
        {
            if (!s.Disabled)
            {
                if (s.Name == "Melee")
                {
                    melee = s.Level;
                }
                if (s.Name == "Shooting")
                {
                    shooting = s.Level;
                }
            }
        }
        return melee > shooting;
    }

    /// <summary>
    /// The move-speed penalty at or past which the soldier role refuses armor: plate (-0.8) and cataphract (-0.5)
    /// slow a squad more than their armor is worth; marine (-0.25) and flak vests (-0.12) pass.
    /// </summary>
    public const double GearArmorSpeedFloor = -.5;

    private static bool GearResearched(GearLoadoutInput p, string project) => p.Research.Contains(project);

    /// <summary>Reports a pawn whose highest enabled work priority is doctoring.</summary>
    private static bool GearMedic(GearLoadoutInput p)
    {
        var best = 5;
        var medic = false;
        foreach (var w in p.Role.Work.Work.ValueOrDefault)
        {
            if (w.Disabled || w.Priority <= 0 || w.Priority > DomainConstants.MaxPriority)
            {
                continue;
            }
            if (w.Priority < best)
            {
                best = w.Priority;
                medic = w.Work == WorkType.Doctor;
            }
            else if (w.Priority == best && w.Work != WorkType.Doctor)
            {
                medic = false;
            }
        }
        return medic;
    }

    private static bool GearEligible(GearLoadoutInput p, GearOption o)
    {
        var role = DeriveGearRole(p.Role);
        var def = o.Definition.Value ?? "";
        var kid = def.StartsWith("Kid", StringComparison.Ordinal) || def.StartsWith("Apparel_Kid", StringComparison.Ordinal);
        if ((role == GearRole.Child) != kid)
        {
            return false;
        }
        // A shield belt stops the wearer shooting: melee soldiers and the medic.
        if (o.Shield && !((role == GearRole.Soldier && GearMelee(p)) || (role != GearRole.Soldier && role != GearRole.Child && !GearConstrained(role) && GearMedic(p))))
        {
            return false;
        }
        if (GearConstrained(role))
        {
            // Apparel that hurts the wearer's mood: read from the def's flags and the item's corpse history, never a name list.
            if ((o.Tainted && !p.TaintFree) || (o.SlaveOnly && role != GearRole.Slave) || o.Slot == GearSlot.Primary)
            {
                return false;
            }
        }
        if (o.Smokepop || (o.Psychic && !p.PsychicDrone))
        {
            return false;
        }
        if (role == GearRole.Soldier && o.Slot == GearSlot.Headgear && o.Sharp > 0 && !GearResearched(p, "Smithing"))
        {
            return false;
        }
        // Plate (-0.8 c/s) and cataphract (-0.5) never; recon and marine only as once their research is finished
        // (their materials are bill demand).
        if (role == GearRole.Soldier && o.MoveSpeed <= GearArmorSpeedFloor)
        {
            return false;
        }
        foreach (var project in o.Research)
        {
            if (!GearResearched(p, project))
            {
                return false;
            }
        }
        if (o.Slot == GearSlot.Primary)
        {
            if (role is GearRole.NonCombatant or GearRole.Child)
            {
                return false;
            }
            if (role == GearRole.Hunter && (!o.Ranged || o.Range < 25))
            {
                return false;
            }
            if (GearMelee(p) && o.Ranged)
            {
                return false;
            }
        }
        if (role is GearRole.Worker or GearRole.Indoor or GearRole.NonCombatant && o.MoveSpeed < 0)
        {
            return false;
        }
        return true;
    }

    /// <summary>Conflicts require a shared layer AND a shared body group. The primary weapon and each logical slot are exclusive independently of apparel layers.</summary>
    public static bool GearConflicts(GearOption a, GearOption b)
    {
        if (a.Slot == b.Slot)
        {
            return true;
        }
        foreach (var l in a.Layers)
        {
            foreach (var m in b.Layers)
            {
                if (l == m)
                {
                    foreach (var g in a.Groups)
                    {
                        foreach (var h in b.Groups)
                        {
                            if (g == h)
                            {
                                return true;
                            }
                        }
                    }
                }
            }
        }
        return false;
    }

    private static double GearItemScore(GearLoadoutInput p, GearOption o)
    {
        var (a, i) = GearQualityMultipliers(o.Quality);
        var role = DeriveGearRole(p.Role);
        if (GearConstrained(role))
        {
            // Cost of acquiring the item; a worn garment is already paid for.
            var cost = 0.0;
            if (o.Source != GearSource.Worn)
            {
                cost = -o.Cost;
            }
            if (o.Condition <= GearTatteredCondition)
            {
                cost -= 20;
            }
            return cost;
        }
        var armor = (o.Sharp * 2 + o.Blunt) * a * o.Condition;
        var (low, high) = p.Climate.TemperatureRange(p.Ambient);
        var thermal = Math.Min(Math.Max(0, p.ComfortableMin - low), o.Cold * i) + Math.Min(Math.Max(0, high - p.ComfortableMax), o.Heat * i);
        var score = armor + thermal + o.MoveSpeed * 10 - o.Cost * .001;
        if (o.Slot == GearSlot.Primary)
        {
            // A primary is worth its quality-scaled damage, so the armory's crafted upgrade outscores the worn
            // weapon and a replace swaps it in.
            score += o.Weapon.DPS * WeaponQualityMultiplier(o.Quality) * o.Condition;
        }
        switch (role)
        {
            case GearRole.Soldier:
                score += armor * 9;
                break;
            case GearRole.Hunter:
                if (o.Slot == GearSlot.Primary)
                {
                    score += o.Range;
                }
                if (o.Slot == GearSlot.Outer)
                {
                    score += o.Cold * i * .2;
                }
                break;
            case GearRole.Indoor:
                score -= thermal * .8;
                break;
        }
        if (o.Condition <= GearTatteredCondition)
        {
            score -= 20;
        }
        return score;
    }

    /// <summary>Whether the items cover the legs and the torso.</summary>
    private static (bool Legs, bool Chest) GearCoverage(IEnumerable<GearOption> items)
    {
        bool legs = false,
            chest = false;
        foreach (var o in items)
        {
            foreach (var g in o.Groups)
            {
                legs = legs || g == "Legs";
                chest = chest || g == "Torso";
            }
        }
        return (legs, chest);
    }

    /// <summary>
    /// Ranks an ensemble for the constraint mode: minus its acquisition cost, minus a tiered penalty for uncovered
    /// legs or torso, worn tainted gear, and degrees of thermal need the items' insulation does not meet. The need
    /// is the comfort band against the seasonal temperature range, so an ensemble that reaches the most insulation
    /// the options allow, up to the need, outranks any cheaper one, and cost decides among those that tie.
    /// </summary>
    private static double GearConstrainedScore(GearLoadoutInput p, IReadOnlyList<GearOption> items)
    {
        var score = 0.0;
        var tainted = 0;
        var dressed = false;
        double cold = 0,
            heat = 0;
        foreach (var o in items)
        {
            score += GearItemScore(p, o);
            var (_, i) = GearQualityMultipliers(o.Quality);
            cold += o.Cold * i;
            heat += o.Heat * i;
            if (o.Tainted)
            {
                tainted++;
            }
            dressed = dressed || (o.Slot != GearSlot.Belt && o.Slot != GearSlot.Headgear);
        }
        if (p.Nudist)
        {
            if (dressed)
            {
                score -= 30;
            }
            return score;
        }
        var (legs, chest) = GearCoverage(items);
        if (!legs)
        {
            score -= GearCoveragePenalty;
        }
        if (!chest)
        {
            score -= GearCoveragePenalty;
        }
        if (!p.TaintFree)
        {
            score -= GearMoodPenalty * tainted;
        }
        var (low, high) = p.Climate.TemperatureRange(p.Ambient);
        score -= GearThermalPenalty * (Math.Max(0, Math.Max(0, p.ComfortableMin - low) - cold) + Math.Max(0, Math.Max(0, high - p.ComfortableMax) - heat));
        return score;
    }

    private static double GearEnsembleScore(GearLoadoutInput p, IReadOnlyList<GearOption> items)
    {
        if (GearConstrained(DeriveGearRole(p.Role)))
        {
            return GearConstrainedScore(p, items);
        }
        var score = 0.0;
        var tainted = 0;
        var dressed = false;
        var (legs, chest) = GearCoverage(items);
        foreach (var o in items)
        {
            score += GearItemScore(p, o);
            if (o.Tainted)
            {
                tainted++;
            }
            dressed = dressed || (o.Slot != GearSlot.Belt && o.Slot != GearSlot.Headgear && o.Slot != GearSlot.Primary);
        }
        if (!p.TaintFree && tainted > 0)
        {
            score -= 5 + 3 * (Math.Min(tainted, 4) - 1);
        }
        if (p.Nudist)
        {
            if (dressed)
            {
                score -= 30;
            }
        }
        else
        {
            // Coverage dominates optional upgrades but remains a scored need.
            if (!legs)
            {
                score -= 1000;
            }
            if (p.Female && !chest)
            {
                score -= 1000;
            }
        }
        return score;
    }

    /// <summary>
    /// The most search nodes one PlanGearLoadout visits. The pruned exact search is exponential in per-slot option
    /// counts, so a large wardrobe is searched until the budget is spent and the best ensemble found is returned
    /// with SearchBudgetExhausted set; no option is dropped. Counting nodes (not time) keeps the result deterministic.
    /// </summary>
    public const int GearSearchBudget = 200000;

    private static int GearSourceRank(GearSource s) =>
        s switch
        {
            GearSource.Worn => 0,
            GearSource.Loose => 1,
            GearSource.Stored => 2,
            GearSource.Bill => 3,
            _ => 4,
        };

    /// <summary>Searches compatible ensembles, retaining forced/locked items. Equal scores retain worn gear, then stable item identity. No input is mutated.</summary>
    public static GearLoadout PlanGearLoadout(GearLoadoutInput p)
    {
        p.Validate();
        foreach (var t in p.Role.Work.Traits.ValueOrDefault)
        {
            p = p with { Nudist = p.Nudist || t.Effects.Nudist, TaintFree = p.TaintFree || t.Effects.TaintFree };
        }
        var role = DeriveGearRole(p.Role);
        var options = p.Worn.Concat(p.Options).OrderBy(o => GearSourceRank(o.Source)).ThenBy(o => o.ID, StringComparer.Ordinal).ToList();
        var bySlot = new Dictionary<GearSlot, List<GearOption>>();
        var locked = new List<GearOption>();
        foreach (var o in options)
        {
            if (o.Locked)
            {
                locked.Add(o);
            }
            if (o.Source == GearSource.Worn || GearEligible(p, o))
            {
                if (!bySlot.TryGetValue(o.Slot, out var list))
                {
                    bySlot[o.Slot] = list = [];
                }
                list.Add(o);
            }
        }
        var best = new List<GearOption>(p.Worn);
        var bestScore = GearEnsembleScore(p, best);
        // All ensemble adjustments are penalties, so remaining positive item scores form an admissible upper
        // bound for pruning the exact search.
        var upper = new double[GearSlots.Length + 1];
        for (var i = GearSlots.Length - 1; i >= 0; i--)
        {
            var m = 0.0;
            foreach (var o in bySlot.GetValueOrDefault(GearSlots[i]) ?? [])
            {
                m = Math.Max(m, GearItemScore(p, o));
            }
            upper[i] = upper[i + 1] + m;
        }
        var nodes = 0;
        var exhausted = false;
        var chosen = new List<GearOption>();
        void Visit(int index, double raw)
        {
            if (exhausted)
            {
                return;
            }
            if (++nodes > GearSearchBudget)
            {
                exhausted = true;
                return;
            }
            if (raw + upper[index] < bestScore)
            {
                return;
            }
            if (index == GearSlots.Length)
            {
                // This slice proposes purchases/replacements, not strip orders. Do not credit recovery for
                // silently removing a worn garment.
                foreach (var w in p.Worn)
                {
                    var covered = false;
                    foreach (var c in chosen)
                    {
                        covered = covered || GearConflicts(w, c);
                    }
                    if (!covered)
                    {
                        return;
                    }
                }
                var score = GearEnsembleScore(p, chosen);
                if (score > bestScore || (score == bestScore && GearPurchases(chosen) < GearPurchases(best)))
                {
                    bestScore = score;
                    best = new List<GearOption>(chosen);
                }
                return;
            }
            var slot = GearSlots[index];
            var forced = false;
            foreach (var l in locked)
            {
                forced = forced || l.Slot == slot;
            }
            foreach (var o in bySlot.GetValueOrDefault(slot) ?? [])
            {
                var ok = true;
                foreach (var l in locked)
                {
                    if (l.ID != o.ID && GearConflicts(l, o))
                    {
                        ok = false;
                    }
                }
                foreach (var c in chosen)
                {
                    if (GearConflicts(c, o))
                    {
                        ok = false;
                    }
                }
                if (ok)
                {
                    chosen.Add(o);
                    Visit(index + 1, raw + GearItemScore(p, o));
                    chosen.RemoveAt(chosen.Count - 1);
                }
            }
            if (!forced)
            {
                Visit(index + 1, raw);
            }
        }
        Visit(0, 0);
        var gaps = new List<GearGap>();
        foreach (var o in best)
        {
            if (o.Source == GearSource.Worn)
            {
                continue;
            }
            // Compare the ensemble with this slot left as currently worn.
            var alternative = best.Where(other => other.ID != o.ID).ToList();
            foreach (var worn in p.Worn)
            {
                if (!GearConflicts(worn, o))
                {
                    continue;
                }
                var fits = true;
                foreach (var other in alternative)
                {
                    if (GearConflicts(worn, other))
                    {
                        fits = false;
                    }
                }
                if (fits)
                {
                    alternative.Add(worn);
                }
            }
            if (!GearGapAffordable(p, o, alternative, best))
            {
                continue;
            }
            gaps.Add(new GearGap { Slot = o.Slot, Wanted = o, Source = o.Source, Gain = bestScore - GearEnsembleScore(p, alternative) });
        }
        var sorted = gaps.OrderByDescending(g => g.Gain).ThenBy(g => Wire.Name(g.Slot), StringComparer.Ordinal).ToList();
        return new GearLoadout
        {
            Role = role,
            Target = EquatableArray.From(best),
            Score = bestScore,
            SearchBudgetExhausted = exhausted,
            Gaps = EquatableArray.From(sorted),
        };
    }

    /// <summary>
    /// Whether the colonist's remaining share covers the gap, decided once at gap-build time so every
    /// GearGapThreshold reader agrees and an unaffordable upgrade holds nothing open. Necessities are never
    /// charged: the slot is empty (no worn item conflicts), a replaced item is tattered, or the gap supplies the
    /// legs (or a woman's chest) coverage the alternative lacks. Anything else is an upgrade priced as the wanted
    /// item's market value less the worn items it replaces (quality and condition priced by ItemMarketValue); an
    /// unpriceable upgrade is refused under a gated share. Weapons (the primary slot) are exempt: the armory drives them.
    /// </summary>
    private static bool GearGapAffordable(GearLoadoutInput p, GearOption wanted, IReadOnlyList<GearOption> alternative, IReadOnlyList<GearOption> best)
    {
        if (wanted.Slot == GearSlot.Primary)
        {
            return true;
        }
        var replaced = new List<GearOption>();
        foreach (var w in p.Worn)
        {
            if (GearConflicts(w, wanted))
            {
                if (w.Condition <= GearTatteredCondition)
                {
                    return true;
                }
                replaced.Add(w);
            }
        }
        if (replaced.Count == 0)
        {
            return true;
        }
        if (!p.Nudist)
        {
            var (legs, chest) = GearCoverage(alternative);
            var (bestLegs, bestChest) = GearCoverage(best);
            if ((!legs && bestLegs) || (p.Female && !chest && bestChest))
            {
                return true;
            }
        }
        (double Value, bool Ok) Price(GearOption o)
        {
            var item = PersonalItemOf(o);
            if (o.Source == GearSource.Bill)
            {
                item = item with { Condition = 1 }; // a crafted garment is new
            }
            var v = ItemMarketValue(o.Cost, item.Quality, item.Condition);
            return (v.ValueOrDefault, v.IsKnown);
        }
        var (delta, ok) = Price(wanted);
        foreach (var w in replaced)
        {
            var (v, vok) = Price(w);
            delta -= v;
            ok = ok && vok;
        }
        if (!ok)
        {
            // Allows(+Inf) is false under any gated share and true when ungated.
            return p.Share.Allows(double.PositiveInfinity);
        }
        return p.Share.Allows(delta);
    }

    private static int GearPurchases(IEnumerable<GearOption> items) => items.Count(o => o.Source != GearSource.Worn);

    /// <summary>
    /// Projects model-selected gaps into the existing method planner. Native gain is only an admission gate: a
    /// loose/stored item must still occur in the fresh eligible candidate list, but is ordered by model gain.
    /// </summary>
    internal static GearObservation ModeledGearObservation(GearObservation v, IReadOnlyList<GearLoadout> loadouts)
    {
        var models = new Dictionary<PawnId, GearLoadout>();
        foreach (var l in loadouts)
        {
            models[l.Pawn] = l;
        }
        var pawns = new List<GearPawn>(v.Pawns.Count);
        foreach (var p0 in v.Pawns)
        {
            var p = p0;
            if (!models.TryGetValue(p.Pawn, out var l))
            {
                // A pawn the model skipped cannot wear apparel: nothing to repair.
                pawns.Add(
                    p with
                    {
                        Deficit = Known(false),
                        Candidates = Known<EquatableArray<GearCandidate>>([]),
                        Replacements = Known<EquatableArray<GearReplacement>>([]),
                    }
                );
                continue;
            }
            var native = p.Candidates.ValueOrDefault;
            var candidates = new List<GearCandidate>();
            var needs = new List<GearReplacement>();
            var deficit = false;
            foreach (var g in l.Gaps)
            {
                if (g.Gain <= GearGapThreshold(l.Role))
                {
                    continue;
                }
                deficit = true;
                if (g.Source == GearSource.Bill)
                {
                    needs.Add(new GearReplacement { Definition = g.Wanted.Definition, Stuff = g.Wanted.Stuff, Reason = "loadout" });
                    continue;
                }
                foreach (var c in native)
                {
                    if (c.Target == g.Wanted.ID && c.Definition == g.Wanted.Definition && c.Gain > 0)
                    {
                        candidates.Add(c with { Gain = g.Gain });
                    }
                }
            }
            pawns.Add(
                p with
                {
                    Deficit = Known(deficit),
                    Candidates = Known(EquatableArray.From(candidates)),
                    Replacements = Known(EquatableArray.From(needs)),
                }
            );
        }
        return v with { Pawns = pawns };
    }

    /// <summary>Counts purchases, not unique definitions per pawn; quality does not split a native def x stuff production bill.</summary>
    public static IReadOnlyList<GearDemand> GearProductionDemand(IReadOnlyList<GearLoadout> loadouts)
    {
        var counts = new Dictionary<(Resource Def, Resource Stuff), int>();
        foreach (var l in loadouts)
        {
            foreach (var g in l.Gaps)
            {
                if (g.Source == GearSource.Bill && g.Gain > GearGapThreshold(l.Role))
                {
                    var key = (g.Wanted.Definition, g.Wanted.Stuff);
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }
        }
        return counts
            .Select(kv => new GearDemand(kv.Key.Def, kv.Key.Stuff, kv.Value))
            .OrderBy(d => d.Definition.Value, StringComparer.Ordinal)
            .ThenBy(d => d.Stuff.Value, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Allocates physical supplies once in stable pawn order. Bill options remain reusable; their resulting quantities are aggregated separately.</summary>
    public static (IReadOnlyList<GearLoadout> Loadouts, Fact<EquatableArray<GearDemand>> Demand) PlanColonyGear(IReadOnlyList<GearPawn> pawns)
    {
        var seen = new HashSet<PawnId>();
        foreach (var p in pawns)
        {
            DomainException.Require(FoodId(p.Pawn.Value) && seen.Add(p.Pawn), "invalid gear pawn identity");
        }
        var rows = pawns.OrderBy(p => p.Pawn.Value, StringComparer.Ordinal).ToList();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var @out = new List<GearLoadout>();
        foreach (var p in rows)
        {
            if (!p.LoadoutModel.TryGetValue(out var input))
            {
                DomainException.Require(p.ModelRefusal.Length == 0, $"gear pawn {p.Pawn.Value}: loadout model refused: {p.ModelRefusal}");
                if (p.Blocked)
                {
                    continue;
                }
                throw new DomainException($"gear pawn {p.Pawn.Value}: no loadout model");
            }
            if (p.Climate is not null)
            {
                input = input with { Climate = p.Climate };
            }
            input = input with { Options = EquatableArray.From(input.Options.Where(o => o.Source == GearSource.Bill || !used.Contains(o.ID))) };
            var l = PlanGearLoadout(input) with { Pawn = p.Pawn };
            @out.Add(l);
            foreach (var g in l.Gaps)
            {
                if (g.Source != GearSource.Bill && g.Gain > GearGapThreshold(l.Role))
                {
                    used.Add(g.Wanted.ID);
                }
            }
        }
        return (@out, Known(EquatableArray.From(GearProductionDemand(@out))));
    }
}
