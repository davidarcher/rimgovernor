using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The slice of RoundsPolicy the prisoners family plans from. ReleaseAfterDays is how long a prisoner the
/// colony has no use for is fed while the food runway holds FoodTargetDays; below that target it is released
/// at once. Zero releases a useless prisoner at once regardless.
/// </summary>
public sealed record PrisonerPolicy
{
    public double ReleaseAfterDays { get; init; }

    public double FoodTargetDays { get; init; }
}

/// <summary>
/// One population read's census (the frame's <c>observations_read_population</c> section), each person joined to its
/// pawn table row: the colony prisoners, every observed humanlike as a custody candidate, the colony side of a
/// prisoner's use, the hosted guests' care cap inputs and the owned-pawn short names.
/// </summary>
public sealed record PrisonerCensus
{
    public required EquatableArray<PrisonerFacts> Prisoners { get; init; }

    /// <summary>The capture/rescue candidate census: every observed humanlike, not only prisoners.</summary>
    public required EquatableArray<CustodyFacts> Custody { get; init; }

    /// <summary>The colony side of each prisoner's use: the free colonists' best skills and the Ideology mode.</summary>
    public required PrisonerColony Colony { get; init; }

    /// <summary>The colony guests' care cap inputs.</summary>
    public required EquatableArray<CarePatient> Guests { get; init; }

    /// <summary>The owned-pawn short-name census.</summary>
    public required EquatableArray<OwnedName> Names { get; init; }
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

    /// <summary>Whether Ideology is installed and, if so, its classic mode and the player faction's primary ideoligion.</summary>
    public ColonyIdeology Ideology { get; init; } = new ColonyIdeology.NotInstalled();

    /// <summary>A Believers goal lacks believers (#3501): a recruitable prisoner of another ideoligion is converted before anything else, whatever the worth threshold says.</summary>
    public bool BelieversWanted { get; init; }

    /// <summary>
    /// The frame's ideoligion fact: slavery and organ use read it through the shared precept rule. The
    /// routine reading fills it; unknown without Ideology.
    /// </summary>
    public Fact<Ideoligion> Ideoligion { get; init; }

    /// <summary>The prisoners the player's goals name (#3499): recruited whatever their worth.</summary>
    public IReadOnlyList<PawnId> NamedPrisoners { get; init; } = [];

    /// <summary>The player's preferences, stamped from the review facts: slavery and organ use read what they forbid.</summary>
    public PreferencesRecord Preferences { get; init; } = new();

    /// <summary>Ideology is installed outside classic mode: its precepts bind the colony.</summary>
    public bool PreceptsBind => Ideology is ColonyIdeology.Installed { Classic: false };

    /// <summary>The player faction's primary ideoligion id while precepts bind; null otherwise or without one.</summary>
    public string? PrimaryIdeo => Ideology is ColonyIdeology.Installed { Classic: false, PrimaryIdeoId: var id } ? id : null;

    /// <summary>
    /// Reports whether the colony's ideoligion lets it enslave without a mood cost or refusal: the precept
    /// rule's stance for the EnslavedPrisoner event is allowed or approved. Classic mode, an unread
    /// ideoligion and a game without Ideology hold; so does a player who forbids slavery.
    /// </summary>
    public bool SlaveryAllowed()
    {
        if (!PreceptsBind || Preferences.Forbids(ForbiddenMethod.Slavery))
        {
            return false;
        }
        var stance = IdeologyRuleRules.ActionStance(Ideoligion, new PreceptAction { HistoryEvent = "EnslavedPrisoner" }, new PreceptSubject()).Stance;
        return stance is PreceptStance.Allowed or PreceptStance.Approved;
    }
}

/// <summary>
/// The interaction a prisoner is under: a mode the colony sets (<see cref="Settable"/>), or one it never sets
/// (<see cref="Other"/>: Execution, a DLC mode the intent does not expose), which the colony leaves alone.
/// </summary>
public abstract record PrisonerHeldInteraction
{
    private protected PrisonerHeldInteraction() { }

    public sealed record Settable(PrisonerInteractionMode Mode) : PrisonerHeldInteraction;

    public sealed record Other : PrisonerHeldInteraction;
}

/// <summary>One observed colony prisoner's population census row.</summary>
public sealed partial record PrisonerFacts
{
    public required PawnId Pawn { get; init; }

    public required bool Dead { get; init; }

    public required bool Recruitable { get; init; }

    public required PrisonerHeldInteraction CurrentInteraction { get; init; }

    /// <summary>Native's remaining recruit resistance.</summary>
    public required double Resistance { get; init; }

    /// <summary>The TimeAsPrisoner record, in ticks.</summary>
    public required long HeldTicks { get; init; }

    /// <summary>The prisoner's ideoligion id, empty for none.</summary>
    public string Ideo { get; init; } = "";

    /// <summary>Bars Enslave.</summary>
    public bool WildMan { get; init; }

    public required PrisonerProspect Prospect { get; init; }

    // Organ harvest facts: the prisoner's operations and queued medical bills, its home faction's id (empty
    // for none) and the goodwill change vanilla's harvest violation would make with it (<= 0). MissingParts:
    // the prisoner's missing parts; removal must retain one leg.
    public EquatableArray<MissingBodyPart> MissingParts { get; init; } = [];

    public EquatableArray<CareOperation> Operations { get; init; } = [];

    public int QueuedSurgeries { get; init; }

    public required int HarvestGoodwill { get; init; }

    /// <summary>The prisoner's MedicalCareCategory.</summary>
    public MedicalCare MedicalCare { get; init; }

    // Care cap inputs: conditions and life threat, the queued bills' recipes and an Execution interaction.
    public EquatableArray<Hediff> Conditions { get; init; } = [];

    public bool LifeThreatening { get; init; }

    public IReadOnlyList<string> QueuedRecipes { get; init; } = [];

    public bool Executing { get; init; }

    /// <summary>The prisoner has a creepjoiner tracker; Kind is its PawnKindDef name.</summary>
    public required bool CreepJoiner { get; init; }

    public string Kind { get; init; } = "";

    internal bool WorthRecruiting(PrisonerColony c) =>
        c.NamedPrisoners.Contains(Pawn) || PopulationRules.PrisonerWorth(Prospect, Resistance, c) >= PopulationRules.RecruitThreshold(c.Colonists);

    /// <summary>
    /// Reports whether a living prisoner is one the colony would not recruit: unrecruitable, or worth below
    /// RecruitThreshold. It only gates organ harvest.
    /// </summary>
    public bool HarvestEligible(PrisonerColony c) => !Dead && (!Recruitable || !WorthRecruiting(c));
}

public static partial class PopulationRules
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
    /// a downed pawn.
    /// </summary>
    internal static PrisonerInteractionMode? PrisonerUse(PrisonerFacts row, PrisonerColony colony, Fact<double> food, PrisonerPolicy p)
    {
        var intent = PrisonerIntent(row, colony, food, p);
        if (intent is null || (row.CurrentInteraction is PrisonerHeldInteraction.Settable { Mode: var current } && current == intent))
        {
            return null;
        }
        if (intent == PrisonerInteractionMode.Release && Legless(row))
        {
            return null;
        }
        return intent;
    }

    /// <summary>Every leg of a human body (two) is missing.</summary>
    private static bool Legless(PrisonerFacts row) =>
        row.MissingParts.Count(m => m.Part.DefName == "Leg") >= 2;

    /// <summary>
    /// The use the colony has for one living prisoner, or null while it is simply held. The rules, in order:
    /// a prisoner under a mode the colony never sets (<see cref="PrisonerHeldInteraction.Other"/>) is left alone;
    /// a recruitable prisoner whose resistance is broken and who is already being recruited keeps recruiting;
    /// worth recruiting (PrisonerWorth >= RecruitThreshold, or named by a player goal) and recruitable: Convert first when Ideology is
    /// active outside classic mode and the prisoner holds another ideoligion, so the recruit joins without a
    /// foreign faith dragging mood and certainty, Recruit once it shares the colony's ideoligion (or without
    /// Ideology); under an unmet Believers goal (<see cref="PrisonerColony.BelieversWanted"/>) a recruitable
    /// prisoner of another ideoligion is converted whatever its worth, since a convert is a believer to recruit;
    /// otherwise, able to labor, not a wild man and the ideoligion allows slavery without mood
    /// cost: Enslave; otherwise Release: once chosen it stays, else at once while the food runway is below
    /// target, else after ReleaseAfterDays in custody. A release that waits on an unread food runway raises
    /// <see cref="RequiredFactException"/>.
    /// </summary>
    internal static PrisonerInteractionMode? PrisonerIntent(PrisonerFacts row, PrisonerColony colony, Fact<double> food, PrisonerPolicy p)
    {
        if (row.Dead || row.CurrentInteraction is not PrisonerHeldInteraction.Settable { Mode: var current })
        {
            return null;
        }
        if (row.Recruitable && current == PrisonerInteractionMode.Recruit && row.Resistance <= 0)
        {
            return PrisonerInteractionMode.Recruit;
        }
        var foreign = colony.PrimaryIdeo is { Length: > 0 } ours && row.Ideo.Length != 0 && row.Ideo != ours;
        if (row.Recruitable && foreign && colony.BelieversWanted)
        {
            return PrisonerInteractionMode.Convert;
        }
        if (row.Recruitable && row.WorthRecruiting(colony))
        {
            return foreign ? PrisonerInteractionMode.Convert : PrisonerInteractionMode.Recruit;
        }
        if (colony.SlaveryAllowed() && !row.WildMan && CanLabor(row.Prospect))
        {
            return PrisonerInteractionMode.Enslave;
        }
        if (current == PrisonerInteractionMode.Release)
        {
            return PrisonerInteractionMode.Release;
        }
        if (!food.TryGetValue(out var days))
        {
            throw new RequiredFactException("food runway");
        }
        if (days < p.FoodTargetDays || row.HeldTicks >= p.ReleaseAfterDays * GameTime.TicksPerDay)
        {
            return PrisonerInteractionMode.Release;
        }
        return null;
    }
}
