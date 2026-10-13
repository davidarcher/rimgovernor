namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    // Remote work (loot recovery, ruin salvage, surface mining outside the base extent) is held for one of
    // explicit reasons. Selection reports them on the review's hold rows; dispatch mirrors them as the
    // pending action's HeldReason through the executor's refusal table. Every other selection reason
    // (demand) keeps its own text.

    /// <summary>A known hostile is on the map. The threat explains the hold even where it also makes the route read unsafe.</summary>
    public const string RemoteHoldThreat = "threat_present";

    /// <summary>Native walked no safe colonist route to the target and back to storage, or the target itself is unsafe.</summary>
    public const string RemoteHoldRouteUnsafe = "route_unsafe";

    /// <summary>Removing the target would drop a roof, or the deposit is not an open-surface rock.</summary>
    public const string RemoteHoldRoofSupport = "roof_support_risk";

    /// <summary>A supported deposit no colonist can reach yet; the corridor dig opens it. Not a roof risk.</summary>
    public const string RemoteHoldBuried = "buried";

    /// <summary>No accepting storage headroom for the yield.</summary>
    public const string RemoteHoldMissingStorage = "missing_storage";

    /// <summary>Urgent colony work outranks acquisition.</summary>
    public const string RemoteHoldUrgentWork = "urgent_competing_work";

    // The dispatch-time mirrors (Go: UnsafeRoute, RoofSupportRisk, StorageMissing, UrgentCompetingWork of type
    // Reason): a pending remote action's inspection refuses with these and the executor records them as the
    // action's hold reason. They are members of the Reason enum, which belongs to the admission slice.

    /// <summary>UrgentWorkPriority outranks every demand priority (1-100) so urgent work holds acquisition whatever the shortage.</summary>
    public const int UrgentWorkPriority = 100;

    /// <summary>
    /// Known true while a colonist is an urgent patient (bleeding, or downed with a tend outstanding) or a
    /// disaster still disrupts a colony service. Unknown patients leave it unknown; the dispatch-time
    /// emergency check holds on the same unknown facts.
    /// </summary>
    public static Fact<bool> UrgentWorkCompeting(RoundsFacts f)
    {
        if (!f.UrgentPatients.TryGetValue(out var patients))
        {
            return Unknown<bool>();
        }
        if (patients > 0)
        {
            return Known(true);
        }
        if (f.Disaster is not null)
        {
            switch (f.Disaster.Phase)
            {
                case DisasterPhase.Disrupted:
                case DisasterPhase.Survival:
                    return Known(true);
                default:
                    break;
            }
        }
        return Known(false);
    }

    /// <summary>
    /// Turns the urgent work verdict into the scoring competition: unknown urgency competes with nothing,
    /// since the emergency path holds dispatch on unknown facts and reach never widens on them.
    /// </summary>
    public static AcquisitionCompetition RemoteCompetition(RoundsFacts f)
    {
        if (UrgentWorkCompeting(f).TryGetValue(out var urgent) && urgent)
        {
            return new AcquisitionCompetition { UrgentPriority = UrgentWorkPriority };
        }
        return new AcquisitionCompetition();
    }

    /// <summary>
    /// Folds an eligibility or demand reason into the explicit vocabulary above and returns any other reason
    /// unchanged. An "ineligible" salvage or loot candidate failed native route safety; an ineligible mining
    /// deposit failed the open-surface roof check.
    /// </summary>
    public static string RemoteHoldReason(RemoteWorkKind kind, string reason)
    {
        var baseReason = reason;
        if (baseReason.StartsWith("demand:", StringComparison.Ordinal))
        {
            var i = baseReason.IndexOf(':', StringComparison.Ordinal);
            if (i >= 0)
            {
                baseReason = baseReason[(i + 1)..];
            }
        }
        switch (baseReason)
        {
            case "threat_present":
                return RemoteHoldThreat;
            case "no_storage":
            case "storage_unknown":
            case "no_storage_headroom":
                return RemoteHoldMissingStorage;
            case "competing_urgent_work":
                return RemoteHoldUrgentWork;
            case "route_impassable":
            case "route_unknown":
                return RemoteHoldRouteUnsafe;
            case "roof_blocker":
                return RemoteHoldRoofSupport;
            case "ineligible":
                return kind == RemoteWorkKind.Mining ? RemoteHoldRoofSupport : RemoteHoldRouteUnsafe;
            default:
                return reason;
        }
    }

    /// <summary>
    /// Reports the threat hold ahead of any route verdict: a hostile on the map is what the operator needs to
    /// know. An unknown threat holds too.
    /// </summary>
    internal static string RemoteThreatHold(RemoteSafety r)
    {
        if (!r.Threat.TryGetValue(out var threat))
        {
            return "threat_unknown";
        }
        if (threat)
        {
            return RemoteHoldThreat;
        }
        if (OutdoorHazardHeldDash(r.Hazard, r.DashOpen))
        {
            return RemoteHoldHazard;
        }
        return "";
    }
}

public enum RemoteWorkKind
{
    Loot,
    Salvage,
    Mining,
}

/// <summary>Names one candidate a remote selection kept back.</summary>
public sealed record RemoteWorkHold
{
    public RemoteWorkKind Kind { get; init; }

    public string Target { get; init; } = "";

    public string Reason { get; init; } = "";
}

/// <summary>
/// The observed outdoor safety every remote selection holds on: a hostile on the map (unknown holds too) and
/// the outdoor hazard, which an open dash window lifts for extreme cold only. Distance is not a hold; the
/// supply planner prices it.
/// </summary>
public sealed record RemoteSafety
{
    public Fact<bool> Threat { get; init; }

    public Fact<EquatableArray<OutdoorHazard>> Hazard { get; init; }

    /// <summary>A planned dash window is open (DashWindow.OpenAt).</summary>
    public bool DashOpen { get; init; }
}

/// <summary>
/// Carries what every remote selection filters against: the outdoor safety, the demand and the urgent work
/// competing for the colonists. Extent geometry decides which loot is base scope; the rest is remote.
/// </summary>
public sealed record RemoteWorkRequest
{
    public RemoteSafety Safety { get; init; } = new();

    public Fact<ColonyExtent> Extent { get; init; }

    public Fact<EquatableArray<ResourceDemand>> Demand { get; init; }

    public AcquisitionCompetition Competition { get; init; } = new();

    /// <summary>Plan orders mine sources by LayoutPlan.MineTier.</summary>
    public Fact<LayoutPlan> Plan { get; init; }
}
