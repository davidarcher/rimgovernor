using Pb = RimGovernor.Protocol.Clock;
using PbCommon = RimGovernor.Protocol.Common;

namespace RimGovernor.Brain.Store;

// Go internal/store/clock_methods.go: the (s *Store) wrappers around the clock journal. Each runs one transaction;
// the logic lives in ClockJournal.
public sealed partial class Store
{
    /// <summary>
    /// Go <c>PrepareClock</c>: <c>Created</c> is false only for exact durable intent replay. It records no live
    /// lease and grants no permission to call native control.
    /// </summary>
    public (ClockAttempt Attempt, bool Created) PrepareClock(ClockIntent intent, CancellationToken cancellationToken) =>
        Run(tx => ClockJournal.Prepare(tx, intent), cancellationToken);

    /// <summary><see cref="PrepareClock"/> as a task.</summary>
    public Task<(ClockAttempt Attempt, bool Created)> PrepareClockAsync(ClockIntent intent, CancellationToken cancellationToken) =>
        Task.Run(() => PrepareClock(intent, cancellationToken), cancellationToken);

    /// <summary>Go <c>LookupClockAttempt</c>.</summary>
    public ClockAttempt LookupClockAttempt(string id, CancellationToken cancellationToken) => Run(tx => ClockJournal.LookupAttempt(tx, id), cancellationToken);

    /// <summary><see cref="LookupClockAttempt"/> as a task.</summary>
    public Task<ClockAttempt> LookupClockAttemptAsync(string id, CancellationToken cancellationToken) =>
        Task.Run(() => LookupClockAttempt(id, cancellationToken), cancellationToken);

    /// <summary>Go <c>LoadClockAttempts</c>.</summary>
    public IReadOnlyList<ClockAttempt> LoadClockAttempts(int limit, CancellationToken cancellationToken) => Run(tx => ClockJournal.LoadAttempts(tx, limit), cancellationToken);

    /// <summary><see cref="LoadClockAttempts"/> as a task.</summary>
    public Task<IReadOnlyList<ClockAttempt>> LoadClockAttemptsAsync(int limit, CancellationToken cancellationToken) =>
        Task.Run(() => LoadClockAttempts(limit, cancellationToken), cancellationToken);

    /// <summary>Go <c>DispatchClock</c>: prepared to dispatched.</summary>
    public ClockAttempt DispatchClock(string id, CancellationToken cancellationToken) => Run(tx => ClockJournal.Dispatch(tx, id), cancellationToken);

    /// <summary><see cref="DispatchClock"/> as a task.</summary>
    public Task<ClockAttempt> DispatchClockAsync(string id, CancellationToken cancellationToken) => Task.Run(() => DispatchClock(id, cancellationToken), cancellationToken);

    /// <summary>Go <c>MarkClockUncertain</c>.</summary>
    public ClockAttempt MarkClockUncertain(string id, CancellationToken cancellationToken) => Run(tx => ClockJournal.MarkUncertain(tx, id), cancellationToken);

    /// <summary><see cref="MarkClockUncertain"/> as a task.</summary>
    public Task<ClockAttempt> MarkClockUncertainAsync(string id, CancellationToken cancellationToken) =>
        Task.Run(() => MarkClockUncertain(id, cancellationToken), cancellationToken);

    /// <summary>
    /// Go <c>RecordClockReply</c>: accepts an original control-call reply or a recovered receipt. A lookup failure
    /// must never be repackaged as a pre-admission control refusal.
    /// </summary>
    public ClockAttempt RecordClockReply(string id, Pb.ControlReply reply, CancellationToken cancellationToken) =>
        Run(tx => ClockJournal.RecordReply(tx, id, reply), cancellationToken);

    /// <summary><see cref="RecordClockReply"/> as a task.</summary>
    public Task<ClockAttempt> RecordClockReplyAsync(string id, Pb.ControlReply reply, CancellationToken cancellationToken) =>
        Task.Run(() => RecordClockReply(id, reply, cancellationToken), cancellationToken);

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

    /// <summary>Go <c>LookupClockEpoch</c>.</summary>
    public ClockEpochObligation LookupClockEpoch(string id, CancellationToken cancellationToken) => Run(tx => ClockJournal.LookupEpoch(tx, id), cancellationToken);

    /// <summary><see cref="LookupClockEpoch"/> as a task.</summary>
    public Task<ClockEpochObligation> LookupClockEpochAsync(string id, CancellationToken cancellationToken) =>
        Task.Run(() => LookupClockEpoch(id, cancellationToken), cancellationToken);

    /// <summary>
    /// Go <c>LoadClockEpochs</c>: checks the complete attempt catalog as well as ownership rows. It never omits a
    /// missing or corrupt obligation from recovery.
    /// </summary>
    public IReadOnlyList<ClockEpochObligation> LoadClockEpochs(int limit, CancellationToken cancellationToken) => Run(tx => ClockJournal.LoadEpochs(tx, limit), cancellationToken);

    /// <summary><see cref="LoadClockEpochs"/> as a task.</summary>
    public Task<IReadOnlyList<ClockEpochObligation>> LoadClockEpochsAsync(int limit, CancellationToken cancellationToken) =>
        Task.Run(() => LoadClockEpochs(limit, cancellationToken), cancellationToken);

    /// <summary>Go <c>BeginClockPause</c>.</summary>
    public ClockEpochObligation BeginClockPause(string id, ulong sequence, CancellationToken cancellationToken) => Run(tx => ClockJournal.BeginPause(tx, id, sequence), cancellationToken);

    /// <summary><see cref="BeginClockPause"/> as a task.</summary>
    public Task<ClockEpochObligation> BeginClockPauseAsync(string id, ulong sequence, CancellationToken cancellationToken) =>
        Task.Run(() => BeginClockPause(id, sequence, cancellationToken), cancellationToken);

    /// <summary>Go <c>MarkClockPauseUncertain</c>.</summary>
    public ClockEpochObligation MarkClockPauseUncertain(string id, ulong sequence, CancellationToken cancellationToken) =>
        Run(tx => ClockJournal.MarkPauseUncertain(tx, id, sequence), cancellationToken);

    /// <summary><see cref="MarkClockPauseUncertain"/> as a task.</summary>
    public Task<ClockEpochObligation> MarkClockPauseUncertainAsync(string id, ulong sequence, CancellationToken cancellationToken) =>
        Task.Run(() => MarkClockPauseUncertain(id, sequence, cancellationToken), cancellationToken);

    /// <summary>
    /// Go <c>ObserveClockEpoch</c>: stores fresh evidence under the current pause sequence. An uncertain pause must
    /// be observed as running before another pause dispatch.
    /// </summary>
    public ClockEpochObligation ObserveClockEpoch(string id, ulong sequence, PbCommon.ObservationContext current, Pb.Status? status, CancellationToken cancellationToken) =>
        Run(tx => ClockJournal.ObserveEpoch(tx, id, sequence, current, status), cancellationToken);

    /// <summary><see cref="ObserveClockEpoch"/> as a task.</summary>
    public Task<ClockEpochObligation> ObserveClockEpochAsync(string id, ulong sequence, PbCommon.ObservationContext current, Pb.Status? status, CancellationToken cancellationToken) =>
        Task.Run(() => ObserveClockEpoch(id, sequence, current, status, cancellationToken), cancellationToken);

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

    /// <summary>
    /// Go <c>RetireClockHistory</c>: drops only settled or never-dispatched history. The exact retained index and
    /// retirement watermark commit with deletions, so retirement cannot turn an old request into permission for
    /// another native dispatch.
    /// </summary>
    public ClockRetirement RetireClockHistory(ClockSequenceState expected, uint keepRecent, CancellationToken cancellationToken) =>
        Run(tx => ClockJournal.RetireHistory(tx, expected, keepRecent), cancellationToken);

    /// <summary><see cref="RetireClockHistory"/> as a task.</summary>
    public Task<ClockRetirement> RetireClockHistoryAsync(ClockSequenceState expected, uint keepRecent, CancellationToken cancellationToken) =>
        Task.Run(() => RetireClockHistory(expected, keepRecent, cancellationToken), cancellationToken);

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

    /// <summary>
    /// Go <c>MarkClockScopeSuperseded</c>: retires only a positively replaced world scope. The original command
    /// outcome stays uncertain; no epoch or absence is inferred.
    /// </summary>
    public ClockAttempt MarkClockScopeSuperseded(string id, PbCommon.ObservationContext observed, CancellationToken cancellationToken) =>
        Run(tx => ClockJournal.MarkScopeSuperseded(tx, id, observed), cancellationToken);

    /// <summary><see cref="MarkClockScopeSuperseded"/> as a task.</summary>
    public Task<ClockAttempt> MarkClockScopeSupersededAsync(string id, PbCommon.ObservationContext observed, CancellationToken cancellationToken) =>
        Task.Run(() => MarkClockScopeSuperseded(id, observed, cancellationToken), cancellationToken);

    /// <summary>Go <c>ReadClockSequence</c>.</summary>
    public ClockSequenceState ReadClockSequence(CancellationToken cancellationToken) => Run(tx => ClockJournal.ReadSequence(tx), cancellationToken);

    /// <summary><see cref="ReadClockSequence"/> as a task.</summary>
    public Task<ClockSequenceState> ReadClockSequenceAsync(CancellationToken cancellationToken) => Task.Run(() => ReadClockSequence(cancellationToken), cancellationToken);
}
