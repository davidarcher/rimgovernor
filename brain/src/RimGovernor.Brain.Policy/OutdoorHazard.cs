using RimGovernor.Brain.Domain;

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
    /// Extreme cold is the only hazard and no open warmth window names this trip for its pawn (#4051): the trip
    /// waits for a window, which opens once the pawn's observed hypothermia refills (WarmthRefillTicks).
    /// </summary>
    public const string WarmthHoldBudget = "warmth_budget";

    /// <summary>
    /// Derives the active hazards: toxic fallout from the condition census, cold and heat from the outdoor
    /// temperature at or past the extreme bounds.
    /// </summary>
    public static EquatableArray<OutdoorHazard> OutdoorHazardsOf(ReviewFacts f)
    {
        var result = new List<OutdoorHazard>();
        if (f.DisasterConditions.Any(c => c.Definition == DisasterRules.ConditionToxicFallout))
        {
            result.Add(new OutdoorHazard(HazardKind.ToxicFallout));
        }
        if (f.Census.OutdoorTemperatureC <= CombatWaitRules.ExtremeColdC)
        {
            result.Add(new OutdoorHazard(HazardKind.ExtremeCold));
        }
        if (f.Census.OutdoorTemperatureC >= CombatWaitRules.ExtremeHeatC)
        {
            result.Add(new OutdoorHazard(HazardKind.ExtremeHeat));
        }
        return EquatableArray.From(result);
    }

    /// <summary>Reports whether outdoor work is held: any active hazard.</summary>
    public static bool OutdoorHazardHeld(EquatableArray<OutdoorHazard> hazards) => hazards.Count > 0;

    /// <summary>
    /// The hold on one trip: "" when free; <see cref="RemoteHoldHazard"/> for any hazard other than extreme cold
    /// (heat and fallout hold whatever the windows say); <see cref="WarmthHoldBudget"/> under extreme cold unless
    /// <paramref name="named"/>, an open warmth window naming the trip.
    /// </summary>
    public static string TripHold(EquatableArray<OutdoorHazard> hazards, bool named)
    {
        var cold = false;
        foreach (var h in hazards)
        {
            if (h.Kind != HazardKind.ExtremeCold)
            {
                return RemoteHoldHazard;
            }
            cold = true;
        }
        return cold && !named ? WarmthHoldBudget : "";
    }

    /// <summary>Whether some window open at <paramref name="now"/> names the trip.</summary>
    public static bool TripNamed(EquatableArray<WarmthWindow> windows, Tick now, WarmthTripKind kind, string id)
    {
        foreach (var w in windows)
        {
            if (w.OpenAt(now) && w.Names(kind, id))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The hold on a trip any window may name: remote loot and salvage, which native staffs itself.</summary>
    public static string TripHold(ReviewFacts f, WarmthTripKind kind, string id) =>
        TripHold(OutdoorHazardsOf(f), TripNamed(f.Warmth, f.Tick, kind, id));

    /// <summary>The hold on <paramref name="pawn"/>'s trip: lifted only by that pawn's own open window naming it.</summary>
    public static string TripHold(ReviewFacts f, PawnId pawn, WarmthTripKind kind, string id)
    {
        var w = f.WarmthWindowOf(pawn);
        return TripHold(OutdoorHazardsOf(f), w.OpenAt(f.Tick) && w.Names(kind, id));
    }

    /// <summary>Whether <paramref name="pawn"/>'s trip is held (TripHold is not empty).</summary>
    public static bool TripHeld(ReviewFacts f, PawnId pawn, WarmthTripKind kind, string id) => TripHold(f, pawn, kind, id).Length != 0;

    /// <summary>
    /// The chop, formation hunt and other all-hazards gate: the same hold the remote requests apply, read from
    /// the review facts. A lone hunt or forage reads <see cref="AcquisitionTripHolds"/> instead.
    /// </summary>
    public static bool OutdoorAcquisitionHeld(ReviewFacts f) => OutdoorHazardHeld(OutdoorHazardsOf(f));

    /// <summary>
    /// Holds the acquisition census per source under a hazard (#4051): a tree (chop) under any hazard, a hunt or
    /// a plant unless an open warmth window names it as a lone hunt or forage (the window's pawn is the one
    /// PlanSheltering lets out; native staffs the designation). Held sources are written into
    /// <paramref name="held"/>, which the selections skip. Returns "" while some source is free (or there is no
    /// hazard), else the reason every source shares: the hazard's, or the warmth budget's under cold alone.
    /// </summary>
    public static string AcquisitionTripHolds(ReviewFacts f, IEnumerable<AcquisitionSource> sources, IDictionary<string, bool> held)
    {
        var hazards = OutdoorHazardsOf(f);
        if (hazards.Count == 0)
        {
            return "";
        }
        // The shared reason is the budget's when some source waits for a window (it names the refill), else the hazard's.
        var reason = TripHold(hazards, false);
        var free = false;
        var budget = false;
        var any = false;
        foreach (var s in sources)
        {
            var hold = s.Tree ? RemoteHoldHazard : TripHold(f, s.Hunt ? WarmthTripKind.LoneHunt : WarmthTripKind.Forage, s.Id);
            if (hold.Length == 0)
            {
                free = true;
                continue;
            }
            held[s.Id] = true;
            any = true;
            budget |= hold == WarmthHoldBudget;
        }
        return free ? "" : budget ? WarmthHoldBudget : any ? RemoteHoldHazard : reason;
    }
}
