using Pb = RimGovernor.Contracts.Wire.Clock;

namespace RimGovernor.Brain.Store;

// Go internal/store/clock_methods.go: the (s *Store) wrappers around the clock journal. Each runs one transaction;
// the logic lives in ClockJournal.
public sealed partial class Store
{
    /// <summary>
    /// Go <c>AdoptClockBacklog</c>: starts a journal that never read the profile at <paramref name="cursor"/>
    /// (see <see cref="ClockJournal"/>); false once it holds any history.
    /// </summary>
    public bool AdoptClockBacklog(string profile, long cursor, CancellationToken cancellationToken) => Run(tx => ClockJournal.AdoptBacklog(tx, profile, cursor), cancellationToken);

    /// <summary><see cref="AdoptClockBacklog"/> as a task.</summary>
    public Task<bool> AdoptClockBacklogAsync(string profile, long cursor, CancellationToken cancellationToken) =>
        Task.Run(() => AdoptClockBacklog(profile, cursor, cancellationToken), cancellationToken);

    /// <summary>
    /// Go <c>CompactClockHistory</c>: keeps a recent page tail and every unreviewed or unacknowledged
    /// interruption/gap. Acknowledgement replies move to an indexed archive; routine polling never loads that
    /// growing archive into memory.
    /// </summary>
    public ClockHistoryCompaction CompactClockHistory(string profile, CancellationToken cancellationToken) => Run(tx => ClockJournal.CompactHistory(tx, profile), cancellationToken);

    /// <summary><see cref="CompactClockHistory"/> as a task.</summary>
    public Task<ClockHistoryCompaction> CompactClockHistoryAsync(string profile, CancellationToken cancellationToken) =>
        Task.Run(() => CompactClockHistory(profile, cancellationToken), cancellationToken);

    /// <summary>Go <c>BindClockInbox</c>: binds one native profile journal, independent of loaded worlds.</summary>
    public ClockInboxState BindClockInbox(string profile, CancellationToken cancellationToken)
    {
        var path = ClockJournal.CanonicalProfile(profile);
        return Run(tx => ClockJournal.BindInbox(tx, path), cancellationToken);
    }

    /// <summary><see cref="BindClockInbox"/> as a task.</summary>
    public Task<ClockInboxState> BindClockInboxAsync(string profile, CancellationToken cancellationToken) => Task.Run(() => BindClockInbox(profile, cancellationToken), cancellationToken);

    /// <summary>Go <c>ReadClockInbox</c>.</summary>
    public ClockInboxState ReadClockInbox(string profile, CancellationToken cancellationToken) => LoadClockInbox(profile, ClockJournal.InboxCapacity, cancellationToken).State;

    /// <summary><see cref="ReadClockInbox"/> as a task.</summary>
    public Task<ClockInboxState> ReadClockInboxAsync(string profile, CancellationToken cancellationToken) => Task.Run(() => ReadClockInbox(profile, cancellationToken), cancellationToken);

    /// <summary>
    /// Go <c>LoadClockInbox</c>: returns every retained page or fails; <paramref name="limit"/> never truncates.
    /// Ingestion is not processing or acknowledgement of an interruption.
    /// </summary>
    public ClockInbox LoadClockInbox(string profile, int limit, CancellationToken cancellationToken)
    {
        var path = ClockJournal.CanonicalProfile(profile);
        return Run(tx => ClockJournal.LoadInbox(tx, path, limit), cancellationToken);
    }

    /// <summary><see cref="LoadClockInbox"/> as a task.</summary>
    public Task<ClockInbox> LoadClockInboxAsync(string profile, int limit, CancellationToken cancellationToken) =>
        Task.Run(() => LoadClockInbox(profile, limit, cancellationToken), cancellationToken);

    /// <summary>
    /// Go <c>AppendClockEvents</c>: commits a scanned page, surviving events and cursor together. Only exact
    /// retained request/page replay is a no-op (<c>Created</c> false). A changed stale page must be refetched at
    /// the current cursor; its new context does not rewrite history.
    /// </summary>
    public (ClockInboxState State, bool Created) AppendClockEvents(string profile, Pb.EventsRequest request, Pb.EventsPage page, CancellationToken cancellationToken)
    {
        var path = ClockJournal.CanonicalProfile(profile);
        return Run(tx => ClockJournal.AppendEvents(tx, path, request, page), cancellationToken);
    }

    /// <summary><see cref="AppendClockEvents"/> as a task.</summary>
    public Task<(ClockInboxState State, bool Created)> AppendClockEventsAsync(string profile, Pb.EventsRequest request, Pb.EventsPage page, CancellationToken cancellationToken) =>
        Task.Run(() => AppendClockEvents(profile, request, page, cancellationToken), cancellationToken);

    /// <summary>Go <c>ReadClockReview</c>.</summary>
    public ClockReviewState ReadClockReview(string profile, CancellationToken cancellationToken) => Run(tx => ClockJournal.ReadReview(tx, profile), cancellationToken);

    /// <summary><see cref="ReadClockReview"/> as a task.</summary>
    public Task<ClockReviewState> ReadClockReviewAsync(string profile, CancellationToken cancellationToken) => Task.Run(() => ReadClockReview(profile, cancellationToken), cancellationToken);

    /// <summary>Go <c>ReviewClockEvents</c>.</summary>
    public ClockReviewState ReviewClockEvents(string profile, ulong expectedRevision, CancellationToken cancellationToken) =>
        Run(tx => ClockJournal.ReviewEvents(tx, profile, expectedRevision), cancellationToken);

    /// <summary><see cref="ReviewClockEvents"/> as a task.</summary>
    public Task<ClockReviewState> ReviewClockEventsAsync(string profile, ulong expectedRevision, CancellationToken cancellationToken) =>
        Task.Run(() => ReviewClockEvents(profile, expectedRevision, cancellationToken), cancellationToken);

    /// <summary>Go <c>AcknowledgeClockEvents</c>.</summary>
    public ClockReviewState AcknowledgeClockEvents(string profile, ClockAcknowledgement ack, CancellationToken cancellationToken) =>
        Run(tx => ClockJournal.AcknowledgeEvents(tx, profile, ack), cancellationToken);

    /// <summary><see cref="AcknowledgeClockEvents"/> as a task.</summary>
    public Task<ClockReviewState> AcknowledgeClockEventsAsync(string profile, ClockAcknowledgement ack, CancellationToken cancellationToken) =>
        Task.Run(() => AcknowledgeClockEvents(profile, ack, cancellationToken), cancellationToken);
}
