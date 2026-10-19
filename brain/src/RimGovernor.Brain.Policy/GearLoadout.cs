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

    /// <summary>The def x stuff ArmorRating_Heat (zero when the catalog hides it); <see cref="Heat"/> is the heat insulation.</summary>
    public double ArmorHeat { get; init; }

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

    /// <summary>
    /// The days until the option's stuff is in hand (<see cref="StuffArrival"/>): now for an item and a stuffless bill.
    /// A bill whose stuff arrives after the tier-1 horizon (<see cref="ForecastCoupled.TierOneHorizonDays"/>), or at no
    /// known day, is never eligible to plan or craft, however good its stats. The colony model still values it as a project's product.
    /// </summary>
    public Fact<double> Arrival { get; init; } = Known(0.0);
}

public sealed record GearLoadoutInput
{
    public GearClimate? Climate { get; init; }
    public GearRoleInput Role { get; init; } = new();
    public bool Female { get; init; }
    public bool Nudist { get; init; }

    /// <summary>The finished research census; an option is eligible only once every project its Research names is finished.</summary>
    public EquatableArray<string> Research { get; init; }

    /// <summary>Whether a psychic-drone letter has been seen.</summary>
    public bool PsychicDrone { get; init; }

    public double Ambient { get; init; }
    public double ComfortableMin { get; init; }
    public double ComfortableMax { get; init; }
    public EquatableArray<GearOption> Worn { get; init; }

    /// <summary>Options have passed outfit/body/stage and resource-policy eligibility. Loose/stored IDs identify physical items; bill IDs identify products.</summary>
    public EquatableArray<GearOption> Options { get; init; }

    /// <summary>The colony's expected raid, which values armor (<see cref="GearArmorTerm"/>); unknown plans against a nominal hit with a <c>gear_threat_unknown</c> finding.</summary>
    public Fact<RaidThreat> Threat { get; init; }

    /// <summary>Throws <see cref="DomainException"/>.</summary>
    public void Validate()
    {
        Climate.Validate();
        DomainException.Require(double.IsFinite(Ambient) && double.IsFinite(ComfortableMin) && double.IsFinite(ComfortableMax), "invalid gear temperature");
        DomainException.Require(ComfortableMin <= ComfortableMax, "invalid gear comfort band");
        // Every pawn's input is validated on each observation, so the checks below reuse their sets and read
        // the stats in place rather than allocating per option.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in Worn.Concat(Options))
        {
            DomainException.Require(
                FoodForecastRules.FoodId(o.ID)
                    && ids.Add(o.ID)
                    && AdmissionRules.ValidResource(o.Definition)
                    && (string.IsNullOrEmpty(o.Stuff.Value) || AdmissionRules.ValidResource(o.Stuff))
                    && o.Quality >= 0
                    && o.Quality <= 6,
                "invalid gear option identity"
            );
            DomainException.Require(o.Slot is >= GearSlot.SkinTorso and <= GearSlot.Primary, "invalid gear slot");
            DomainException.Require(o.Source is >= GearSource.Worn and <= GearSource.Bill, "invalid gear source");
            DomainException.Require(
                FoodForecastRules.FoodNumber(o.Condition) && FoodForecastRules.FoodNumber(o.Sharp) && FoodForecastRules.FoodNumber(o.Blunt) && FoodForecastRules.FoodNumber(o.ArmorHeat)
                    && FoodForecastRules.FoodNumber(o.Cold) && FoodForecastRules.FoodNumber(o.Heat) && FoodForecastRules.FoodNumber(o.Cost)
                    && FoodForecastRules.FoodNumber(o.Range) && FoodForecastRules.FoodNumber(o.MaxHitPoints) && FoodForecastRules.FoodNumber(o.WearPerDay),
                "invalid gear stat"
            );
            DomainException.Require(o.Condition <= 1 && double.IsFinite(o.MoveSpeed), "invalid gear condition or speed");
            foreach (var project in o.Research)
            {
                DomainException.Require(AdmissionRules.ValidResource(new Resource(project)), "invalid gear research");
            }
            foreach (var a in o.Ingredients)
            {
                DomainException.Require(AdmissionRules.ValidResource(a.Resource) && a.Count > 0, "invalid gear ingredient");
            }
            RequireCoverage(o.Layers, seen);
            RequireCoverage(o.Groups, seen);
        }
        foreach (var project in Research)
        {
            DomainException.Require(AdmissionRules.ValidResource(new Resource(project)), "invalid gear research");
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
                DomainException.Require(!GearLoadoutRules.GearConflicts(o, Worn[j]), "conflicting worn gear");
            }
        }
    }

    private static void RequireCoverage(EquatableArray<string> list, HashSet<string> seen)
    {
        seen.Clear();
        foreach (var v in list)
        {
            DomainException.Require(FoodForecastRules.FoodId(v) && seen.Add(v), "invalid gear coverage");
        }
    }
}

public sealed record GearGap
{
    public GearSlot Slot { get; init; }
    public GearOption Wanted { get; init; } = new();
    public GearSource Source { get; init; }
    public double Gain { get; init; }

    /// <summary>The item is needed for the pawn's insulation floor: the ensemble with this slot left as worn falls further short of it.</summary>
    public bool Floor { get; init; }
}

public sealed record GearLoadout
{
    public PawnId Pawn { get; init; }
    public GearRole Role { get; init; }
    public EquatableArray<GearOption> Target { get; init; }
    public EquatableArray<GearGap> Gaps { get; init; }

    /// <summary>Set when the exact search hit GearSearchBudget: Target is the best ensemble found within it, not a proven optimum.</summary>
    public bool SearchBudgetExhausted { get; init; }

    /// <summary>What the plan lacked: <c>gear_threat_unknown</c> when armor was valued against a nominal hit.</summary>
    public EquatableArray<string> Findings { get; init; }
}

public static class GearLoadoutRules
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
            if (w.Disabled || w.Priority <= 0 || w.Priority > WorkAssignmentConstants.MaxPriority)
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
        // Spans over constant data: the scorer reads these per option per visit, and arrays were allocated per call.
        ReadOnlySpan<double> armor = [.6, .8, 1, 1.15, 1.3, 1.45, 1.8];
        ReadOnlySpan<double> insulation = [.8, .9, 1, 1.1, 1.2, 1.5, 1.8];
        return (armor[quality], insulation[quality]);
    }

    public static double GearGapThreshold(GearRole role) =>
        role switch
        {
            GearRole.Soldier => .05,
            GearRole.Hunter => .1,
            GearRole.Slave or GearRole.Prisoner => .5,
            _ => .2,
        };

    /// <summary>Whether a gap is acted on: it is needed for the insulation floor, or it gains more than the role's threshold.</summary>
    internal static bool GearGapWanted(GearRole role, GearGap g) => g.Floor || g.Gain > GearGapThreshold(role);

    /// <summary>
    /// The constraint mode for pawns whose downside or standing the bot does not weigh: slaves, prisoners and
    /// unrevealed creepjoiners. The pick covers legs and torso, then takes the lowest cost; see GearConstrainedScore.
    /// </summary>
    private static bool GearConstrained(GearRole role) => role is GearRole.Slave or GearRole.Prisoner;

    // The constraint mode's coverage tier dominates every item score.
    private const double GearCoveragePenalty = 1e9;

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
            if (w.Disabled || w.Priority <= 0 || w.Priority > WorkAssignmentConstants.MaxPriority)
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

    internal static bool GearEligible(GearLoadoutInput p, GearOption o) => GearEligible(p, DeriveGearRole(p.Role), o);

    /// <summary><see cref="GearEligible(GearLoadoutInput, GearOption)"/> for a pawn whose <paramref name="role"/> is already derived.</summary>
    private static bool GearEligible(GearLoadoutInput p, GearRole role, GearOption o)
    {
        if (!o.Arrival.TryGetValue(out var arrival) || arrival > ForecastCoupled.TierOneHorizonDays)
        {
            return false;
        }
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
            if ((o.SlaveOnly && role != GearRole.Slave) || o.Slot == GearSlot.Primary)
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

    private const double SoldierArmorWeight = 10;

    /// <summary>The item score the gear planner ranks <paramref name="o"/> by for <paramref name="p"/>'s role, against its expected raid.</summary>
    internal static double GearItemScore(GearLoadoutInput p, GearOption o) => GearItemScore(DeriveGearRole(p.Role), o, new GearArmorTerm(p.Threat));

    private static double GearItemScore(GearRole role, GearOption o, GearArmorTerm armorTerm)
    {
        if (GearConstrained(role))
        {
            return o.Condition <= GearRules.GearTatteredCondition ? -20 : 0;
        }
        // Damage per hit avoided; a soldier weighs it SoldierArmorWeight times as much as anyone else.
        var armor = armorTerm.Of(o);
        var score = armor;
        if (o.Slot == GearSlot.Primary)
        {
            // A primary is worth its quality-scaled damage, so the armory's crafted upgrade outscores the worn
            // weapon and a replace swaps it in.
            score += o.Weapon.DPS * ArmoryRules.WeaponQualityMultiplier(o.Quality) * o.Condition;
        }
        switch (role)
        {
            case GearRole.Soldier:
                score += armor * (SoldierArmorWeight - 1);
                break;
            case GearRole.Hunter:
                if (o.Slot == GearSlot.Primary)
                {
                    score += o.Range;
                }
                break;
        }
        if (o.Condition <= GearRules.GearTatteredCondition)
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
    /// Ranks an ensemble for the constraint mode: minus a dominating penalty for uncovered legs or torso, and minus
    /// tattered items; ties keep worn gear, then stable item identity. Warmth is the insulation floor's.
    /// </summary>
    private static double GearConstrainedScore(GearLoadoutInput p, GearRole role, IReadOnlyList<GearOption> items, GearArmorTerm armor)
    {
        var score = 0.0;
        var dressed = false;
        foreach (var o in items)
        {
            score += GearItemScore(role, o, armor);
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
        return score;
    }

    /// <summary>The items' summed Cold and Heat insulation, each scaled by its quality multiplier.</summary>
    internal static (double Cold, double Heat) GearInsulation(IEnumerable<GearOption> items)
    {
        double cold = 0,
            heat = 0;
        foreach (var o in items)
        {
            var (c, h) = GearItemInsulation(o);
            cold += c;
            heat += h;
        }
        return (cold, heat);
    }

    private static (double Cold, double Heat) GearItemInsulation(GearOption o)
    {
        var (_, i) = GearQualityMultipliers(o.Quality);
        return (o.Cold * i, o.Heat * i);
    }

    /// <summary>
    /// How far insulation falls short of a floor, summed over cold and heat; shortfalls within rounding are 0, and the
    /// result is rounded so that equal ensembles compare equal whatever their item order.
    /// </summary>
    private static double GearShortfall((double Cold, double Heat) floor, double cold, double heat)
    {
        static double Short(double x) => x > 1e-6 ? x : 0;
        return Math.Round(Short(floor.Cold - cold) + Short(floor.Heat - heat), 6);
    }

    private static double GearEnsembleScore(GearLoadoutInput p, GearRole role, IReadOnlyList<GearOption> items, GearArmorTerm armor)
    {
        if (GearConstrained(role))
        {
            return GearConstrainedScore(p, role, items, armor);
        }
        var score = 0.0;
        var dressed = false;
        var (legs, chest) = GearCoverage(items);
        foreach (var o in items)
        {
            score += GearItemScore(role, o, armor);
            dressed = dressed || (o.Slot != GearSlot.Belt && o.Slot != GearSlot.Headgear && o.Slot != GearSlot.Primary);
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

    /// <summary>
    /// Searches compatible ensembles, retaining forced/locked items. Equal scores retain worn gear, then the cheaper option
    /// (so of two stuffs with equal stats the cheaper wins), then stable item identity. No input is mutated.
    /// <paramref name="disallowed"/> is the pawn's apparel filter (#3284, #3285): the pawn drops a matching worn item unless it
    /// is locked and never picks a matching option. <paramref name="floor"/> is the pawn's insulation floor (#3329): an
    /// ensemble whose summed insulation (GearInsulation) is below it in cold or heat is ineligible, and when none meets it the
    /// search returns the closest (the least summed shortfall). The planner prices neither mood nor
    /// warmth; the coupled comparator decides the filter and the floor.
    /// </summary>
    public static GearLoadout PlanGearLoadout(GearLoadoutInput p, Func<GearOption, bool>? disallowed = null, (double Cold, double Heat)? floor = null)
    {
        var limit = floor ?? (0, 0);
        p.Validate();
        foreach (var t in p.Role.Work.Traits.ValueOrDefault)
        {
            p = p with { Nudist = p.Nudist || t.Effects.Nudist };
        }
        if (disallowed is not null)
        {
            p = p with
            {
                Worn = EquatableArray.From(p.Worn.Where(o => o.Locked || !disallowed(o))),
                Options = EquatableArray.From(p.Options.Where(o => !disallowed(o))),
            };
        }
        var role = DeriveGearRole(p.Role);
        var armor = new GearArmorTerm(p.Threat);
        var options = p.Worn.Concat(p.Options).OrderBy(o => GearSourceRank(o.Source)).ThenBy(o => o.Cost).ThenBy(o => o.ID, StringComparer.Ordinal).ToList();
        var bySlot = new Dictionary<GearSlot, List<GearOption>>();
        var locked = new List<GearOption>();
        foreach (var o in options)
        {
            if (o.Locked)
            {
                locked.Add(o);
            }
            if (o.Source == GearSource.Worn || GearEligible(p, role, o))
            {
                if (!bySlot.TryGetValue(o.Slot, out var list))
                {
                    bySlot[o.Slot] = list = [];
                }
                list.Add(o);
            }
        }
        double Shortfall(IEnumerable<GearOption> items)
        {
            var (c, h) = GearInsulation(items);
            return GearShortfall(limit, c, h);
        }
        var best = new List<GearOption>(p.Worn);
        var bestScore = GearEnsembleScore(p, role, best, armor);
        var bestShort = Shortfall(best);
        // All ensemble adjustments are penalties, so remaining positive item scores form an admissible upper
        // bound for pruning the exact search; the most insulation each remaining slot offers bounds the shortfall.
        var upper = new double[GearSlots.Length + 1];
        var warm = new (double Cold, double Heat)[GearSlots.Length + 1];
        for (var i = GearSlots.Length - 1; i >= 0; i--)
        {
            var m = 0.0;
            var (c, h) = (0.0, 0.0);
            foreach (var o in bySlot.GetValueOrDefault(GearSlots[i]) ?? [])
            {
                m = Math.Max(m, GearItemScore(role, o, armor));
                var (oc, oh) = GearItemInsulation(o);
                (c, h) = (Math.Max(c, oc), Math.Max(h, oh));
            }
            upper[i] = upper[i + 1] + m;
            warm[i] = (warm[i + 1].Cold + c, warm[i + 1].Heat + h);
        }
        var nodes = 0;
        var exhausted = false;
        var chosen = new List<GearOption>();
        void Visit(int index, double raw, double cold, double heat)
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
            // Less shortfall wins first, then the score.
            var reach = GearShortfall(limit, cold + warm[index].Cold, heat + warm[index].Heat);
            if (reach > bestShort || (reach == bestShort && raw + upper[index] < bestScore))
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
                var score = GearEnsembleScore(p, role, chosen, armor);
                var shortfall = GearShortfall(limit, cold, heat);
                if (shortfall < bestShort || (shortfall == bestShort && (score > bestScore || (score == bestScore && GearPurchases(chosen) < GearPurchases(best)))))
                {
                    bestScore = score;
                    bestShort = shortfall;
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
                    var (oc, oh) = GearItemInsulation(o);
                    Visit(index + 1, raw + GearItemScore(role, o, armor), cold + oc, heat + oh);
                    chosen.RemoveAt(chosen.Count - 1);
                }
            }
            if (!forced)
            {
                Visit(index + 1, raw, cold, heat);
            }
        }
        Visit(0, 0, 0, 0);
        if (System.Diagnostics.Activity.Current is { OperationName: "gear.loadout" } span)
        {
            span.SetTag("rimgovernor.nodes", nodes);
            span.SetTag("rimgovernor.options", options.Count);
            span.SetTag("rimgovernor.eligible", bySlot.Values.Sum(l => l.Count));
            span.SetTag("rimgovernor.slots", string.Join(",", bySlot.OrderBy(kv => (int)kv.Key).Select(kv => kv.Key + "=" + kv.Value.Count)));
        }
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
            gaps.Add(new GearGap
            {
                Slot = o.Slot, Wanted = o, Source = o.Source, Gain = bestScore - GearEnsembleScore(p, role, alternative, armor), Floor = Shortfall(alternative) > bestShort,
            });
        }
        var sorted = gaps.OrderByDescending(g => g.Gain).ThenBy(g => Wire.Name(g.Slot), StringComparer.Ordinal).ToList();
        return new GearLoadout
        {
            Role = role,
            Target = EquatableArray.From(best),
            SearchBudgetExhausted = exhausted,
            Gaps = EquatableArray.From(sorted),
            Findings = armor.ThreatKnown || GearConstrained(role) ? default : EquatableArray.From([GearArmorTerm.ThreatUnknownFinding]),
        };
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
                if (!GearGapWanted(l.Role, g))
                {
                    continue;
                }
                deficit = true;
                if (g.Source == GearSource.Bill)
                {
                    needs.Add(new GearReplacement { Definition = g.Wanted.Definition, Reason = "loadout" });
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

    // Each pawn's search span, under the scope prefix the trace listener samples.
    private static readonly System.Diagnostics.ActivitySource s_spans = new("github.com/davidarcher/RimGovernor/policy");

    /// <summary>
    /// Allocates physical supplies once in stable pawn order. Bill options remain reusable; their resulting quantities are
    /// aggregated separately. Each pawn plans inside its outfit filter (GearPawn.AllowTainted) and above its insulation floor
    /// (GearPawn.ColdFloor/HeatFloor; by default its worn insulation, so warmth never drops without the comparator).
    /// </summary>
    public static IReadOnlyList<GearLoadout> PlanColonyGear(IReadOnlyList<GearPawn> pawns)
    {
        var seen = new HashSet<PawnId>();
        foreach (var p in pawns)
        {
            DomainException.Require(FoodForecastRules.FoodId(p.Pawn.Value) && seen.Add(p.Pawn), "invalid gear pawn identity");
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
            input = input with { Options = EquatableArray.From(input.Options.Where(o => o.Source == GearSource.Bill || !used.Contains(o.ID))) };
            // The pawn's outfit filter: tainted apparel is out unless the comparator allowed it (DesiredApparelPolicy).
            var (cold, heat) = GearInsulation(input.Worn);
            using var span = s_spans.StartActivity("gear.loadout");
            var l = PlanGearLoadout(input, p.AllowTainted ? null : o => o.Tainted, (p.ColdFloor ?? cold, p.HeatFloor ?? heat)) with { Pawn = p.Pawn };
            if (l.Findings.Count > 0)
            {
                span?.SetTag("rimgovernor.findings", string.Join(",", l.Findings));
            }
            @out.Add(l);
            foreach (var g in l.Gaps)
            {
                if (g.Source != GearSource.Bill && GearGapWanted(l.Role, g))
                {
                    used.Add(g.Wanted.ID);
                }
            }
        }
        return @out;
    }
}
