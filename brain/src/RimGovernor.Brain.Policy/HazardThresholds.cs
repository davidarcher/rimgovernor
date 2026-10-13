namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    // Hazard thresholds Go authors into every clock WatchPolicy (and the hunt route margin into the colony facts
    // request and native rules). Native holds no literal for them: it validates presence and finiteness and
    // applies what it is sent. docs/developers/architecture/hazard-detection-bounds.md describes each.

    /// <summary>One blow of at least this much is a serious injury, above every common automatic round (charge rifle 15, heavy SMG 12, assault rifle 11) but under a pila (26) or sniper round (25).</summary>
    public const float SeriousSingleHitDamage = 20f;

    /// <summary>Summary health crossing under this is serious. MedicalRestSafety uses the same value: a resting patient at or under it needs a fresh medical review.</summary>
    public const float SeriousSummaryHealthFloor = 0.5f;

    /// <summary>A total bleed rate (fraction of blood per day) crossing over this is serious.</summary>
    public const float SeriousBleedRateFloor = 1.0f;

    /// <summary>A vital part hit to under this fraction of its health is serious whatever the damage was.</summary>
    public const float SeriousVitalPartFloor = 0.5f;

    /// <summary>An explosive landing within its blast radius plus this many cells of a colonist is launched near them.</summary>
    public const float ExplosiveNearMarginCells = 3f;

    /// <summary>The range assumed for a pawn without a ranged weapon.</summary>
    public const float MeleeReachCells = 1.5f;

    /// <summary>A new wound that bleeds the colonist out within this many game ticks (two in-game hours) keeps the stop.</summary>
    public const uint InjurySeverityFloorTicks = 5000;

    /// <summary>A hunt route passing within this many cells (Chebyshev) of a wild predator is unsafe.</summary>
    public const float HuntPredatorMarginCells = 25f;
}
