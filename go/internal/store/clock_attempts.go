package store

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

const clockColumns = "request_id,native_action_id,payload,phase,reply"

func scanClock(row interface{ Scan(...any) error }, session ControllerSessionID) (ClockAttempt, error) {
	var id, action string
	var payload, reply []byte
	var phase ClockPhase
	if err := row.Scan(&id, &action, &payload, &phase, &reply); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return ClockAttempt{}, ErrNotFound
		}
		return ClockAttempt{}, err
	}
	attempt := &c.AttemptKey{ControllerSessionId: proto.String(string(session)), ActionId: proto.String(action), AttemptId: proto.Uint64(1)}
	intent, err := decodeClockIntent(id, attempt, payload)
	if err != nil {
		return ClockAttempt{}, err
	}
	value := ClockAttempt{Intent: intent, NativeAttempt: attempt, Phase: phase}
	if reply != nil {
		value.Reply = &k.ControlReply{}
		if err = clockUnmarshal(reply, value.Reply); err != nil {
			return ClockAttempt{}, err
		}
		if err = bridge.ValidateClockControlReply(value.Reply, clockExpectation(value)); err != nil {
			return ClockAttempt{}, err
		}
	}
	switch phase {
	case ClockPrepared, ClockDispatched:
		if reply != nil {
			return ClockAttempt{}, errors.New("unissued clock phase contains outcome")
		}
	case ClockUncertain:
		if reply != nil && clockReplyPhase(value.Reply) != ClockUncertain {
			return ClockAttempt{}, errors.New("uncertain clock phase contradicts outcome")
		}
	case ClockApplied, ClockRefused:
		if reply == nil || clockReplyPhase(value.Reply) != phase {
			return ClockAttempt{}, errors.New("terminal clock phase missing matching outcome")
		}
	default:
		return ClockAttempt{}, errors.New("invalid clock phase")
	}
	return value, nil
}
func loadClock(ctx context.Context, tx *sql.Tx, id string) (ClockAttempt, error) {
	session, err := identity(ctx, tx)
	if err != nil {
		return ClockAttempt{}, err
	}
	value, err := scanClock(tx.QueryRowContext(ctx, "SELECT "+clockColumns+" FROM clock_attempts WHERE request_id=?", id), session)
	if err != nil {
		return ClockAttempt{}, err
	}
	if err = clockActionAvailable(ctx, tx, value.NativeAttempt.GetActionId()); err != nil {
		return ClockAttempt{}, err
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

// PrepareClock returns created=false only for exact durable intent replay. It
// records no live lease and grants no permission to call native control.
func (s *Store) PrepareClock(ctx context.Context, intent ClockIntent) (ClockAttempt, bool, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockAttempt{}, false, err
	}
	defer tx.Rollback()
	session, err := identity(ctx, tx)
	if err != nil {
		return ClockAttempt{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return ClockAttempt{}, false, err
	}
	candidate := ClockAttempt{Intent: intent, NativeAttempt: &c.AttemptKey{ControllerSessionId: proto.String(string(session)), ActionId: proto.String("clock-" + hex.EncodeToString(entropy[:])), AttemptId: proto.Uint64(1)}, Phase: ClockPrepared}
	payload, err := encodeClockIntent(candidate)
	if err != nil {
		return ClockAttempt{}, false, err
	}
	old, err := loadClock(ctx, tx, intent.RequestID)
	if err == nil {
		original, e := encodeClockIntent(old)
		if e != nil {
			return ClockAttempt{}, false, e
		}
		if !bytes.Equal(original, payload) {
			return ClockAttempt{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return ClockAttempt{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return ClockAttempt{}, false, err
	}
	if err = clockActionAvailable(ctx, tx, candidate.NativeAttempt.GetActionId()); err != nil {
		return ClockAttempt{}, false, err
	}
	var count int
	if err = tx.QueryRowContext(ctx, "SELECT count(*) FROM clock_attempts").Scan(&count); err != nil {
		return ClockAttempt{}, false, err
	}
	if count >= 4096 {
		return ClockAttempt{}, false, ErrCapacity
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO clock_attempts(request_id,native_action_id,payload,phase) VALUES(?,?,?,?)", intent.RequestID, candidate.NativeAttempt.GetActionId(), payload, ClockPrepared); err != nil {
		return ClockAttempt{}, false, conflict(err)
	}
	saved, err := loadClock(ctx, tx, intent.RequestID)
	if err != nil {
		return ClockAttempt{}, false, err
	}
	if err = tx.Commit(); err != nil {
		return ClockAttempt{}, false, err
	}
	return saved, true, nil
}
func (s *Store) LookupClockAttempt(ctx context.Context, id string) (ClockAttempt, error) {
	if err := submissionID(id); err != nil {
		return ClockAttempt{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockAttempt{}, err
	}
	defer tx.Rollback()
	v, err := loadClock(ctx, tx, id)
	if err != nil {
		return ClockAttempt{}, err
	}
	return v, tx.Commit()
}
func (s *Store) LoadClockAttempts(ctx context.Context, limit int) ([]ClockAttempt, error) {
	if limit < 1 || limit > 4096 {
		return nil, errors.New("clock attempt limit must be 1..4096")
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	rows, err := tx.QueryContext(ctx, "SELECT request_id FROM clock_attempts ORDER BY request_id LIMIT ?", limit+1)
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
	if len(ids) > limit {
		return nil, errors.New("clock attempt catalog exceeds limit")
	}
	out := make([]ClockAttempt, 0, len(ids))
	for _, id := range ids {
		v, e := loadClock(ctx, tx, id)
		if e != nil {
			return nil, e
		}
		out = append(out, v)
	}
	return out, tx.Commit()
}
func (s *Store) DispatchClock(ctx context.Context, id string) (ClockAttempt, error) {
	return s.updateClock(ctx, id, func(v ClockAttempt) (ClockPhase, *k.ControlReply, error) {
		if v.Phase != ClockPrepared {
			return "", nil, ErrConflict
		}
		return ClockDispatched, nil, nil
	})
}
func (s *Store) MarkClockUncertain(ctx context.Context, id string) (ClockAttempt, error) {
	return s.updateClock(ctx, id, func(v ClockAttempt) (ClockPhase, *k.ControlReply, error) {
		if v.Phase != ClockDispatched && v.Phase != ClockUncertain {
			return "", nil, ErrConflict
		}
		return ClockUncertain, v.Reply, nil
	})
}

// RecordClockReply accepts an original control-call reply or a recovered receipt.
// A lookup failure must never be repackaged as a pre-admission control refusal.
func (s *Store) RecordClockReply(ctx context.Context, id string, reply *k.ControlReply) (ClockAttempt, error) {
	if reply == nil {
		return ClockAttempt{}, errors.New("clock reply required")
	}
	reply = proto.Clone(reply).(*k.ControlReply)
	return s.updateClock(ctx, id, func(v ClockAttempt) (ClockPhase, *k.ControlReply, error) {
		if err := bridge.ValidateClockControlReply(reply, clockExpectation(v)); err != nil {
			return "", nil, err
		}
		if v.Phase == ClockApplied || v.Phase == ClockRefused {
			if proto.Equal(v.Reply, reply) {
				return v.Phase, v.Reply, nil
			}
			return "", nil, ErrConflict
		}
		if v.Phase != ClockDispatched && v.Phase != ClockUncertain {
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
func (s *Store) updateClock(ctx context.Context, id string, change func(ClockAttempt) (ClockPhase, *k.ControlReply, error)) (ClockAttempt, error) {
	if err := submissionID(id); err != nil {
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
	phase, reply, err := change(old)
	if err != nil {
		return ClockAttempt{}, err
	}
	if phase == old.Phase && proto.Equal(reply, old.Reply) {
		return old, tx.Commit()
	}
	var data []byte
	if reply != nil {
		data, err = clockBinary(reply)
		if err != nil {
			return ClockAttempt{}, err
		}
	}
	if _, err = tx.ExecContext(ctx, "UPDATE clock_attempts SET phase=?,reply=? WHERE request_id=?", phase, data, id); err != nil {
		return ClockAttempt{}, err
	}
	saved, err := loadClock(ctx, tx, id)
	if err != nil {
		return ClockAttempt{}, err
	}
	if err = tx.Commit(); err != nil {
		return ClockAttempt{}, err
	}
	return saved, nil
}
