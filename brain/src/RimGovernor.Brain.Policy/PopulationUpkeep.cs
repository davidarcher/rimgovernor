using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

public static partial class PolicyConstants
{
    /// <summary>
    /// MaintainPopulation names Population-*'s prisoner interaction deficit: any observed, living colony
    /// prisoner whose exclusive interaction is not the use the colony has for it (prisonerUse). The governor
    /// chooses each prisoner's use itself -- recruit, convert, enslave or release -- with no player-only
    /// exemption; execution and the non-exclusive toggles are never proposed. Native eligibility
    /// (recruitable, alive, prisoner, not a wild man barred from the mode, Ideology for Enslave/Convert) is
    /// re-validated by native when it applies the intent; this only decides which already-observed prisoner
    /// gets which write.
    /// </summary>
    public static readonly ConcernId MaintainPopulation = new("MaintainPopulation");
}

/// <summary>
/// The slice of RoundsPolicy MaintainPopulation plans from. ReleaseAfterDays is how long a prisoner the
/// colony has no use for is fed while the food runway holds FoodTargetDays; below that target it is released
/// at once. Zero releases a useless prisoner at once regardless.
/// </summary>
public sealed record PrisonerPolicy
{
    public double ReleaseAfterDays { get; init; }

    public double FoodTargetDays { get; init; }
}

/// <summary>
/// The storyteller's colony-level population state read with the population census:
/// StorytellerUtilityPopulation's intent and adjusted population, the chance a non-colony humanlike downed by
/// violence dies now, and the chance a new prisoner is unrecruitable.
/// </summary>
public sealed record PopulationOutlook
{
    public Fact<double> Intent { get; init; }

    public Fact<double> AdjustedPopulation { get; init; }

    public Fact<double> DeathOnDownedChance { get; init; }

    public Fact<double> UnrecruitableChance { get; init; }
}

/// <summary>One pawn's native skill row. Passion is "None", "Minor" or "Major".</summary>
public sealed record PrisonerSkill
{
    public string Name { get; init; } = "";

    public int Level { get; init; }

    public string Passion { get; init; } = "";

    public bool Disabled { get; init; }
}

/// <summary>One pawn's native trait row. Worth is the net worth of the degree (TraitBalance.Worth); zero when the read had no catalog to derive it.</summary>
public sealed record PrisonerTrait
{
    public string Def { get; init; } = "";

    public int Degree { get; init; }

    public double Worth { get; init; }
}

/// <summary>The biography a prisoner's use is judged by. Health is SummaryHealthPercent, 0..1; Incapable lists the work types the pawn can never do.</summary>
public sealed record PrisonerProspect
{
    public double Age { get; init; }

    public double Health { get; init; }

    public IReadOnlyList<PrisonerSkill> Skills { get; init; } = [];

    public IReadOnlyList<PrisonerTrait> Traits { get; init; } = [];

    public IReadOnlyList<string> Incapable { get; init; } = [];
}

/// <summary>
/// The colony side of the decision: the free colonists' best level per skill and the Ideology facts Enslave
/// and Convert need.
/// </summary>
public sealed record PrisonerColony
{
    public int Colonists { get; init; }

    public IReadOnlyDictionary<string, int> BestSkill { get; init; } = new Dictionary<string, int>();

    public bool IdeologyActive { get; init; }

    public bool ClassicIdeo { get; init; }

    /// <summary>The player faction's primary ideoligion id; empty without Ideology.</summary>
    public string Ideo { get; init; } = "";

    /// <summary>
    /// The frame's ideoligion fact: slavery and organ use read it through the shared precept rule. The
    /// routine reading fills it; unknown without Ideology.
    /// </summary>
    public Fact<Ideoligion> Ideology { get; init; }

    /// <summary>
    /// Each free colonist's Medicine level by pawn, doctors only (the skill not disabled): the doctor
    /// training floor counts it and the training step names its surgeon from it.
    /// </summary>
    public IReadOnlyDictionary<PawnId, int> Medicine { get; init; } = new Dictionary<PawnId, int>();

    /// <summary>
    /// Reports whether the colony's ideoligion lets it enslave without a mood cost or refusal: the precept
    /// rule's stance for the EnslavedPrisoner event is allowed or approved. Classic mode, an unread
    /// ideoligion and a game without Ideology hold.
    /// </summary>
    public bool SlaveryAllowed()
    {
        if (!IdeologyActive || ClassicIdeo)
        {
            return false;
        }
        var stance = PolicyFunctions.ActionStance(Ideology, new PreceptAction { HistoryEvent = "EnslavedPrisoner" }, new PreceptSubject()).Stance;
        return stance is PreceptStance.Allowed or PreceptStance.Approved;
    }
}

/// <summary>One observed prisoner's population census row.</summary>
public sealed partial record PrisonerFacts
{
    public PawnId Pawn { get; init; }

    public Fact<bool> Dead { get; init; }

    public Fact<bool> Prisoner { get; init; }

    public Fact<bool> Recruitable { get; init; }

    public Fact<PrisonerInteractionMode> CurrentInteraction { get; init; }

    /// <summary>Native's remaining recruit resistance.</summary>
    public Fact<double> Resistance { get; init; }

    /// <summary>The TimeAsPrisoner record, in ticks.</summary>
    public Fact<long> HeldTicks { get; init; }

    /// <summary>Pawn_GuestTracker.will (Ideology only).</summary>
    public Fact<double> Will { get; init; }

    /// <summary>The prisoner's ideoligion id, empty for none.</summary>
    public string Ideo { get; init; } = "";

    /// <summary>Bars Enslave.</summary>
    public bool WildMan { get; init; }

    public Fact<PrisonerProspect> Prospect { get; init; }

    // Organ harvest facts: the prisoner's operations and queued medical bills, its home faction's id (empty
    // for none) and the goodwill change vanilla's harvest violation would make with it (<= 0). MissingParts:
    // the prisoner's missing parts; removal must retain one leg.
    public Fact<IReadOnlyList<MissingPart>> MissingParts { get; init; }

    public Fact<IReadOnlyList<SurgeryOperation>> Operations { get; init; }

    public Fact<int> QueuedSurgeries { get; init; }

    public string Faction { get; init; } = "";

    public Fact<int> HarvestGoodwill { get; init; }

    /// <summary>The prisoner's MedicalCareCategory name.</summary>
    public Fact<string> MedicalCare { get; init; }

    /// <summary>The prisoner carries a drug addiction it cannot feed, so it is in or near withdrawal.</summary>
    public Fact<bool> Withdrawal { get; init; }

    // Care cap inputs: conditions and life threat, the queued bills' recipes and an Execution interaction.
    public Fact<IReadOnlyList<CareCondition>> Conditions { get; init; }

    public Fact<bool> LifeThreatening { get; init; }

    public IReadOnlyList<string> QueuedRecipes { get; init; } = [];

    public bool Executing { get; init; }

    /// <summary>The prisoner's current drug policy and chemicals.</summary>
    public Fact<PawnPolicyInputs> PolicyInputs { get; init; }

    /// <summary>The prisoner has a creepjoiner tracker (unknown while that read failed); Kind is its PawnKindDef name.</summary>
    public Fact<bool> CreepJoiner { get; init; }

    public string Kind { get; init; } = "";

    /// <summary>The row's PrisonerWorth against the colony; Unknown without a prospect.</summary>
    public Fact<double> Worth(PrisonerColony c)
    {
        if (!Prospect.TryGetValue(out var p))
        {
            return Unknown<double>();
        }
        Resistance.TryGetValue(out var resistance);
        return Known(PolicyFunctions.PrisonerWorth(p, resistance, c));
    }

    internal bool WorthRecruiting(PrisonerProspect p, PrisonerColony c)
    {
        Resistance.TryGetValue(out var resistance);
        return PolicyFunctions.PrisonerWorth(p, resistance, c) >= PolicyFunctions.RecruitThreshold(c.Colonists);
    }

    /// <summary>
    /// Reports whether a living prisoner is one the colony would not recruit: unrecruitable, or worth below
    /// RecruitThreshold. It only gates organ harvest; Unknown when a needed fact is missing.
    /// </summary>
    public Fact<bool> HarvestEligible(PrisonerColony c)
    {
        var dk = Dead.TryGetValue(out var dead);
        var rk = Recruitable.TryGetValue(out var recruitable);
        var pk = Prospect.TryGetValue(out var p);
        if (!dk || !rk || !pk)
        {
            return Unknown<bool>();
        }
        return Known(!dead && (!recruitable || !WorthRecruiting(p!, c)));
    }
}

/// <summary>Names why the prisoner interaction planner did or did not propose an interaction write.</summary>
public enum PrisonerPlanReason
{
    [WireName("")]
    None = 0,
    [WireName("no_recruitable_prisoner")]
    NoDeficit,
    [WireName("population_census_unknown")]
    Unknown,
}

/// <summary>
/// The one prisoner/mode pair chosen for an interaction write, mirroring HusbandryChoice's
/// single-write-per-cycle rule. Interaction is set whenever Reason is None.
/// </summary>
public sealed record PrisonerChoice
{
    public PrisonerPlanReason Reason { get; init; }

    public PawnId Pawn { get; init; }

    public PrisonerInteractionMode? Interaction { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Scores a prospect against the colony: +1 per usable skill at 8+, +1 more at 12+, +1 per major and
    /// +0.5 per minor passion, +2 per skill at 6+ that beats the colony's best by 3 or more (a gap the colony
    /// lacks), +1 per good and -2 per bad trait (the sign of TraitBalance.Worth), -0.5 per incapable work
    /// type, -1 under 16 or over 60, -1 below half health, and -1 per 10 points of native recruit resistance
    /// still to break (0 when unknown). Recruiting needs RecruitThreshold; organ harvest considers only
    /// prisoners below it.
    /// </summary>
    public static double PrisonerWorth(PrisonerProspect p, double resistance, PrisonerColony c)
    {
        var score = -resistance / 10;
        foreach (var s in p.Skills)
        {
            if (s.Disabled)
            {
                continue;
            }
            if (s.Level >= 8)
            {
                score++;
            }
            if (s.Level >= 12)
            {
                score++;
            }
            switch (s.Passion)
            {
                case "Major":
                    score++;
                    break;
                case "Minor":
                    score += 0.5;
                    break;
            }
            var ok = c.BestSkill.TryGetValue(s.Name, out var best);
            if (s.Level >= 6 && (!ok || s.Level >= best + 3))
            {
                score += 2;
            }
        }
        foreach (var t in p.Traits)
        {
            if (t.Worth > 0)
            {
                score++;
            }
            if (t.Worth < 0)
            {
                score -= 2;
            }
        }
        score -= 0.5 * p.Incapable.Count;
        if (p.Age < 16 || p.Age > 60)
        {
            score--;
        }
        if (p.Health < 0.5)
        {
            score--;
        }
        return score;
    }

    /// <summary>
    /// The worth a prospect needs: a colony of fewer than 3 takes almost anyone (2), fewer than 6 wants a
    /// solid hand (4), a larger one only a strong one (6).
    /// </summary>
    public static double RecruitThreshold(int colonists) => colonists < 3 ? 2 : colonists < 6 ? 4 : 6;

    /// <summary>Reports whether a prospect would be a useful slave: at least 13, at least 40% health and incapable of at most four work types.</summary>
    internal static bool CanLabor(PrisonerProspect p) => p.Age >= 13 && p.Health >= 0.4 && p.Incapable.Count <= 4;

    /// <summary>
    /// The interaction one living prisoner should have, or null when the row is settled: PrisonerIntent's use
    /// when the prisoner does not already have it. A legless prisoner is not released: vanilla cannot release
    /// a downed pawn, and MaintainSurgery puts a peg leg back first. Unknown reports a fact the decision
    /// needed but the census did not carry; an unknown fact is never evidence of a settled prisoner.
    /// </summary>
    internal static (PrisonerInteractionMode? Want, bool Unknown) PrisonerUse(PrisonerFacts row, PrisonerColony colony, Fact<double> food, PrisonerPolicy p)
    {
        var (intent, unknown) = PrisonerIntent(row, colony, food, p);
        PrisonerInteractionMode? current = row.CurrentInteraction.TryGetValue(out var c) ? c : null;
        if (unknown || intent is null || intent == current)
        {
            return (null, unknown);
        }
        if (intent == PrisonerInteractionMode.Release && row.Legless())
        {
            return (null, false);
        }
        return (intent, false);
    }

    /// <summary>
    /// The use the colony has for one living prisoner, or null while it is simply held. The rules, in order:
    /// a recruitable prisoner whose resistance is broken and who is already being recruited keeps recruiting;
    /// worth recruiting (PrisonerWorth >= RecruitThreshold) and recruitable: Convert first when Ideology is
    /// active outside classic mode and the prisoner holds another ideoligion, so the recruit joins without a
    /// foreign faith dragging mood and certainty, Recruit once it shares the colony's ideoligion (or without
    /// Ideology); otherwise, able to labor, not a wild man and the ideoligion allows slavery without mood
    /// cost: Enslave; otherwise Release: once chosen it stays, else at once while the food runway is below
    /// target, else after ReleaseAfterDays in custody.
    /// </summary>
    internal static (PrisonerInteractionMode? Intent, bool Unknown) PrisonerIntent(PrisonerFacts row, PrisonerColony colony, Fact<double> food, PrisonerPolicy p)
    {
        if (!row.Dead.TryGetValue(out var dead))
        {
            return (null, true);
        }
        if (dead)
        {
            return (null, false);
        }
        var rk = row.Recruitable.TryGetValue(out var recruitable);
        var ck = row.CurrentInteraction.TryGetValue(out var current);
        var pk = row.Prospect.TryGetValue(out var prospect);
        if (!rk || !ck || !pk)
        {
            return (null, true);
        }
        if (recruitable && current == PrisonerInteractionMode.Recruit)
        {
            if (row.Resistance.TryGetValue(out var resistance) && resistance <= 0)
            {
                return (PrisonerInteractionMode.Recruit, false);
            }
        }
        if (recruitable && row.WorthRecruiting(prospect!, colony))
        {
            if (colony.IdeologyActive && !colony.ClassicIdeo && colony.Ideo.Length != 0 && row.Ideo.Length != 0 && row.Ideo != colony.Ideo)
            {
                return (PrisonerInteractionMode.Convert, false);
            }
            return (PrisonerInteractionMode.Recruit, false);
        }
        if (colony.SlaveryAllowed() && !row.WildMan && CanLabor(prospect!))
        {
            return (PrisonerInteractionMode.Enslave, false);
        }
        if (current == PrisonerInteractionMode.Release)
        {
            return (PrisonerInteractionMode.Release, false);
        }
        if (!food.TryGetValue(out var days))
        {
            return (null, true);
        }
        if (days < p.FoodTargetDays)
        {
            return (PrisonerInteractionMode.Release, false);
        }
        if (!row.HeldTicks.TryGetValue(out var held))
        {
            return (null, true);
        }
        if (held >= p.ReleaseAfterDays * GameTime.TicksPerDay)
        {
            return (PrisonerInteractionMode.Release, false);
        }
        return (null, false);
    }

    /// <summary>
    /// Reports whether any observed, living prisoner still needs an interaction write. An unknown census, or
    /// any prisoner whose facts are incomplete for the decision, leaves the whole need unknown rather than
    /// silently treating it as recovered. The colony facts matter only once a prisoner is held.
    /// </summary>
    public static Fact<bool> PrisonerRecruitDeficit(Fact<IReadOnlyList<PrisonerFacts>> prisoners, Fact<PrisonerColony> colony, Fact<double> food, PrisonerPolicy p)
    {
        if (!prisoners.TryGetValue(out var rows))
        {
            return Unknown<bool>();
        }
        if (rows.Count == 0)
        {
            return Known(false);
        }
        if (!colony.TryGetValue(out var c))
        {
            return Unknown<bool>();
        }
        var deficit = false;
        foreach (var row in rows)
        {
            var (want, unknown) = PrisonerUse(row, c, food, p);
            if (unknown)
            {
                return Unknown<bool>();
            }
            if (want is not null)
            {
                deficit = true;
            }
        }
        return Known(deficit);
    }

    /// <summary>
    /// Picks the lowest-pawn-ID living prisoner still needing a write, and the write it needs, to dispatch
    /// next. A row whose facts are incomplete is skipped rather than blocking the rest.
    /// </summary>
    public static PrisonerChoice SelectPrisonerInteractionMethod(Fact<IReadOnlyList<PrisonerFacts>> prisoners, Fact<PrisonerColony> colony, Fact<double> food, PrisonerPolicy p)
    {
        var known = prisoners.TryGetValue(out var rows);
        var ck = colony.TryGetValue(out var c);
        if (!known || (rows!.Count > 0 && !ck))
        {
            return new PrisonerChoice { Reason = PrisonerPlanReason.Unknown };
        }
        foreach (var row in rows!.OrderBy(r => r.Pawn))
        {
            var (want, unknown) = PrisonerUse(row, c!, food, p);
            if (!unknown && want is not null)
            {
                return new PrisonerChoice { Pawn = row.Pawn, Interaction = want };
            }
        }
        return new PrisonerChoice { Reason = PrisonerPlanReason.NoDeficit };
    }
}
