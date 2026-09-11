package store

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

func validateClockScope(attempt ClockAttempt, observed *c.ObservationContext) error {
	if attempt.Intent.Command.Start == nil || (attempt.Phase != ClockDispatched && attempt.Phase != ClockUncertain) {
		return ErrConflict
	}
	if err := bridge.ValidateContext(observed); err != nil {
		return err
	}
	if len(observed.ProtoReflect().GetUnknown()) != 0 || len(observed.Identity.ProtoReflect().GetUnknown()) != 0 {
		return ErrConflict
	}
	original := attempt.Intent.Snapshot
	actual := observed.Identity
	if actual.GetColonyId() == string(original.Colony) && actual.GetMapId() == int32(original.Map) && actual.GetLoadToken() == string(original.Load) {
		return ErrConflict
	}
	return nil
}

// MarkClockScopeSuperseded retires only a positively replaced world scope.
// The original command outcome stays uncertain; no epoch or absence is inferred.
func (s *Store) MarkClockScopeSuperseded(ctx context.Context, id string, observed *c.ObservationContext) (ClockAttempt, error) {
	if err := submissionID(id); err != nil {
		return ClockAttempt{}, err
	}
	if observed == nil {
		return ClockAttempt{}, ErrConflict
	}
	observed = proto.Clone(observed).(*c.ObservationContext)
	data, err := clockBinary(observed)
	if err != nil {
		return ClockAttempt{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockAttempt{}, err
	}
	defer tx.Rollback()
	old, err := loadClock(ctx, tx, id)
	if err != nil {
		return ClockAttempt{}, err
	}
	if err = validateClockScope(old, observed); err != nil {
		return ClockAttempt{}, err
	}
	if old.SupersededAt != nil {
		if !proto.Equal(old.SupersededAt, observed) {
			return ClockAttempt{}, ErrConflict
		}
		return old, tx.Commit()
	}
	if _, err = tx.ExecContext(ctx, "UPDATE clock_attempts SET scope_context=? WHERE request_id=?", data, id); err != nil {
		return ClockAttempt{}, err
	}
	saved, err := loadClock(ctx, tx, id)
	if err != nil {
		return ClockAttempt{}, err
	}
	return saved, tx.Commit()
}
