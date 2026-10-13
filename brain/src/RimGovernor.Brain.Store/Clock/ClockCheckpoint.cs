using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store.Codec;

namespace RimGovernor.Brain.Store;

// Go internal/store/clock/checkpoint.go. The checkpoint replaces only validated history in one transaction.
// Public cursors and review revisions never reset when the active log is compacted.
public static partial class ClockJournal
{
    /// <summary>
    /// Go <c>HistoryTail</c>: the retained page/review count above which <see cref="CompactHistory"/> compacts.
    /// Settable for the same reason as <see cref="ReviewCapacity"/>: the compaction-boundary test shrinks it
    /// rather than paying for 128 real polls.
    /// </summary>
    public static int HistoryTail { get; set; } = 128;

    /// <summary>Go <c>loadClockCheckpoint</c>.</summary>
    internal static ClockHistoryCheckpoint LoadCheckpoint(StoreTransaction tx)
    {
        var (checkpoint, found) = tx.QueryFirst(
            "SELECT pages,cursor,lost_count,review_revision,review_reviewed,review_acknowledged,review_inbox_cursor FROM clock_history_checkpoint WHERE singleton=1",
            [],
            r => new ClockHistoryCheckpoint(r.GetInt64(0), r.GetInt64(1), r.UnsignedText(2), new ClockReviewHead(r.UnsignedText(3), r.GetInt64(4), r.GetInt64(5)), r.GetInt64(6))
        );
        if (!found)
        {
            throw new StoreCorruptException("missing clock history checkpoint");
        }
        var review = checkpoint.Review;
        if (checkpoint.Pages < 0
            || checkpoint.Cursor < checkpoint.Pages
            || checkpoint.Pages > long.MaxValue - InboxCapacity
            || (checkpoint.Pages == 0 && (checkpoint.Cursor != 0 || checkpoint.LostCount != 0))
            || checkpoint.LostCount > (ulong)checkpoint.Cursor
            || review.Acknowledged < 0
            || review.Reviewed < review.Acknowledged
            || review.Reviewed < checkpoint.Cursor
            || checkpoint.ReviewInboxCursor < review.Reviewed
            || (review.Revision == 0 && (review.Reviewed != 0 || review.Acknowledged != 0)))
        {
            throw new StoreCorruptException("invalid clock history checkpoint");
        }
        return checkpoint;
    }

    private static void SaveCheckpoint(StoreTransaction tx, ClockHistoryCheckpoint checkpoint) =>
        _ = tx.Execute(
            "UPDATE clock_history_checkpoint SET pages=$pages,cursor=$cursor,lost_count=$lost,review_revision=$revision,review_reviewed=$reviewed,review_acknowledged=$acknowledged,review_inbox_cursor=$inbox WHERE singleton=1",
            ("$pages", checkpoint.Pages),
            ("$cursor", checkpoint.Cursor),
            ("$lost", checkpoint.LostCount),
            ("$revision", checkpoint.Review.Revision),
            ("$reviewed", checkpoint.Review.Reviewed),
            ("$acknowledged", checkpoint.Review.Acknowledged),
            ("$inbox", checkpoint.ReviewInboxCursor)
        );

    /// <summary>
    /// Go <c>loadClockAcknowledgement</c>: an acknowledgement that compaction moved to the archive, answered with
    /// the state it had when it was made; null when this request id was never archived.
    /// </summary>
    private static ClockReviewState? LoadAcknowledgement(StoreTransaction tx, ClockAcknowledgement ack, ReviewReplay replay)
    {
        var (row, found) = tx.QueryFirst(
            "SELECT expected_revision,through_cursor,head_revision,head_reviewed,head_acknowledged,inbox_cursor FROM clock_acknowledgements WHERE request_id=$id",
            [("$id", ack.RequestId)],
            r => (Request: new ClockAcknowledgement(ack.RequestId, r.UnsignedText(0), r.GetInt64(1)), Head: new ClockReviewHead(r.UnsignedText(2), r.GetInt64(3), r.GetInt64(4)), InboxCursor: r.GetInt64(5))
        );
        if (!found)
        {
            return null;
        }
        var request = row.Request;
        var h = row.Head;
        var checkpoint = replay.Inbox.Checkpoint;
        if (!ValidSubmissionId(request.RequestId)
            || request.ThroughCursor < 0
            || h.Reviewed != request.ThroughCursor
            || h.Acknowledged != request.ThroughCursor
            || row.InboxCursor < h.Reviewed
            || row.InboxCursor > checkpoint.ReviewInboxCursor
            || h.Revision > checkpoint.Review.Revision
            || (h.Revision != request.ExpectedRevision && (request.ExpectedRevision == ulong.MaxValue || h.Revision != request.ExpectedRevision + 1)))
        {
            throw new StoreCorruptException("invalid archived clock acknowledgement");
        }
        if (request != ack)
        {
            throw new ConflictException();
        }
        // An acknowledgement covers every hold through its reviewed cursor. Preserve its historical captured cursor
        // instead of applying it to today's evidence.
        return new ClockReviewState(h.Revision, row.InboxCursor, h.Reviewed, h.Acknowledged, []);
    }

    /// <summary>
    /// Go <c>CompactHistory</c>: keeps a recent page tail and every unreviewed or unacknowledged interruption/gap.
    /// Acknowledgement replies move to an indexed archive; routine polling never loads that growing archive into
    /// memory.
    /// </summary>
    internal static ClockHistoryCompaction CompactHistory(StoreTransaction tx, string profile)
    {
        var path = CanonicalProfile(profile);
        var replay = LoadReview(tx, path);
        var cursor = replay.Inbox.State.Cursor;
        var none = new ClockHistoryCompaction(0, 0, 0, cursor);
        var size = tx.Scalar("SELECT coalesce(sum(length(request)+length(page)),0) FROM clock_event_pages");
        if (replay.Entries.Count < HistoryTail && replay.Inbox.Pages.Count < HistoryTail && replay.Inbox.State.EventCount < 2 * HistoryTail && size < InboxBytes / 2)
        {
            return none;
        }
        var checkpoint = replay.Inbox.Checkpoint;
        var pagesDropped = 0;
        var eventsDropped = 0;
        // Compact a contiguous prefix so missing native cursor positions retain their original loss attribution and
        // ingestion still checks an exact next cursor.
        var pages = replay.Inbox.Pages;
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i].Page;
            if (i >= pages.Count - 8 || page.NextCursor > replay.Head.Reviewed)
            {
                break;
            }
            if (page.NextCursor > replay.Head.Acknowledged && (page.Gap || page.Events.Any(e => PageInterrupts(e, page))))
            {
                break;
            }
            checkpoint = checkpoint with { Pages = checkpoint.Pages + 1, Cursor = page.NextCursor, LostCount = checkpoint.LostCount + page.LostCount };
            pagesDropped++;
            eventsDropped += page.Events.Count;
        }
        if (pagesDropped == 0 && replay.Entries.Count == 0)
        {
            return none;
        }
        for (var i = 0; i < replay.Entries.Count; i++)
        {
            var entry = replay.Entries[i];
            if (entry.Kind != ClockReviewKind.Ack)
            {
                continue;
            }
            var head = replay.Heads[i];
            _ = tx.Execute(
                "INSERT INTO clock_acknowledgements(request_id,expected_revision,through_cursor,head_revision,head_reviewed,head_acknowledged,inbox_cursor) VALUES($id,$expected,$through,$revision,$reviewed,$acknowledged,$inbox)",
                ("$id", entry.RequestId),
                ("$expected", entry.ExpectedRevision),
                ("$through", entry.ThroughCursor),
                ("$revision", head.Revision),
                ("$reviewed", head.Reviewed),
                ("$acknowledged", head.Acknowledged),
                ("$inbox", entry.InboxCursor)
            );
        }
        checkpoint = checkpoint with { Review = replay.Head, ReviewInboxCursor = cursor };
        SaveCheckpoint(tx, checkpoint);
        _ = tx.Execute("DELETE FROM clock_inbox_events WHERE page_sequence<=$pages", ("$pages", checkpoint.Pages));
        _ = tx.Execute("DELETE FROM clock_event_pages WHERE sequence<=$pages", ("$pages", checkpoint.Pages));
        _ = tx.Execute("DELETE FROM clock_review_log");
        _ = LoadReview(tx, path);
        return new ClockHistoryCompaction(pagesDropped, eventsDropped, replay.Entries.Count, cursor);
    }

    /// <summary>
    /// Go <c>AdoptBacklog</c>: starts a journal that has never read the profile at <paramref name="cursor"/>. Native
    /// rows through it are history this journal records as compacted, reviewed and acknowledged, so a fresh journal
    /// against a long-lived native profile reads one page instead of replaying every earlier session. Returns
    /// false, changing nothing, once the journal holds any history.
    /// </summary>
    internal static bool AdoptBacklog(StoreTransaction tx, string profile, long cursor)
    {
        var path = CanonicalProfile(profile);
        if (cursor <= 0)
        {
            return false;
        }
        var replay = LoadReview(tx, path);
        if (replay.Inbox.Checkpoint != default || replay.Inbox.Pages.Count != 0 || replay.Entries.Count != 0 || replay.Head != default)
        {
            return false;
        }
        if (tx.Scalar("SELECT count(*) FROM clock_acknowledgements") != 0)
        {
            return false;
        }
        var checkpoint = new ClockHistoryCheckpoint(1, cursor, 0, new ClockReviewHead(1, cursor, cursor), cursor);
        SaveCheckpoint(tx, checkpoint);
        _ = tx.Execute(
            "UPDATE clock_review SET revision=$revision,reviewed=$reviewed,acknowledged=$acknowledged WHERE singleton=1",
            ("$revision", checkpoint.Review.Revision),
            ("$reviewed", checkpoint.Review.Reviewed),
            ("$acknowledged", checkpoint.Review.Acknowledged)
        );
        _ = LoadReview(tx, path);
        return true;
    }
}
