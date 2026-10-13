using System.Text;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

/// <summary>
/// The shrine planner's last step as it judged it, not the review's advisory <see cref="Rounds.ShrineHolds"/>:
/// <see cref="Reason"/> is the step's answer, <see cref="Shrine"/> and <see cref="Hold"/> the shrine it held on
/// (or acted on) and why, and <see cref="Skipped"/> every candidate it judged and passed over before or after
/// that one, with its own reason. <see cref="Tick"/> is the review tick the step planned under (Go
/// <c>RoundsShrineStep</c>).
/// </summary>
public sealed record RoundsShrineStep
{
    public Tick Tick { get; init; }

    public string Reason { get; init; } = "";

    public string Shrine { get; init; } = "";

    public string Hold { get; init; } = "";

    public PlanId Plan { get; init; }

    public EquatableArray<ShrineHold> Skipped { get; init; }

    /// <summary>Whether the step is well formed under the review at <paramref name="tick"/> (Go <c>validate</c>).</summary>
    public bool IsValid(Tick tick) =>
        Tick <= tick
        && Reason.Length != 0
        && Encoding.UTF8.GetByteCount(Reason) <= 64
        && Encoding.UTF8.GetByteCount(Hold) <= 64
        && Encoding.UTF8.GetByteCount(Shrine) <= 256
        && Skipped.Count <= 256;
}

public sealed partial class Store
{
    /// <summary>Marks a ShrineHolds row the shrine planner's last step held on (<c>ShrineHold.Planner</c>).</summary>
    public const string ShrinePlannerHeld = "held";

    /// <summary>Marks a ShrineHolds row the shrine planner's last step passed over (<c>ShrineHold.Planner</c>).</summary>
    public const string ShrinePlannerSkipped = "skipped";

    /// <summary>
    /// Files the shrine planner's step on the review loaded under <paramref name="revision"/> and marks its
    /// ShrineHolds rows: the shrine the step held on as held, every candidate it passed over as skipped. A review
    /// that has moved past <paramref name="revision"/> is <see cref="ConflictException"/>.
    /// </summary>
    public Rounds RecordShrineStep(ulong revision, RoundsShrineStep step, CancellationToken cancellationToken) =>
        Run(
            tx =>
            {
                var review = LoadRounds(tx);
                if (review.Revision != revision)
                {
                    throw new ConflictException($"rounds revision {review.Revision}, shrine step under {revision}");
                }
                DomainException.Require(step.IsValid(review.Tick), "invalid routine shrine step");
                review = review with { ShrineHolds = MarkShrineHolds(review.ShrineHolds, step), ShrineStep = step };
                PutSingleton(tx, "rounds", review, MaxRoundsPayload);
                return review;
            },
            cancellationToken
        );

    public Task<Rounds> RecordShrineStepAsync(ulong revision, RoundsShrineStep step, CancellationToken cancellationToken) =>
        Task.Run(() => RecordShrineStep(revision, step, cancellationToken), cancellationToken);

    /// <summary>
    /// Marks each shrine row of <paramref name="holds"/> with what <paramref name="step"/> did with that shrine; a
    /// null step clears the marks. Casket and occupant rows are left as they are.
    /// </summary>
    internal static EquatableArray<ShrineHold> MarkShrineHolds(EquatableArray<ShrineHold> holds, RoundsShrineStep? step)
    {
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        if (step is not null)
        {
            foreach (var hold in step.Skipped)
            {
                _ = skipped.Add(hold.Shrine);
            }
        }
        var marked = new List<ShrineHold>(holds.Count);
        foreach (var row in holds)
        {
            if (row.Casket.Length != 0 || row.Occupant.Length != 0)
            {
                marked.Add(row);
                continue;
            }
            var planner = "";
            if (step is not null && step.Hold.Length != 0 && string.Equals(row.Shrine, step.Shrine, StringComparison.Ordinal))
            {
                planner = ShrinePlannerHeld;
            }
            else if (skipped.Contains(row.Shrine))
            {
                planner = ShrinePlannerSkipped;
            }
            marked.Add(row with { Planner = planner });
        }
        return EquatableArray.From(marked);
    }
}
