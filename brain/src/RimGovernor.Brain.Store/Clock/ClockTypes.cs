using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using Pb = RimGovernor.Contracts.Wire.Clock;
using PbCommon = RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Store;

// Go internal/store/clock/types.go and the store-level alias files clock_types.go, clock_epoch_types.go,
// clock_retention_types.go, clock_review_types.go and clock_window_types.go. The Go `clock` package was a
// separate package only to split build caching, so its exported types live in this namespace under the
// names `internal/store` re-exported them by (ClockIntent, ClockAttempt, ...).

/// <summary>
/// Go <c>clock.ErrRetired</c> (re-exported as <c>store.ErrRetired</c>): the request was allocated but its row has
/// since been retired, so it can never be dispatched again.
/// </summary>
public sealed class ClockRetiredException() : DomainException("clock request retired");

/// <summary>Where a clock attempt stands (Go <c>Phase</c>).</summary>
public enum ClockPhase
{
    /// <summary>Not a phase: the empty value Go rejected as an invalid phase.</summary>
    [WireName("")]
    None = 0,
    Prepared,
    Dispatched,
    Uncertain,
    Applied,
    Refused,
}

/// <summary>
/// Go <c>Intent</c>: the immutable, lease-free intent of one clock command plus, for a finite start, the reviewed
/// profile evidence it was admitted against.
/// </summary>
public sealed record ClockIntent(string RequestId, string Key, GenerationSnapshot Snapshot, ClockCommand Command, ClockWindowAdmission? Window = null);

/// <summary>
/// Go <c>WindowAdmission</c>: binds a finite start to the reviewed profile evidence. It grants no authority and is
/// rechecked atomically at durable dispatch.
/// </summary>
public sealed record ClockWindowAdmission(string Profile, GenerationSnapshot Snapshot, Tick Tick, ulong ReviewRevision, long CapturedCursor, uint MaxTicks);

/// <summary>Go <c>Attempt</c>.</summary>
/// <param name="Intent">The command and its admission evidence.</param>
/// <param name="NativeAttempt">The native attempt key this command was issued under.</param>
/// <param name="Phase">The journal phase.</param>
/// <param name="Reply">The control reply, null until the call has an outcome.</param>
/// <param name="SupersededAt">Retires only the original world scope; <paramref name="Phase"/> retains uncertainty.</param>
public sealed record ClockAttempt(ClockIntent Intent, PbCommon.AttemptKey? NativeAttempt, ClockPhase Phase, Pb.ControlReply? Reply = null, PbCommon.ObservationContext? SupersededAt = null);

/// <summary>Go <c>EpochStage</c>.</summary>
public enum ClockEpochStage
{
    [WireName("")]
    None = 0,
    Required,
    Pausing,
    Uncertain,
    Paused,
    Retired,
    Superseded,
}

/// <summary>
/// Go <c>EpochObligation</c>: derives ownership from an immutable applied Start receipt. <see cref="Sequence"/> is
/// a local pause-dispatch fence, not a native attempt or a live lease.
/// </summary>
public sealed record ClockEpochObligation(
    string StartRequestId,
    Pb.Epoch? Epoch,
    ClockEpochStage Stage,
    ulong Sequence,
    PbCommon.ObservationContext? Context,
    Pb.Status? Status
);

/// <summary>Go <c>SequenceState</c>: binds allocation and retirement to this journal namespace.</summary>
public sealed record ClockSequenceState(ControllerSessionId Namespace, ulong LastAllocated, ulong RetiredThrough)
{
    /// <summary>Go <c>NextRequestID</c>.</summary>
    public string NextRequestId()
    {
        DomainException.Require(RetiredThrough <= LastAllocated && LastAllocated != ulong.MaxValue, "invalid or exhausted clock sequence");
        return ClockJournal.ClockRequestId(Namespace, LastAllocated + 1);
    }
}

/// <summary>Go <c>Retirement</c>.</summary>
public sealed record ClockRetirement(ClockSequenceState State, int RemovedAttempts, int RemovedEpochs, int RetainedAttempts);

/// <summary>Go <c>HoldKind</c>.</summary>
public enum ClockHoldKind
{
    [WireName("")]
    None = 0,
    Interruption,
    Gap,
}

/// <summary>
/// Go <c>Hold</c>: refers only to immutable captured cursor positions. It carries no grant and cannot suppress a
/// current unsafe native observation.
/// </summary>
public sealed record ClockHold(ClockHoldKind Kind, long FromCursor, long ThroughCursor);

/// <summary>Go <c>ReviewState</c>.</summary>
public sealed record ClockReviewState(ulong Revision, long InboxCursor, long ReviewedCursor, long AcknowledgedCursor, IReadOnlyList<ClockHold> Holds);

/// <summary>Go <c>Acknowledgement</c>.</summary>
public sealed record ClockAcknowledgement(string RequestId, ulong ExpectedRevision, long ThroughCursor);

/// <summary>Go <c>InboxState</c>.</summary>
public sealed record ClockInboxState(string Profile, long Cursor, bool Gap, ulong LostCount, int EventCount, int PageCount);

/// <summary>Go <c>EventPage</c>: one retained native journal page with the request that fetched it.</summary>
public sealed record ClockEventPage(Pb.EventsRequest Request, Pb.EventsPage Page);

/// <summary>Go <c>Inbox</c>: every retained page.</summary>
public sealed record ClockInbox(ClockInboxState State, IReadOnlyList<ClockEventPage> Pages)
{
    internal ClockHistoryCheckpoint Checkpoint { get; init; }
}

/// <summary>Go <c>HistoryCompaction</c>.</summary>
public sealed record ClockHistoryCompaction(int RemovedPages, int RemovedEvents, int RemovedReviews, long Cursor);

/// <summary>Whether a review log entry marks events reviewed or acknowledged.</summary>
internal enum ClockReviewKind
{
    [WireName("")]
    None = 0,
    Review,
    Ack,
}

/// <summary>Go <c>clockReviewHead</c>.</summary>
internal readonly record struct ClockReviewHead(ulong Revision, long Reviewed, long Acknowledged);

/// <summary>Go <c>clockReviewEntry</c> (exported as <c>ReviewEntry</c>).</summary>
internal sealed record ClockReviewEntry(ClockReviewKind Kind, long InboxCursor, ulong ExpectedRevision, long ThroughCursor, string RequestId);

/// <summary>
/// Go <c>clockHistoryCheckpoint</c>: replaces only validated history in one transaction. Public cursors and review
/// revisions never reset when the active log is compacted.
/// </summary>
internal readonly record struct ClockHistoryCheckpoint(long Pages, long Cursor, ulong LostCount, ClockReviewHead Review, long ReviewInboxCursor);

/// <summary>Go <c>clockSequenceHead</c>.</summary>
internal sealed class ClockSequenceHead
{
    public ulong LastAllocated { get; set; }

    public ulong RetiredThrough { get; set; }

    public List<ulong> Retained { get; set; } = [];
}
