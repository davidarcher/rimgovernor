using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using StoreRounds = RimGovernor.Brain.Store.Rounds;
using StoreRoundsResult = RimGovernor.Brain.Store.RoundsResult;

namespace RimGovernor.Brain.Executor;

/// <summary>One enabled review's canonical reading, policy inputs and committed cursor, retained for replay.</summary>
public sealed class SnapshotRounds
{
    public string Recorded { get; set; } = "";
    public GenerationSnapshot Snapshot { get; set; }
    public Tick Tick { get; set; }
    public bool Immediate { get; set; }
    public required ColonyReading Reading { get; set; }
    public RoundsFacts Facts => Reading.Facts;
    public RoundsLatches Latches { get; set; } = new();
    public RoundsPolicy Policy { get; set; } = new();
    public StoreRounds? Review { get; set; }

    public RoundsFindings Detect() =>
        Immediate
            ? RoundsRules.InspectImmediateRounds(Facts, Latches, Policy)
            : RoundsRules.InspectRounds(Facts, Latches, Policy);

    /// <summary>Explicitly omits site cells when a recording's test does not replay a site search.</summary>
    public void TrimCells()
    {
        if (Reading.Window is { } window)
            Reading = Reading with { Window = window with { Value = window.Value with { Cells = [] } } };
    }
}

public static class SnapshotRoundsReplay
{
    /// <summary>The snapshot of an enabled review's result; null when the review detected nothing (disabled).</summary>
    public static SnapshotRounds? FromReview(GenerationSnapshot current, Tick tick, StoreRoundsResult result, ColonyReading reading)
    {
        if (result.Detection is not { } detection) return null;
        var review = result.Review;
        return new SnapshotRounds
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
