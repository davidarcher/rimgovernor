package clock

import (
	"bytes"
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/hex"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

const clockColumns = "request_id,native_action_id,payload,phase,reply,scope_context"

func scanClock(row interface{ Scan(...any) error }, session ControllerSessionID) (Attempt, error) {
	var id, action string
	var payload, reply, scope []byte
	var phase Phase
	if err := row.Scan(&id, &action, &payload, &phase, &reply, &scope); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return Attempt{}, ErrNotFound
		}
		return Attempt{}, err
	}
	attempt := &c.AttemptKey{ControllerSessionId: proto.String(string(session)), ActionId: proto.String(action), AttemptId: proto.Uint64(1)}
	intent, err := decodeClockIntent(id, attempt, payload)
	if err != nil {
		return Attempt{}, err
	}
	value := Attempt{Intent: intent, NativeAttempt: attempt, Phase: phase}
	if reply != nil {
		value.Reply = &k.ControlReply{}
		if err = clockUnmarshal(reply, value.Reply); err != nil {
			return Attempt{}, err
		}
		if err = bridge.ValidateClockControlReply(value.Reply, clockExpectation(value)); err != nil {
			return Attempt{}, err
		}
	}
	switch phase {
	case Prepared, Dispatched:
		if reply != nil {
			return Attempt{}, errors.New("unissued clock phase contains outcome")
		}
	case Uncertain:
		if reply != nil && clockReplyPhase(value.Reply) != Uncertain {
			return Attempt{}, errors.New("uncertain clock phase contradicts outcome")
		}
	case Applied, Refused:
		if reply == nil || clockReplyPhase(value.Reply) != phase {
			return Attempt{}, errors.New("terminal clock phase missing matching outcome")
		}
	default:
		return Attempt{}, errors.New("invalid clock phase")
	}
	if scope != nil {
		value.SupersededAt = &c.ObservationContext{}
		if err = clockUnmarshal(scope, value.SupersededAt); err != nil {
			return Attempt{}, err
		}
		if err = validateClockScope(value, value.SupersededAt); err != nil {
			return Attempt{}, err
		}
	}
	return value, nil
}
func loadClock(ctx context.Context, tx *sql.Tx, id string) (Attempt, error) {
	session, head, err := loadClockSequence(ctx, tx)
	if err != nil {
		return Attempt{}, err
	}
	n, err := parseClockRequestID(session, id)
	if err != nil {
		return Attempt{}, err
	}
	found := false
	for _, v := range head.Retained {
		if v == n {
			found = true
			break
		}
	}
	if !found {
		if n <= head.RetiredThrough {
			return Attempt{}, ErrRetired
		}
		if n <= head.LastAllocated {
			return Attempt{}, errors.New("missing allocated clock request")
		}
		return Attempt{}, ErrNotFound
	}
	return loadClockUnchecked(ctx, tx, id, session)
}
func loadClockUnchecked(ctx context.Context, tx *sql.Tx, id string, session ControllerSessionID) (Attempt, error) {
	value, err := scanClock(tx.QueryRowContext(ctx, "SELECT "+clockColumns+" FROM clock_attempts WHERE request_id=?", id), session)
	if err != nil {
		return Attempt{}, err
	}
	if err = clockActionAvailable(ctx, tx, value.NativeAttempt.GetActionId()); err != nil {
		return Attempt{}, err
	}
	if err = checkClockWindowProfile(ctx, tx, value.Intent.Window); err != nil {
		return Attempt{}, err
	}
	if _, err = checkClockEpoch(ctx, tx, value); err != nil {
		return Attempt{}, err
	}
	return value, nil
}
func clockActionAvailable(ctx context.Context, tx *sql.Tx, action string) error {
	var count int
	if err := tx.QueryRowContext(ctx, "SELECT count(*) FROM actions WHERE id=?", action).Scan(&count); err != nil {
		return err
	}
	if count != 0 {
		return ErrConflict
	}
	return nil
}

// Prepare returns created=false only for exact durable intent replay. It
// records no live lease and grants no permission to call native control.
func Prepare(ctx context.Context, tx *sql.Tx, intent Intent) (Attempt, bool, error) {
	session, head, err := loadClockSequence(ctx, tx)
	if err != nil {
		return Attempt{}, false, err
	}
	sequence, err := parseClockRequestID(session, intent.RequestID)
	if err != nil {
		return Attempt{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return Attempt{}, false, err
	}
	candidate := Attempt{Intent: intent, NativeAttempt: &c.AttemptKey{ControllerSessionId: proto.String(string(session)), ActionId: proto.String("clock-" + hex.EncodeToString(entropy[:])), AttemptId: proto.Uint64(1)}, Phase: Prepared}
	payload, err := encodeClockIntent(candidate)
	if err != nil {
		return Attempt{}, false, err
	}
	if err = checkClockWindowProfile(ctx, tx, intent.Window); err != nil {
		return Attempt{}, false, err
	}
	old, err := loadClock(ctx, tx, intent.RequestID)
	if err == nil {
		original, e := encodeClockIntent(old)
		if e != nil {
			return Attempt{}, false, e
		}
		if !bytes.Equal(original, payload) {
			return Attempt{}, false, ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return Attempt{}, false, err
	}
	if head.LastAllocated == ^uint64(0) || sequence != head.LastAllocated+1 {
		return Attempt{}, false, ErrConflict
	}
	if err = clockActionAvailable(ctx, tx, candidate.NativeAttempt.GetActionId()); err != nil {
		return Attempt{}, false, err
	}
	var count int
	if err = tx.QueryRowContext(ctx, "SELECT count(*) FROM clock_attempts").Scan(&count); err != nil {
		return Attempt{}, false, err
	}
	if count >= 4096 {
		return Attempt{}, false, ErrCapacity
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO clock_attempts(request_id,native_action_id,payload,phase) VALUES(?,?,?,?)", intent.RequestID, candidate.NativeAttempt.GetActionId(), payload, Prepared); err != nil {
		return Attempt{}, false, conflict(err)
	}
	head.LastAllocated = sequence
	head.Retained = append(head.Retained, sequence)
	if err = saveClockSequence(ctx, tx, head); err != nil {
		return Attempt{}, false, err
	}
	saved, err := loadClock(ctx, tx, intent.RequestID)
	if err != nil {
		return Attempt{}, false, err
	}
	return saved, true, nil
}
func LookupAttempt(ctx context.Context, tx *sql.Tx, id string) (Attempt, error) {
	if err := submissionID(id); err != nil {
		return Attempt{}, err
	}
	return loadClock(ctx, tx, id)
}
func LoadAttempts(ctx context.Context, tx *sql.Tx, limit int) ([]Attempt, error) {
	if limit < 1 || limit > 4096 {
		return nil, errors.New("clock attempt limit must be 1..4096")
	}
	session, head, err := loadClockSequence(ctx, tx)
	if err != nil {
		return nil, err
	}
	if len(head.Retained) > limit {
		return nil, ErrCapacity
	}
	out := make([]Attempt, 0, len(head.Retained))
	for _, sequence := range head.Retained {
		id, _ := ClockRequestID(session, sequence)
		v, e := loadClockUnchecked(ctx, tx, id, session)
		if e != nil {
			return nil, e
		}
		out = append(out, v)
	}
	return out, nil
}
func Dispatch(ctx context.Context, tx *sql.Tx, id string) (Attempt, error) {
	return update(ctx, tx, id, func(v Attempt) (Phase, *k.ControlReply, error) {
		if v.Phase != Prepared {
			return "", nil, ErrConflict
		}
		return Dispatched, nil, nil
	})
}
func MarkUncertain(ctx context.Context, tx *sql.Tx, id string) (Attempt, error) {
	return update(ctx, tx, id, func(v Attempt) (Phase, *k.ControlReply, error) {
		if v.Phase != Dispatched && v.Phase != Uncertain {
			return "", nil, ErrConflict
		}
		return Uncertain, v.Reply, nil
	})
}

// RecordReply accepts an original control-call reply or a recovered receipt.
// A lookup failure must never be repackaged as a pre-admission control refusal.
func RecordReply(ctx context.Context, tx *sql.Tx, id string, reply *k.ControlReply) (Attempt, error) {
	if reply == nil {
		return Attempt{}, errors.New("clock reply required")
	}
	reply = proto.Clone(reply).(*k.ControlReply)
	return update(ctx, tx, id, func(v Attempt) (Phase, *k.ControlReply, error) {
		if err := bridge.ValidateClockControlReply(reply, clockExpectation(v)); err != nil {
			return "", nil, err
		}
		if v.Phase == Applied || v.Phase == Refused {
			if proto.Equal(v.Reply, reply) {
				return v.Phase, v.Reply, nil
			}
			return "", nil, ErrConflict
		}
		if v.Phase != Dispatched && v.Phase != Uncertain {
			return "", nil, ErrConflict
		}
		// Admission evidence and attempt conflicts require receipt recovery;
		// a different later refusal cannot establish that nothing happened.
		if v.Reply != nil && reply.GetReceipt() == nil && !proto.Equal(v.Reply, reply) {
			return "", nil, ErrConflict
		}
		return clockReplyPhase(reply), reply, nil
	})
}
func update(ctx context.Context, tx *sql.Tx, id string, change func(Attempt) (Phase, *k.ControlReply, error)) (Attempt, error) {
	if err := submissionID(id); err != nil {
		return Attempt{}, err
	}
	old, err := loadClock(ctx, tx, id)
	if err != nil {
		return Attempt{}, err
	}
	if old.SupersededAt != nil {
		return Attempt{}, ErrConflict
	}
	phase, reply, err := change(old)
	if err != nil {
		return Attempt{}, err
	}
	if phase == Dispatched {
		if err = checkClockWindowDispatch(ctx, tx, old.Intent.Window); err != nil {
			return Attempt{}, err
		}
	}
	if phase == old.Phase && proto.Equal(reply, old.Reply) {
		return old, nil
	}
	var data []byte
	if reply != nil {
		data, err = clockBinary(reply)
		if err != nil {
			return Attempt{}, err
		}
	}
	if _, err = tx.ExecContext(ctx, "UPDATE clock_attempts SET phase=?,reply=? WHERE request_id=?", phase, data, id); err != nil {
		return Attempt{}, err
	}
	if phase == Applied && old.Intent.Command.Start != nil {
		if _, err = tx.ExecContext(ctx, "INSERT INTO clock_epochs(start_request_id,stage,sequence) VALUES(?,'required','0')", id); err != nil {
			return Attempt{}, err
		}
	}
	return loadClock(ctx, tx, id)
}
