namespace RimGovernor.Brain.Policy;

/// <summary>What the power family reads (#3257): the foothold power standing and whether a solar flare holds the grid off.</summary>
public sealed record PowerFacts
{
    /// <summary>Whether the grid covers its required draw with no consumer disabled and no weather outage owed (<see cref="RoundsRules.FootholdPower"/>).</summary>
    public bool Standing { get; init; }

    /// <summary>A solar flare with a known remaining duration switches every powered building off (<see cref="DisasterRules.PowerOutageHold"/>).</summary>
    public bool OutageHeld { get; init; }

    /// <summary>The facts a review already holds.</summary>
    public static PowerFacts From(RoundsFacts f) => new()
    {
        Standing = RoundsRules.FootholdPower(f),
        OutageHeld = DisasterRules.PowerOutageHold(f.DisasterConditions),
    };
}

/// <summary>
/// The power family (#3257): one Need, <c>power/basic</c>, met while the grid covers its draw. Its Methods are the
/// power planner's generators, batteries and conduits, which the runtime's work step commits under the Need. A
/// solar flare holds it without a Method: the deficit it measures is real, but a generator does not answer it.
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

    /// <summary>A flare holds the ready Need; otherwise every Method is the work step's.</summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, PowerFacts facts) =>
        facts.OutageHeld && ready.Any(n => n.Id == Basic)
            ? [NeedMethod.Held(Basic, OutageHold, "a solar flare switches the grid off; the planner waits for it to end")]
            : [];
}

public static partial class PowerRules
{
    /// <summary>The colony's power, a plain threshold: met while the grid stands, unmet while it is short. Its deficit is whole.</summary>
    public static Need PowerNeedOf(PowerFacts f) =>
        FoodRules.WholeNeed(PowerFamily.Basic, NeedTier.Wellbeing, f.Standing);
}
