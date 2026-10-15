using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using Pb = RimGovernor.Contracts.Wire.Clock;
using PbCommon = RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Store;

// Go internal/store/clock/types.go and the store-level alias files clock_types.go, clock_epoch_types.go,
// clock_retention_types.go, clock_review_types.go and clock_window_types.go. The Go `clock` package was a
// separate package only to split build caching, so its exported types live in this namespace under the
// names `internal/store` re-exported them by (ClockIntent, ClockAttempt, ...).

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
