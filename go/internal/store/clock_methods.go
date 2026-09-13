package store

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/store/clock"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// PrepareClock returns created=false only for exact durable intent replay. It
// records no live lease and grants no permission to call native control.
func (s *Store) PrepareClock(ctx context.Context, intent ClockIntent) (ClockAttempt, bool, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockAttempt{}, false, err
	}
	defer tx.Rollback()
	v, created, err := clock.Prepare(ctx, tx, intent)
	if err != nil {
		return ClockAttempt{}, false, err
	}
	return v, created, tx.Commit()
}
func (s *Store) LookupClockAttempt(ctx context.Context, id string) (ClockAttempt, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockAttempt{}, err
	}
	defer tx.Rollback()
	v, err := clock.LookupAttempt(ctx, tx, id)
	if err != nil {
		return ClockAttempt{}, err
	}
	return v, tx.Commit()
}
func (s *Store) LoadClockAttempts(ctx context.Context, limit int) ([]ClockAttempt, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	v, err := clock.LoadAttempts(ctx, tx, limit)
	if err != nil {
		return nil, err
	}
	return v, tx.Commit()
}
func (s *Store) DispatchClock(ctx context.Context, id string) (ClockAttempt, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockAttempt{}, err
	}
	defer tx.Rollback()
	v, err := clock.Dispatch(ctx, tx, id)
	if err != nil {
		return ClockAttempt{}, err
	}
	return v, tx.Commit()
}
func (s *Store) MarkClockUncertain(ctx context.Context, id string) (ClockAttempt, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockAttempt{}, err
	}
	defer tx.Rollback()
	v, err := clock.MarkUncertain(ctx, tx, id)
	if err != nil {
		return ClockAttempt{}, err
	}
	return v, tx.Commit()
}

// RecordClockReply accepts an original control-call reply or a recovered receipt.
// A lookup failure must never be repackaged as a pre-admission control refusal.
func (s *Store) RecordClockReply(ctx context.Context, id string, reply *k.ControlReply) (ClockAttempt, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockAttempt{}, err
	}
	defer tx.Rollback()
	v, err := clock.RecordReply(ctx, tx, id, reply)
	if err != nil {
		return ClockAttempt{}, err
	}
	return v, tx.Commit()
}

// CompactClockHistory keeps a recent page tail and every unreviewed or
// unacknowledged interruption/gap. Acknowledgement replies move to an indexed
// archive; routine polling never loads that growing archive into memory.
func (s *Store) CompactClockHistory(ctx context.Context, profile string) (ClockHistoryCompaction, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockHistoryCompaction{}, err
	}
	defer tx.Rollback()
	v, err := clock.CompactHistory(ctx, tx, profile)
	if err != nil {
		return ClockHistoryCompaction{}, err
	}
	return v, tx.Commit()
}
func (s *Store) LookupClockEpoch(ctx context.Context, id string) (ClockEpochObligation, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockEpochObligation{}, err
	}
	defer tx.Rollback()
	v, err := clock.LookupEpoch(ctx, tx, id)
	if err != nil {
		return ClockEpochObligation{}, err
	}
	return v, tx.Commit()
}

// LoadClockEpochs checks the complete attempt catalog as well as ownership rows.
// It never omits a missing or corrupt obligation from recovery.
func (s *Store) LoadClockEpochs(ctx context.Context, limit int) ([]ClockEpochObligation, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	v, err := clock.LoadEpochs(ctx, tx, limit)
	if err != nil {
		return nil, err
	}
	return v, tx.Commit()
}
func (s *Store) BeginClockPause(ctx context.Context, id string, sequence uint64) (ClockEpochObligation, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockEpochObligation{}, err
	}
	defer tx.Rollback()
	v, err := clock.BeginPause(ctx, tx, id, sequence)
	if err != nil {
		return ClockEpochObligation{}, err
	}
	return v, tx.Commit()
}
func (s *Store) MarkClockPauseUncertain(ctx context.Context, id string, sequence uint64) (ClockEpochObligation, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockEpochObligation{}, err
	}
	defer tx.Rollback()
	v, err := clock.MarkPauseUncertain(ctx, tx, id, sequence)
	if err != nil {
		return ClockEpochObligation{}, err
	}
	return v, tx.Commit()
}

// ObserveClockEpoch stores fresh evidence under the current pause sequence.
// An uncertain pause must be observed as running before another pause dispatch.
func (s *Store) ObserveClockEpoch(ctx context.Context, id string, sequence uint64, current *c.ObservationContext, status *k.Status) (ClockEpochObligation, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockEpochObligation{}, err
	}
	defer tx.Rollback()
	v, err := clock.ObserveEpoch(ctx, tx, id, sequence, current, status)
	if err != nil {
		return ClockEpochObligation{}, err
	}
	return v, tx.Commit()
}

// BindClockInbox binds one native profile journal, independent of loaded worlds.
func (s *Store) BindClockInbox(ctx context.Context, profile string) (ClockInboxState, error) {
	path, err := clock.CanonicalProfile(profile)
	if err != nil {
		return ClockInboxState{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockInboxState{}, err
	}
	defer tx.Rollback()
	v, err := clock.BindInbox(ctx, tx, path)
	if err != nil {
		return ClockInboxState{}, err
	}
	return v, tx.Commit()
}
func (s *Store) ReadClockInbox(ctx context.Context, profile string) (ClockInboxState, error) {
	inbox, err := s.LoadClockInbox(ctx, profile, clock.InboxCapacity)
	return inbox.State, err
}

// LoadClockInbox returns every retained page or fails; limit never truncates.
// Ingestion is not processing or acknowledgement of an interruption.
func (s *Store) LoadClockInbox(ctx context.Context, profile string, limit int) (ClockInbox, error) {
	path, err := clock.CanonicalProfile(profile)
	if err != nil {
		return ClockInbox{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockInbox{}, err
	}
	defer tx.Rollback()
	v, err := clock.LoadInbox(ctx, tx, path, limit)
	if err != nil {
		return ClockInbox{}, err
	}
	return v, tx.Commit()
}

// AppendClockEvents commits a scanned page, surviving events and cursor together.
// Only exact retained request/page replay is a no-op. A changed stale page must
// be refetched at the current cursor; its new context does not rewrite history.
func (s *Store) AppendClockEvents(ctx context.Context, profile string, request *k.EventsRequest, page *k.EventsPage) (ClockInboxState, bool, error) {
	path, err := clock.CanonicalProfile(profile)
	if err != nil {
		return ClockInboxState{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockInboxState{}, false, err
	}
	defer tx.Rollback()
	v, created, err := clock.AppendEvents(ctx, tx, path, request, page)
	if err != nil {
		return ClockInboxState{}, false, err
	}
	return v, created, tx.Commit()
}

// RetireClockHistory drops only settled or never-dispatched history. The exact
// retained index and retirement watermark commit with deletions, so retirement
// cannot turn an old request into permission for another native dispatch.
func (s *Store) RetireClockHistory(ctx context.Context, expected ClockSequenceState, keepRecent uint32) (ClockRetirement, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockRetirement{}, err
	}
	defer tx.Rollback()
	v, err := clock.RetireHistory(ctx, tx, expected, keepRecent)
	if err != nil {
		return ClockRetirement{}, err
	}
	return v, tx.Commit()
}
func (s *Store) ReadClockReview(ctx context.Context, profile string) (ClockReviewState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockReviewState{}, err
	}
	defer tx.Rollback()
	v, err := clock.ReadReview(ctx, tx, profile)
	if err != nil {
		return ClockReviewState{}, err
	}
	return v, tx.Commit()
}
func (s *Store) ReviewClockEvents(ctx context.Context, profile string, expectedRevision uint64) (ClockReviewState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockReviewState{}, err
	}
	defer tx.Rollback()
	v, err := clock.ReviewEvents(ctx, tx, profile, expectedRevision)
	if err != nil {
		return ClockReviewState{}, err
	}
	return v, tx.Commit()
}
func (s *Store) AcknowledgeClockEvents(ctx context.Context, profile string, ack ClockAcknowledgement) (ClockReviewState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockReviewState{}, err
	}
	defer tx.Rollback()
	v, err := clock.AcknowledgeEvents(ctx, tx, profile, ack)
	if err != nil {
		return ClockReviewState{}, err
	}
	return v, tx.Commit()
}

// MarkClockScopeSuperseded retires only a positively replaced world scope.
// The original command outcome stays uncertain; no epoch or absence is inferred.
func (s *Store) MarkClockScopeSuperseded(ctx context.Context, id string, observed *c.ObservationContext) (ClockAttempt, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockAttempt{}, err
	}
	defer tx.Rollback()
	v, err := clock.MarkScopeSuperseded(ctx, tx, id, observed)
	if err != nil {
		return ClockAttempt{}, err
	}
	return v, tx.Commit()
}
func (s *Store) ReadClockSequence(ctx context.Context) (ClockSequenceState, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockSequenceState{}, err
	}
	defer tx.Rollback()
	v, err := clock.ReadClockSequence(ctx, tx)
	if err != nil {
		return ClockSequenceState{}, err
	}
	return v, tx.Commit()
}
