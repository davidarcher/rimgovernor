using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// Names an outdoor hazard that holds outdoor work. Scope is the three below; fire, flashstorm and the rest
/// are follow-ups. Any other kind in a known hazard list holds too: unknown kinds fail closed.
/// </summary>
public enum HazardKind
{
    [WireName("")]
    None = 0,
    ExtremeCold,
    ExtremeHeat,
    ToxicFallout,
}

/// <summary>One active hazard on the map.</summary>
public sealed record OutdoorHazard(HazardKind Kind);

public static partial class OutdoorHazardRules
{
    /// <summary>
    /// An outdoor hazard (extreme cold or heat, toxic fallout) is active; outdoor work waits and indoor work
    /// is untouched.
    /// </summary>
    public const string RemoteHoldHazard = "outdoor_hazard";

    /// <summary>
    /// Derives the active hazards: toxic fallout from the condition census, cold and heat from the outdoor
    /// temperature at or past the extreme bounds.
    /// </summary>
    public static EquatableArray<OutdoorHazard> OutdoorHazardsOf(RoundsFacts f)
    {
        var result = new List<OutdoorHazard>();
        if (f.DisasterConditions.Any(c => c.Definition == DisasterRules.ConditionToxicFallout))
        {
            result.Add(new OutdoorHazard(HazardKind.ToxicFallout));
        }
        if (f.OutdoorTemperature <= CombatWaitRules.ExtremeColdC)
        {
            result.Add(new OutdoorHazard(HazardKind.ExtremeCold));
        }
        if (f.OutdoorTemperature >= CombatWaitRules.ExtremeHeatC)
        {
            result.Add(new OutdoorHazard(HazardKind.ExtremeHeat));
        }
        return EquatableArray.From(result);
    }

    /// <summary>Reports whether outdoor work is held: any active hazard.</summary>
    public static bool OutdoorHazardHeld(EquatableArray<OutdoorHazard> hazards) => hazards.Count > 0;

    /// <summary>
    /// OutdoorHazardHeld with an open dash window: the window lifts extreme cold only; heat and fallout still hold.
    /// </summary>
    public static bool OutdoorHazardHeldDash(EquatableArray<OutdoorHazard> hazards, bool dashOpen) =>
        hazards.Any(hazard => !dashOpen || hazard.Kind != HazardKind.ExtremeCold);

    /// <summary>The chop, hunt and forage gate: the same hold the remote requests apply, read from the review facts.</summary>
    public static bool OutdoorAcquisitionHeld(RoundsFacts f) => OutdoorHazardHeld(OutdoorHazardsOf(f));
}
