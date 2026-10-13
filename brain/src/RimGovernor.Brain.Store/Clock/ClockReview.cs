using System.Collections.Frozen;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store.Codec;
using Pb = RimGovernor.Protocol.Clock;

namespace RimGovernor.Brain.Store;

// Go internal/store/clock/review.go.
public static partial class ClockJournal
{
    /// <summary>
    /// Go <c>InformationalLetterDefs</c>: the letter definitions the native supervisor itself never stops play for
    /// (an announcement, not a threat, a death or a decision). The game may still pause on one under the player's
    /// automatic-pause preference; that pause stops the window like any other, but it is the game's own, not a
    /// reason to hand control back. Every other letter class still holds.
    /// </summary>
    public static FrozenSet<string> InformationalLetterDefs { get; } = new[] { "NeutralEvent", "PositiveEvent", "NegativeEvent" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Go <c>ReviewCapacity</c>: the review log's bound. Settable so capacity-boundary tests can shrink it rather
    /// than fill a production-scale log.
    /// </summary>
    public static int ReviewCapacity { get; set; } = 4096;

    /// <summary>The state a review read replays from the checkpoint, the review log and the inbox (Go <c>clockReviewReplay</c>).</summary>
    internal sealed class ReviewReplay(ClockReviewHead head, ClockInbox inbox)
    {
        public ClockReviewHead Head { get; set; } = head;

        public List<ClockReviewEntry> Entries { get; } = [];

        public List<ClockReviewHead> Heads { get; } = [];

        public ClockInbox Inbox { get; } = inbox;
    }

    /// <summary>Go <c>clockReviewBoundary</c>: a cursor the inbox can be reviewed up to.</summary>
    private static bool ReviewBoundary(ClockInbox inbox, long cursor) => cursor == inbox.Checkpoint.Cursor || inbox.Pages.Any(p => p.Page.NextCursor == cursor);

    /// <summary>
    /// Go <c>BenignStop</c>: whether a stop reason is the controller's own doing (a budget, a requested pause or a
    /// latched watch, or an armed combat stop owned by an admitted fight plan) rather than an interruption that
    /// needs review before time may resume.
    /// </summary>
    public static bool BenignStop(Pb.StopReason reason) =>
        reason is Pb.StopReason.TickBudget or Pb.StopReason.RequestedPause or Pb.StopReason.WatchLatched or Pb.StopReason.CombatEvent;

    /// <summary>Go <c>BenignStopEvent</c>: <see cref="BenignStop"/> with the stop's evidence; a letter pause for an informational letter is benign too.</summary>
    public static bool BenignStopEvent(Pb.StopEvent? stop)
    {
        var reason = stop?.Reason ?? Pb.StopReason.Unspecified;
        if (BenignStop(reason))
        {
            return true;
        }
        return reason == Pb.StopReason.LetterPause && InformationalLetterDefs.Contains(stop?.Pause?.Letter?.DefName ?? "");
    }

    /// <summary>
    /// Go <c>EventInterrupts</c>: classifies one journal event. Operation outcomes, authority changes, native rule
    /// events, observation invalidations, game alerts, injury observations, notifications and force-pause waits
    /// are typed facts the controller reacts to, not holds: the native supervisor never stops play for them, and
    /// anything that does stop arrives as its own stop event. Shared with the poll loop so both classifications
    /// cannot drift.
    /// </summary>
    public static bool EventInterrupts(Pb.Event @event) =>
        @event.EventCase switch
        {
            Pb.Event.EventOneofCase.Started
            or Pb.Event.EventOneofCase.SpeedChanged
            or Pb.Event.EventOneofCase.HostilesCleared
            or Pb.Event.EventOneofCase.ForcePauseCleared
            or Pb.Event.EventOneofCase.ForcePauseWaiting
            or Pb.Event.EventOneofCase.OperationOutcome
            or Pb.Event.EventOneofCase.AuthorityChanged
            or Pb.Event.EventOneofCase.RuleFired
            or Pb.Event.EventOneofCase.RuleLeaseExpired
            or Pb.Event.EventOneofCase.ObservationInvalidated
            or Pb.Event.EventOneofCase.Alert
            or Pb.Event.EventOneofCase.InjuryObserved
            or Pb.Event.EventOneofCase.Notification => false,
            Pb.Event.EventOneofCase.Stopped => !BenignStopEvent(@event.Stopped),
            _ => true,
        };

    /// <summary>
    /// Go <c>PageInterrupts</c>: <see cref="EventInterrupts"/> for an event read on a page. A stop taken in another
    /// world than the page's (a kept game's previous load) interrupts nothing in the world read now.
    /// </summary>
    public static bool PageInterrupts(Pb.Event @event, Pb.EventsPage page) => EventInterrupts(@event) && !OtherWorld(@event, page);

    /// <summary>Go <c>OtherWorld</c>: whether the event was observed in another world (colony, load or map) than the page's current context.</summary>
    public static bool OtherWorld(Pb.Event @event, Pb.EventsPage page)
    {
        var a = @event.Context?.Identity;
        var b = page.Context?.Identity;
        if (a is null || b is null)
        {
            return false;
        }
        return !string.Equals(a.ColonyId, b.ColonyId, StringComparison.Ordinal) || !string.Equals(a.LoadToken, b.LoadToken, StringComparison.Ordinal) || a.MapId != b.MapId;
    }

    /// <summary>Go <c>clockReviewState</c>.</summary>
    internal static ClockReviewState ReviewStateOf(ClockReviewHead head, ClockInbox inbox, long cursor)
    {
        var holds = new List<ClockHold>();
        foreach (var p in inbox.Pages)
        {
            if (p.Page.NextCursor > head.Reviewed)
            {
                break;
            }
            if (p.Page.NextCursor <= head.Acknowledged)
            {
                continue;
            }
            if (p.Page.Gap)
            {
                holds.Add(new ClockHold(ClockHoldKind.Gap, p.Request.AfterCursor + 1, p.Page.NextCursor));
            }
            foreach (var @event in p.Page.Events)
            {
                if (@event.Cursor > head.Acknowledged && PageInterrupts(@event, p.Page))
                {
                    holds.Add(new ClockHold(ClockHoldKind.Interruption, @event.Cursor, @event.Cursor));
                }
            }
        }
        return new ClockReviewState(head.Revision, cursor, head.Reviewed, head.Acknowledged, holds);
    }

    /// <summary>Go <c>submissionID(id) == nil</c>.</summary>
    private static bool ValidSubmissionId(string id)
    {
        try
        {
            Submissions.ValidateId(id);
            return true;
        }
        catch (DomainException)
        {
            return false;
        }
    }

    /// <summary>Go <c>clockReviewApply</c>: the head after one log entry, or an error for an entry that does not follow from it.</summary>
    private static ClockReviewHead ApplyReview(ClockReviewHead head, ClockReviewEntry entry, ClockInbox inbox)
    {
        DomainException.Require(
            entry.ExpectedRevision == head.Revision && ReviewBoundary(inbox, entry.InboxCursor) && entry.InboxCursor >= head.Reviewed,
            "invalid clock review revision or captured cursor"
        );
        var next = head;
        switch (entry.Kind)
        {
            case ClockReviewKind.Review:
                DomainException.Require(entry.RequestId.Length == 0 && entry.ThroughCursor == entry.InboxCursor && entry.ThroughCursor > head.Reviewed, "invalid clock review provenance");
                next = next with { Reviewed = entry.ThroughCursor };
                break;
            case ClockReviewKind.Ack:
                DomainException.Require(ValidSubmissionId(entry.RequestId) && entry.ThroughCursor == head.Reviewed, "invalid clock acknowledgement provenance");
                next = next with { Acknowledged = entry.ThroughCursor };
                break;
            default:
                throw new DomainException("unknown clock review operation");
        }
        if (next != head)
        {
            if (head.Revision == ulong.MaxValue)
            {
                throw new CapacityException();
            }
            next = next with { Revision = next.Revision + 1 };
        }
        return next;
    }

    /// <summary>
    /// Go <c>loadClockReview</c>: replays the review log over the checkpoint's head and requires the result to be
    /// the saved head.
    /// </summary>
    internal static ReviewReplay LoadReview(StoreTransaction tx, string profile)
    {
        var inbox = ReadInbox(tx, profile, InboxCapacity).Inbox;
        var replay = new ReviewReplay(inbox.Checkpoint.Review, inbox);
        if (!ReviewBoundary(inbox, replay.Head.Reviewed) || !ReviewBoundary(inbox, inbox.Checkpoint.ReviewInboxCursor))
        {
            throw new StoreCorruptException("clock checkpoint lacks inbox boundary");
        }
        var heads = tx.Query("SELECT revision,reviewed,acknowledged FROM clock_review", [], r => new ClockReviewHead(r.UnsignedText(0), r.GetInt64(1), r.GetInt64(2)));
        if (heads.Count != 1)
        {
            throw new StoreCorruptException("missing clock review head");
        }
        var saved = heads[0];
        var count = tx.Scalar("SELECT count(*) FROM clock_review_log");
        if (count > ReviewCapacity)
        {
            throw new CapacityException();
        }
        var rows = tx.Query(
            "SELECT sequence,kind,inbox_cursor,expected_revision,through_cursor,request_id FROM clock_review_log ORDER BY sequence",
            [],
            r => (Sequence: r.GetInt64(0), Entry: ReadReviewEntry(r))
        );
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var previousInbox = inbox.Checkpoint.ReviewInboxCursor;
        foreach (var (sequence, entry) in rows)
        {
            if (sequence != replay.Entries.Count + 1 || entry.InboxCursor < previousInbox)
            {
                throw new StoreCorruptException("clock review log sequence or inbox regression");
            }
            if (entry.Kind == ClockReviewKind.Ack && !seen.Add(entry.RequestId))
            {
                throw new StoreCorruptException("duplicate clock acknowledgement");
            }
            replay.Head = ApplyReview(replay.Head, entry, inbox);
            replay.Entries.Add(entry);
            replay.Heads.Add(replay.Head);
            previousInbox = entry.InboxCursor;
        }
        if (replay.Head != saved || replay.Entries.Count != count)
        {
            throw new StoreCorruptException("clock review head lacks matching provenance");
        }
        return replay;
    }

    private static ClockReviewEntry ReadReviewEntry(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        var kind = r.GetString(1);
        if (!Wire.TryParse<ClockReviewKind>(kind, out var parsed) || parsed == ClockReviewKind.None)
        {
            throw new StoreCorruptException("unknown clock review operation");
        }
        return new ClockReviewEntry(parsed, r.GetInt64(2), r.UnsignedText(3), r.GetInt64(4), r.GetString(5));
    }

    /// <summary>Go <c>ReadReview</c>.</summary>
    internal static ClockReviewState ReadReview(StoreTransaction tx, string profile)
    {
        var replay = LoadReview(tx, CanonicalProfile(profile));
        return ReviewStateOf(replay.Head, replay.Inbox, replay.Inbox.State.Cursor);
    }

    /// <summary>Go <c>saveClockReview</c>: appends one entry to the log and moves the head, in the caller's transaction.</summary>
    private static ClockReviewState SaveReview(StoreTransaction tx, ReviewReplay replay, ClockReviewEntry entry)
    {
        if (replay.Entries.Count >= ReviewCapacity)
        {
            throw new CapacityException();
        }
        var next = ApplyReview(replay.Head, entry, replay.Inbox);
        _ = tx.Execute(
            "INSERT INTO clock_review_log(sequence,kind,inbox_cursor,expected_revision,through_cursor,request_id) VALUES($sequence,$kind,$inbox,$expected,$through,$request)",
            ("$sequence", replay.Entries.Count + 1),
            ("$kind", Wire.Name(entry.Kind)),
            ("$inbox", entry.InboxCursor),
            ("$expected", entry.ExpectedRevision),
            ("$through", entry.ThroughCursor),
            ("$request", entry.RequestId)
        );
        _ = tx.Execute(
            "UPDATE clock_review SET revision=$revision,reviewed=$reviewed,acknowledged=$acknowledged WHERE singleton=1",
            ("$revision", next.Revision),
            ("$reviewed", next.Reviewed),
            ("$acknowledged", next.Acknowledged)
        );
        return ReviewStateOf(next, replay.Inbox, entry.InboxCursor);
    }

    /// <summary>Go <c>ReviewEvents</c>: marks everything ingested so far as reviewed, if the revision is still current.</summary>
    internal static ClockReviewState ReviewEvents(StoreTransaction tx, string profile, ulong expectedRevision)
    {
        var replay = LoadReview(tx, CanonicalProfile(profile));
        if (replay.Head.Revision != expectedRevision)
        {
            throw new ConflictException();
        }
        var cursor = replay.Inbox.State.Cursor;
        if (replay.Head.Reviewed == cursor)
        {
            return ReviewStateOf(replay.Head, replay.Inbox, cursor);
        }
        return SaveReview(tx, replay, new ClockReviewEntry(ClockReviewKind.Review, cursor, expectedRevision, cursor, ""));
    }

    /// <summary>Go <c>AcknowledgeEvents</c>: an idempotent acknowledgement of the reviewed cursor, keyed by its request id.</summary>
    internal static ClockReviewState AcknowledgeEvents(StoreTransaction tx, string profile, ClockAcknowledgement ack)
    {
        Submissions.ValidateId(ack.RequestId);
        var replay = LoadReview(tx, CanonicalProfile(profile));
        if (LoadAcknowledgement(tx, ack, replay) is { } archived)
        {
            return archived;
        }
        for (var i = 0; i < replay.Entries.Count; i++)
        {
            var entry = replay.Entries[i];
            if (entry.Kind == ClockReviewKind.Ack && string.Equals(entry.RequestId, ack.RequestId, StringComparison.Ordinal))
            {
                if (entry.ExpectedRevision != ack.ExpectedRevision || entry.ThroughCursor != ack.ThroughCursor)
                {
                    throw new ConflictException();
                }
                return ReviewStateOf(replay.Heads[i], replay.Inbox, entry.InboxCursor);
            }
        }
        if (ack.ExpectedRevision != replay.Head.Revision || ack.ThroughCursor != replay.Head.Reviewed)
        {
            throw new ConflictException();
        }
        return SaveReview(tx, replay, new ClockReviewEntry(ClockReviewKind.Ack, replay.Inbox.State.Cursor, ack.ExpectedRevision, ack.ThroughCursor, ack.RequestId));
    }
}
