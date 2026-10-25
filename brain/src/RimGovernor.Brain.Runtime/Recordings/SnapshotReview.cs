using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using StoreReview = RimGovernor.Brain.Store.ReviewState;
using StoreReviewResult = RimGovernor.Brain.Store.ReviewResult;

namespace RimGovernor.Brain.Runtime.Recordings;

/// <summary>One enabled review's canonical reading, policy inputs and committed cursor, retained for replay.</summary>
public sealed class SnapshotReview
{
    public string Recorded { get; set; } = "";
    public GenerationSnapshot Snapshot { get; set; }
    public Tick Tick { get; set; }
    public bool Immediate { get; set; }
    public required ColonyReading Reading { get; set; }
    public ReviewFacts Facts => Reading.Facts;
    public Latches Latches { get; set; } = new();
    public PlannerSettings Policy { get; set; } = new();
    public StoreReview? Review { get; set; }

    public Findings Detect() =>
        Immediate
            ? Rules.InspectImmediateReview(Facts, Latches, Policy)
            : Rules.InspectReview(Facts, Latches, Policy);

    /// <summary>Explicitly omits site cells when a recording's test does not replay a site search.</summary>
    public void TrimCells()
    {
        if (Reading.Window is { } window)
            Reading = Reading with { Window = window with { Value = window.Value with { Cells = [] } } };
    }
}

public static class SnapshotReviewReplay
{
    /// <summary>The snapshot of an enabled review's result; null when the review detected nothing (disabled).</summary>
    public static SnapshotReview? FromReview(GenerationSnapshot current, Tick tick, StoreReviewResult result, ColonyReading reading)
    {
        if (result.Detection is not { } detection) return null;
        var review = result.Review;
        return new SnapshotReview
        {
            Reading = reading with { Facts = detection.Facts },
            Recorded = SnapshotRecorder.Provenance(current, tick),
            Snapshot = current,
            Tick = tick,
            Immediate = review.Immediate,
            Latches = detection.Latches,
            Policy = detection.Policy,
            Review = review,
        };
    }
}
