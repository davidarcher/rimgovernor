namespace RimGovernor.Brain.Policy;

/// <summary>
/// Mirrors SecureSuppliesHaulerFacts' eligibility shape applied to the Firefighting work type: the candidate
/// filter for the firefight action (dead/downed/drafted/mental state, needs-tend/bleeding, and an enabled
/// non-zero-priority work setting). (Go <c>FireSafetyPawnFacts</c>.)
/// </summary>
public sealed record FireSafetyPawnFacts
{
    public Domain.PawnId Pawn { get; init; }

    public Fact<bool> Dead { get; init; }

    public Fact<bool> Downed { get; init; }

    public Fact<bool> Drafted { get; init; }

    public Fact<bool> MentalState { get; init; }

    public Fact<bool> PlayerForced { get; init; }

    public Fact<bool> NeedsTend { get; init; }

    public Fact<bool> Bleeding { get; init; }

    public Fact<bool> FirefightingEnabled { get; init; }
}

/// <summary>
/// Names the three-way firefight branch: recovered (no active fire), waiting on the native, non-orderable
/// firefighting WorkGiver because an eligible worker exists, or blocked because no such worker exists and the
/// emergency hold must be retained. (Go <c>FireSafetyOutcome</c>.)
/// </summary>
public enum FireSafetyOutcome
{
    Recovered,

    [WireName("waiting_for_native_fire")]
    WaitingForNative,

    Blocked,

    Unknown,
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Go <c>EvaluateFireSafety</c>: decides the firefight branch from the evidence this project's wire schema
    /// actually exposes: a colony-wide eligible-firefighter existence check paired with ReviewUpkeep's own
    /// Unsafe verdict, rather than a per-fire safety claim the native layer has not confirmed. An unsafe fire
    /// still waits on native firefighting when a worker is eligible: MaintainFireSafety deselects development,
    /// so holding the clock there would freeze the colony forever.
    /// </summary>
    public static FireSafetyOutcome EvaluateFireSafety(bool active, bool known, IReadOnlyList<FireSafetyPawnFacts> pawns)
    {
        if (!active)
        {
            return FireSafetyOutcome.Recovered;
        }
        if (!known)
        {
            return FireSafetyOutcome.Unknown;
        }
        return pawns.Any(FireSafetyEligible) ? FireSafetyOutcome.WaitingForNative : FireSafetyOutcome.Blocked;
    }

    private static bool FireSafetyEligible(FireSafetyPawnFacts p)
    {
        var dk = p.Dead.TryGetValue(out var dead);
        var wk = p.Downed.TryGetValue(out var downed);
        var tk = p.Drafted.TryGetValue(out var drafted);
        var mk = p.MentalState.TryGetValue(out var mental);
        var nk = p.NeedsTend.TryGetValue(out var needsTend);
        var bk = p.Bleeding.TryGetValue(out var bleeding);
        var hk = p.FirefightingEnabled.TryGetValue(out var firefighting);
        if (!dk || !wk || !tk || !mk || !nk || !bk || !hk)
        {
            return false;
        }
        return !dead && !downed && !drafted && !mental && !needsTend && !bleeding && firefighting;
    }
}
