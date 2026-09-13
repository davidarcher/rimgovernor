package clock

import (
	"context"
	"database/sql"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

func validateClockScope(attempt Attempt, observed *c.ObservationContext) error {
	if attempt.Intent.Command.Start == nil || (attempt.Phase != Dispatched && attempt.Phase != Uncertain) {
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

// MarkScopeSuperseded retires only a positively replaced world scope.
// The original command outcome stays uncertain; no epoch or absence is inferred.
func MarkScopeSuperseded(ctx context.Context, tx *sql.Tx, id string, observed *c.ObservationContext) (Attempt, error) {
	if err := submissionID(id); err != nil {
		return Attempt{}, err
	}
	if observed == nil {
		return Attempt{}, ErrConflict
	}
	observed = proto.Clone(observed).(*c.ObservationContext)
	data, err := clockBinary(observed)
	if err != nil {
		return Attempt{}, err
	}
	old, err := loadClock(ctx, tx, id)
	if err != nil {
		return Attempt{}, err
	}
	if err = validateClockScope(old, observed); err != nil {
		return Attempt{}, err
	}
	if old.SupersededAt != nil {
		if !proto.Equal(old.SupersededAt, observed) {
			return Attempt{}, ErrConflict
		}
		return old, nil
	}
	if _, err = tx.ExecContext(ctx, "UPDATE clock_attempts SET scope_context=? WHERE request_id=?", data, id); err != nil {
		return Attempt{}, err
	}
	return loadClock(ctx, tx, id)
}
