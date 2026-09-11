package store

import (
	"context"
	"database/sql"
	"errors"
	"math"
	"strconv"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

func appliedStartEpoch(attempt ClockAttempt) *k.Epoch {
	if attempt.Intent.Command.Start == nil || attempt.Phase != ClockApplied {
		return nil
	}
	status := attempt.Reply.GetReceipt().GetApplied().GetStatus()
	if status.GetRunning() != nil {
		return status.GetRunning().Epoch
	}
	if status.GetStopping() != nil {
		return status.GetStopping().Epoch
	}
	return status.GetStopped().GetEpoch()
}

// checkClockEpoch also runs when loading its original attempt, so a missing
// ownership record cannot silently turn a completed Start into unowned history.
func checkClockEpoch(ctx context.Context, tx *sql.Tx, attempt ClockAttempt) (ClockEpochObligation, error) {
	value := ClockEpochObligation{StartRequestID: attempt.Intent.RequestID}
	original := appliedStartEpoch(attempt)
	var sequence string
	var observed, status []byte
	err := tx.QueryRowContext(ctx, "SELECT stage,sequence,context,status FROM clock_epochs WHERE start_request_id=?", value.StartRequestID).Scan(&value.Stage, &sequence, &observed, &status)
	if original == nil {
		if errors.Is(err, sql.ErrNoRows) {
			return ClockEpochObligation{}, nil
		}
		if err != nil {
			return ClockEpochObligation{}, err
		}
		return ClockEpochObligation{}, errors.New("clock epoch has no applied start")
	}
	if err != nil {
		return ClockEpochObligation{}, err
	}
	value.Epoch = proto.Clone(original).(*k.Epoch)
	value.Sequence, err = strconv.ParseUint(sequence, 10, 64)
	if err != nil || strconv.FormatUint(value.Sequence, 10) != sequence {
		return ClockEpochObligation{}, errors.New("invalid clock pause sequence")
	}
	if observed != nil {
		value.Context = &c.ObservationContext{}
		if err = clockUnmarshal(observed, value.Context); err != nil {
			return ClockEpochObligation{}, err
		}
	}
	if status != nil {
		value.Status = &k.Status{}
		if err = clockUnmarshal(status, value.Status); err != nil {
			return ClockEpochObligation{}, err
		}
	}
	if err = validateClockEpochRecord(value); err != nil {
		return ClockEpochObligation{}, err
	}
	return value, nil
}

func validateClockEpochRecord(v ClockEpochObligation) error {
	if err := bridge.ValidateClockEpoch(v.Epoch); err != nil {
		return err
	}
	if v.Context != nil {
		if err := clockGenerationFloor(v.Epoch.Origin, v.Context); err != nil {
			return err
		}
		state, err := bridge.AssessClockEpoch(v.Epoch, v.Context, v.Status)
		if err != nil {
			return err
		}
		// A dispatch retains its last pre-call observation as a freshness floor.
		pending := v.Sequence > 0 && (v.Stage == ClockEpochPausing || v.Stage == ClockEpochUncertain)
		if ClockEpochStage(state) != v.Stage && !(pending && state == bridge.ClockEpochRequired) {
			return errors.New("clock epoch stage contradicts evidence")
		}
		return nil
	}
	if v.Status != nil {
		return errors.New("clock epoch status has no observation context")
	}
	if v.Stage == ClockEpochRequired && v.Sequence == 0 || v.Stage == ClockEpochPausing && v.Sequence > 0 || v.Stage == ClockEpochUncertain && v.Sequence > 0 {
		return nil
	}
	return errors.New("clock epoch stage has no matching evidence")
}

func loadClockEpoch(ctx context.Context, tx *sql.Tx, id string) (ClockEpochObligation, error) {
	attempt, err := loadClock(ctx, tx, id)
	if err != nil {
		return ClockEpochObligation{}, err
	}
	if appliedStartEpoch(attempt) == nil {
		return ClockEpochObligation{}, ErrNotFound
	}
	return checkClockEpoch(ctx, tx, attempt)
}

func (s *Store) LookupClockEpoch(ctx context.Context, id string) (ClockEpochObligation, error) {
	if err := submissionID(id); err != nil {
		return ClockEpochObligation{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockEpochObligation{}, err
	}
	defer tx.Rollback()
	v, err := loadClockEpoch(ctx, tx, id)
	if err != nil {
		return ClockEpochObligation{}, err
	}
	return v, tx.Commit()
}

// LoadClockEpochs checks the complete attempt catalog as well as ownership rows.
// It never omits a missing or corrupt obligation from recovery.
func (s *Store) LoadClockEpochs(ctx context.Context, limit int) ([]ClockEpochObligation, error) {
	if limit < 1 || limit > 4096 {
		return nil, ErrCapacity
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	rows, err := tx.QueryContext(ctx, "SELECT request_id FROM clock_attempts ORDER BY request_id LIMIT 4097")
	if err != nil {
		return nil, err
	}
	var ids []string
	for rows.Next() {
		var id string
		if err = rows.Scan(&id); err != nil {
			rows.Close()
			return nil, err
		}
		ids = append(ids, id)
	}
	err = errors.Join(rows.Err(), rows.Close())
	if err != nil {
		return nil, err
	}
	if len(ids) > 4096 {
		return nil, ErrCapacity
	}
	values := make([]ClockEpochObligation, 0)
	for _, id := range ids {
		attempt, e := loadClock(ctx, tx, id)
		if e != nil {
			return nil, e
		}
		if appliedStartEpoch(attempt) == nil {
			continue
		}
		v, e := checkClockEpoch(ctx, tx, attempt)
		if e != nil {
			return nil, e
		}
		values = append(values, v)
		if len(values) > limit {
			return nil, ErrCapacity
		}
	}
	var count int
	if err = tx.QueryRowContext(ctx, "SELECT count(*) FROM clock_epochs").Scan(&count); err != nil {
		return nil, err
	}
	if count != len(values) {
		return nil, errors.New("orphaned clock epoch obligation")
	}
	return values, tx.Commit()
}

func terminalClockEpoch(stage ClockEpochStage) bool {
	return stage == ClockEpochPaused || stage == ClockEpochRetired || stage == ClockEpochSuperseded
}

func (s *Store) updateClockEpoch(ctx context.Context, id string, sequence uint64, change func(ClockEpochObligation) (ClockEpochObligation, error)) (ClockEpochObligation, error) {
	if err := submissionID(id); err != nil {
		return ClockEpochObligation{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockEpochObligation{}, err
	}
	defer tx.Rollback()
	old, err := loadClockEpoch(ctx, tx, id)
	if err != nil {
		return ClockEpochObligation{}, err
	}
	if sequence != old.Sequence {
		return ClockEpochObligation{}, ErrConflict
	}
	next, err := change(old)
	if err != nil {
		return ClockEpochObligation{}, err
	}
	if err = validateClockEpochRecord(next); err != nil {
		return ClockEpochObligation{}, err
	}
	if old.Stage == next.Stage && old.Sequence == next.Sequence && proto.Equal(old.Context, next.Context) && proto.Equal(old.Status, next.Status) {
		return old, tx.Commit()
	}
	var observed, status []byte
	if next.Context != nil {
		observed, err = clockBinary(next.Context)
		if err != nil {
			return ClockEpochObligation{}, err
		}
	}
	if next.Status != nil {
		status, err = clockBinary(next.Status)
		if err != nil {
			return ClockEpochObligation{}, err
		}
	}
	if _, err = tx.ExecContext(ctx, "UPDATE clock_epochs SET stage=?,sequence=?,context=?,status=? WHERE start_request_id=?", next.Stage, strconv.FormatUint(next.Sequence, 10), observed, status, id); err != nil {
		return ClockEpochObligation{}, err
	}
	return next, tx.Commit()
}

func (s *Store) BeginClockPause(ctx context.Context, id string, sequence uint64) (ClockEpochObligation, error) {
	return s.updateClockEpoch(ctx, id, sequence, func(v ClockEpochObligation) (ClockEpochObligation, error) {
		if v.Stage != ClockEpochRequired {
			return ClockEpochObligation{}, ErrConflict
		}
		if v.Sequence == math.MaxUint64 {
			return ClockEpochObligation{}, ErrCapacity
		}
		v.Sequence++
		v.Stage = ClockEpochPausing
		return v, nil
	})
}

func (s *Store) MarkClockPauseUncertain(ctx context.Context, id string, sequence uint64) (ClockEpochObligation, error) {
	return s.updateClockEpoch(ctx, id, sequence, func(v ClockEpochObligation) (ClockEpochObligation, error) {
		if v.Stage != ClockEpochPausing && v.Stage != ClockEpochUncertain {
			return ClockEpochObligation{}, ErrConflict
		}
		v.Stage = ClockEpochUncertain
		return v, nil
	})
}

// ObserveClockEpoch stores fresh evidence under the current pause sequence.
// An uncertain pause must be observed as running before another pause dispatch.
func (s *Store) ObserveClockEpoch(ctx context.Context, id string, sequence uint64, current *c.ObservationContext, status *k.Status) (ClockEpochObligation, error) {
	if current == nil {
		return ClockEpochObligation{}, errors.New("clock observation context required")
	}
	current = proto.Clone(current).(*c.ObservationContext)
	if status != nil {
		status = proto.Clone(status).(*k.Status)
	}
	return s.updateClockEpoch(ctx, id, sequence, func(v ClockEpochObligation) (ClockEpochObligation, error) {
		if terminalClockEpoch(v.Stage) {
			if proto.Equal(v.Context, current) && proto.Equal(v.Status, status) {
				return v, nil
			}
			return ClockEpochObligation{}, ErrConflict
		}
		if err := clockGenerationFloor(v.Epoch.Origin, current); err != nil {
			return ClockEpochObligation{}, err
		}
		if v.Context != nil && proto.Equal(v.Context.Identity, current.Identity) {
			if current.GetTick() < v.Context.GetTick() || (v.Context.NativeGeneration != nil && (current.NativeGeneration == nil || current.GetNativeGeneration() < v.Context.GetNativeGeneration())) {
				return ClockEpochObligation{}, ErrConflict
			}
		}
		state, err := bridge.AssessClockEpoch(v.Epoch, current, status)
		if err != nil {
			return ClockEpochObligation{}, err
		}
		v.Stage, v.Context, v.Status = ClockEpochStage(state), current, status
		return v, nil
	})
}

func clockGenerationFloor(prior, current *c.ObservationContext) error {
	if prior != nil && current != nil && proto.Equal(prior.Identity, current.Identity) && prior.NativeGeneration != nil && (current.NativeGeneration == nil || current.GetNativeGeneration() < prior.GetNativeGeneration()) {
		return ErrConflict
	}
	return nil
}
