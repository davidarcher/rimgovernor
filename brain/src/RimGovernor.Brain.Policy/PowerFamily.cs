namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the power family reads (#3257): the foothold power standing, whether a solar flare holds the grid off, and the facts the
/// lost producers' restoration is compared from (task 5.3), re-derived each review.
/// </summary>
public sealed record PowerFacts
{
    /// <summary>The restoration choice's facts; empty on the immediate pass, which reads nothing.</summary>
    public PowerRestorationRequest Restoration { get; init; } = new();

    /// <summary>Whether the grid covers its required draw with no consumer disabled and no weather outage owed (<see cref="Rules.FootholdPower"/>).</summary>
    public bool Standing { get; init; }

    /// <summary>A solar flare with a known remaining duration switches every powered building off (<see cref="DisasterRules.PowerOutageHold"/>).</summary>
    public bool OutageHeld { get; init; }

    /// <summary>The facts a review already holds.</summary>
    public static PowerFacts From(ReviewFacts f) => new()
    {
        Standing = Rules.FootholdPower(f),
        OutageHeld = DisasterRules.PowerOutageHold(f.DisasterConditions),
    };
}

/// <summary>
/// The power family (#3257): one Need, <c>power/basic</c>, met while the grid covers its draw. Its pure Methods restore the
/// producers an empty tank or a breakdown took off a short network (<see cref="PowerRestorationRules"/>, task 5.3): a chosen
/// refuel, or the fuel acquisition an acquire-then-refuel chain starts with. The power planner's generators, batteries and
/// conduits are the runtime's work step, which counts only restorable producers toward a network's capacity. The Need closes
/// only on fresh facts (output meets demand): an accepted refuel restores nothing by itself. A solar flare holds it without a
/// Method: the deficit it measures is real, but a generator does not answer it.
/// </summary>
public sealed class PowerFamily : IFamily<PowerFacts>
{
    public const string FamilyId = "power";

    public static readonly NeedId Basic = NeedId.Of(FamilyId, "basic");

    /// <summary>The refusal subject a solar-flare outage holds the Need with.</summary>
    public const string OutageHold = "power_outage";

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Colony, FactFamily.World];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(PowerFacts facts) => [PowerRules.PowerNeedOf(facts)];

    public IReadOnlyList<Need> DeriveSeriousHarm(PowerFacts facts) => [];

    /// <summary>A flare holds the ready Need; otherwise its restoration Methods, and the planner's are the work step's.</summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, PowerFacts facts)
    {
        if (!ready.Any(n => n.Id == Basic)) return [];
        if (facts.OutageHeld) return [NeedMethod.Held(Basic, OutageHold, "a solar flare switches the grid off; the planner waits for it to end")];
        return PowerRestorationRules.Methods(Basic, PowerRestorationRules.Decide(facts.Restoration));
    }
}

public static partial class PowerRules
{
    /// <summary>The colony's power, a plain threshold: met while the grid stands, unmet while it is short. Its deficit is whole.</summary>
    public static Need PowerNeedOf(PowerFacts f) =>
        FoodRules.WholeNeed(PowerFamily.Basic, NeedTier.Wellbeing, f.Standing);
}
