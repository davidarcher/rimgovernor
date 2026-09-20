package clock

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

func appliedStartEpoch(attempt Attempt) *k.Epoch {
	if attempt.Intent.Command.Start == nil || attempt.Phase != Applied {
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
func checkClockEpoch(ctx context.Context, tx *sql.Tx, attempt Attempt) (EpochObligation, error) {
	value := EpochObligation{StartRequestID: attempt.Intent.RequestID}
	original := appliedStartEpoch(attempt)
	var sequence string
	var observed, status []byte
	err := tx.QueryRowContext(ctx, "SELECT stage,sequence,context,status FROM clock_epochs WHERE start_request_id=?", value.StartRequestID).Scan(&value.Stage, &sequence, &observed, &status)
	if original == nil {
		if errors.Is(err, sql.ErrNoRows) {
			return EpochObligation{}, nil
		}
		if err != nil {
			return EpochObligation{}, err
		}
		return EpochObligation{}, errors.New("clock epoch has no applied start")
	}
	if err != nil {
		return EpochObligation{}, err
	}
	value.Epoch = proto.Clone(original).(*k.Epoch)
	value.Sequence, err = strconv.ParseUint(sequence, 10, 64)
	if err != nil || strconv.FormatUint(value.Sequence, 10) != sequence {
		return EpochObligation{}, errors.New("invalid clock pause sequence")
	}
	if observed != nil {
		value.Context = &c.ObservationContext{}
		if err = clockUnmarshal(observed, value.Context); err != nil {
			return EpochObligation{}, err
		}
	}
	if status != nil {
		value.Status = &k.Status{}
		if err = clockUnmarshal(status, value.Status); err != nil {
			return EpochObligation{}, err
		}
	}
	if err = validateClockEpochRecord(value); err != nil {
		return EpochObligation{}, err
	}
	return value, nil
}

func validateClockEpochRecord(v EpochObligation) error {
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
		pending := v.Sequence > 0 && (v.Stage == EpochPausing || v.Stage == EpochUncertain)
		if EpochStage(state) != v.Stage && !(pending && state == bridge.ClockEpochRequired) {
			return errors.New("clock epoch stage contradicts evidence")
		}
		return nil
	}
	if v.Status != nil {
		return errors.New("clock epoch status has no observation context")
	}
	if v.Stage == EpochRequired && v.Sequence == 0 || v.Stage == EpochPausing && v.Sequence > 0 || v.Stage == EpochUncertain && v.Sequence > 0 {
		return nil
	}
	return errors.New("clock epoch stage has no matching evidence")
}

func loadClockEpoch(ctx context.Context, tx *sql.Tx, id string) (EpochObligation, error) {
	attempt, err := loadClock(ctx, tx, id)
	if err != nil {
		return EpochObligation{}, err
	}
	if appliedStartEpoch(attempt) == nil {
		return EpochObligation{}, ErrNotFound
	}
	return checkClockEpoch(ctx, tx, attempt)
}

func LookupEpoch(ctx context.Context, tx *sql.Tx, id string) (EpochObligation, error) {
	if err := submissionID(id); err != nil {
		return EpochObligation{}, err
	}
	return loadClockEpoch(ctx, tx, id)
}

// LoadEpochs checks the complete attempt catalog as well as ownership rows.
// It never omits a missing or corrupt obligation from recovery.
func LoadEpochs(ctx context.Context, tx *sql.Tx, limit int) ([]EpochObligation, error) {
	if limit < 1 || limit > 4096 {
		return nil, ErrCapacity
	}
	// The sequence head is loaded once: it has already checked that the
	// catalog's rows are exactly the retained sequences, so each attempt is
	// read unchecked rather than re-verifying the head per row, which made
	// this read quadratic in the retained tail (#634).
	session, head, err := loadClockSequence(ctx, tx)
	if err != nil {
		return nil, err
	}
	values := make([]EpochObligation, 0)
	for _, sequence := range head.Retained {
		id, _ := ClockRequestID(session, sequence)
		attempt, e := loadClockUnchecked(ctx, tx, id, session)
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
	return values, nil
}

func terminalClockEpoch(stage EpochStage) bool {
	return stage == EpochPaused || stage == EpochRetired || stage == EpochSuperseded
}

func updateEpoch(ctx context.Context, tx *sql.Tx, id string, sequence uint64, change func(EpochObligation) (EpochObligation, error)) (EpochObligation, error) {
	if err := submissionID(id); err != nil {
		return EpochObligation{}, err
	}
	old, err := loadClockEpoch(ctx, tx, id)
	if err != nil {
		return EpochObligation{}, err
	}
	if sequence != old.Sequence {
		return EpochObligation{}, ErrConflict
	}
	next, err := change(old)
	if err != nil {
		return EpochObligation{}, err
	}
	if err = validateClockEpochRecord(next); err != nil {
		return EpochObligation{}, err
	}
	if old.Stage == next.Stage && old.Sequence == next.Sequence && proto.Equal(old.Context, next.Context) && proto.Equal(old.Status, next.Status) {
		return old, nil
	}
	var observed, status []byte
	if next.Context != nil {
		observed, err = clockBinary(next.Context)
		if err != nil {
			return EpochObligation{}, err
		}
	}
	if next.Status != nil {
		status, err = clockBinary(next.Status)
		if err != nil {
			return EpochObligation{}, err
		}
	}
	if _, err = tx.ExecContext(ctx, "UPDATE clock_epochs SET stage=?,sequence=?,context=?,status=? WHERE start_request_id=?", next.Stage, strconv.FormatUint(next.Sequence, 10), observed, status, id); err != nil {
		return EpochObligation{}, err
	}
	return next, nil
}

func BeginPause(ctx context.Context, tx *sql.Tx, id string, sequence uint64) (EpochObligation, error) {
	return updateEpoch(ctx, tx, id, sequence, func(v EpochObligation) (EpochObligation, error) {
		if v.Stage != EpochRequired {
			return EpochObligation{}, ErrConflict
		}
		if v.Sequence == math.MaxUint64 {
			return EpochObligation{}, ErrCapacity
		}
		v.Sequence++
		v.Stage = EpochPausing
		return v, nil
	})
}

func MarkPauseUncertain(ctx context.Context, tx *sql.Tx, id string, sequence uint64) (EpochObligation, error) {
	return updateEpoch(ctx, tx, id, sequence, func(v EpochObligation) (EpochObligation, error) {
		if v.Stage != EpochPausing && v.Stage != EpochUncertain {
			return EpochObligation{}, ErrConflict
		}
		v.Stage = EpochUncertain
		return v, nil
	})
}

// ObserveEpoch stores fresh evidence under the current pause sequence.
// An uncertain pause must be observed as running before another pause dispatch.
func ObserveEpoch(ctx context.Context, tx *sql.Tx, id string, sequence uint64, current *c.ObservationContext, status *k.Status) (EpochObligation, error) {
	if current == nil {
		return EpochObligation{}, errors.New("clock observation context required")
	}
	current = proto.Clone(current).(*c.ObservationContext)
	if status != nil {
		status = proto.Clone(status).(*k.Status)
	}
	return updateEpoch(ctx, tx, id, sequence, func(v EpochObligation) (EpochObligation, error) {
		if terminalClockEpoch(v.Stage) {
			if proto.Equal(v.Context, current) && proto.Equal(v.Status, status) {
				return v, nil
			}
			return EpochObligation{}, ErrConflict
		}
		if err := clockGenerationFloor(v.Epoch.Origin, current); err != nil {
			return EpochObligation{}, err
		}
		if v.Context != nil && proto.Equal(v.Context.Identity, current.Identity) {
			if current.GetTick() < v.Context.GetTick() || (v.Context.NativeGeneration != nil && (current.NativeGeneration == nil || current.GetNativeGeneration() < v.Context.GetNativeGeneration())) {
				return EpochObligation{}, ErrConflict
			}
		}
		state, err := bridge.AssessClockEpoch(v.Epoch, current, status)
		if err != nil {
			return EpochObligation{}, err
		}
		v.Stage, v.Context, v.Status = EpochStage(state), current, status
		return v, nil
	})
}

func clockGenerationFloor(prior, current *c.ObservationContext) error {
	if prior != nil && current != nil && proto.Equal(prior.Identity, current.Identity) && prior.NativeGeneration != nil && (current.NativeGeneration == nil || current.GetNativeGeneration() < prior.GetNativeGeneration()) {
		return ErrConflict
	}
	return nil
}
