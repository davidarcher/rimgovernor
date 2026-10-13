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

public static partial class PolicyConstants
{
    /// <summary>
    /// An outdoor hazard (extreme cold or heat, toxic fallout) is active; outdoor work waits and indoor work
    /// is untouched.
    /// </summary>
    public const string RemoteHoldHazard = "outdoor_hazard";
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Derives the active hazards: toxic fallout from the condition census, cold and heat from the outdoor
    /// temperature at or past the extreme bounds. Unknown while either read is missing, so a map without the
    /// reads is never held for it.
    /// </summary>
    public static Fact<EquatableArray<OutdoorHazard>> OutdoorHazardsOf(RoundsFacts f)
    {
        if (!f.DisasterConditions.TryGetValue(out var conditions) || !f.OutdoorTemperature.TryGetValue(out var temp))
        {
            return Unknown<EquatableArray<OutdoorHazard>>();
        }
        var result = new List<OutdoorHazard>();
        if (conditions.Any(c => c.Definition == ConditionToxicFallout))
        {
            result.Add(new OutdoorHazard(HazardKind.ToxicFallout));
        }
        if (temp <= ExtremeColdC)
        {
            result.Add(new OutdoorHazard(HazardKind.ExtremeCold));
        }
        if (temp >= ExtremeHeatC)
        {
            result.Add(new OutdoorHazard(HazardKind.ExtremeHeat));
        }
        return Known(EquatableArray.From(result));
    }

    /// <summary>Reports whether outdoor work is held: any known active hazard, including a kind this build does not recognise. Unknown never holds.</summary>
    public static bool OutdoorHazardHeld(Fact<EquatableArray<OutdoorHazard>> h) =>
        h.TryGetValue(out var hazards) && hazards.Count > 0;

    /// <summary>
    /// OutdoorHazardHeld with an open dash window: the window lifts extreme cold only; heat, fallout and any
    /// unrecognised kind still hold.
    /// </summary>
    public static bool OutdoorHazardHeldDash(Fact<EquatableArray<OutdoorHazard>> h, bool dashOpen)
    {
        if (!h.TryGetValue(out var hazards))
        {
            return false;
        }
        return hazards.Any(hazard => !dashOpen || hazard.Kind != HazardKind.ExtremeCold);
    }

    /// <summary>The chop, hunt and forage gate: the same hold the remote requests apply, read from the review facts.</summary>
    public static bool OutdoorAcquisitionHeld(RoundsFacts f) => OutdoorHazardHeld(OutdoorHazardsOf(f));
}
