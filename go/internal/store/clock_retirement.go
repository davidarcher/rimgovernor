package store

import (
	"context"
	"errors"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

// RetireClockHistory drops only settled or never-dispatched history. The exact
// retained index and retirement watermark commit with deletions, so retirement
// cannot turn an old request into permission for another native dispatch.
func (s *Store) RetireClockHistory(ctx context.Context, expected ClockSequenceState, keepRecent uint32) (ClockRetirement, error) {
	var out ClockRetirement
	if keepRecent > 256 {
		return out, ErrCapacity
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return out, err
	}
	defer tx.Rollback()
	namespace, head, err := loadClockSequence(ctx, tx)
	if err != nil {
		return out, err
	}
	actual := ClockSequenceState{Namespace: namespace, LastAllocated: head.LastAllocated, RetiredThrough: head.RetiredThrough}
	if actual != expected {
		return out, ErrConflict
	}
	rows, err := tx.QueryContext(ctx, "SELECT request_id FROM clock_attempts")
	if err != nil {
		return out, err
	}
	ids := make(map[uint64]string, len(head.Retained))
	for rows.Next() {
		var id string
		if err = rows.Scan(&id); err != nil {
			rows.Close()
			return out, err
		}
		sequence, e := parseClockRequestID(namespace, id)
		if e != nil {
			rows.Close()
			return out, e
		}
		ids[sequence] = id
	}
	if err = errors.Join(rows.Err(), rows.Close()); err != nil {
		return out, err
	}
	attempts := make([]ClockAttempt, len(head.Retained))
	epochs := make([]ClockEpochObligation, len(head.Retained))
	keep := make([]bool, len(head.Retained))
	epochCount := 0
	latestWindow := -1
	for i, sequence := range head.Retained {
		id, ok := ids[sequence]
		if !ok {
			return out, errors.New("retained clock attempt missing")
		}
		v, e := loadClockUnchecked(ctx, tx, id, namespace)
		if e != nil {
			return out, e
		}
		attempts[i] = v
		owned, e := checkClockEpoch(ctx, tx, v)
		if e != nil {
			return out, e
		}
		epochs[i] = owned
		if appliedStartEpoch(v) != nil {
			epochCount++
			if !terminalClockEpoch(owned.Stage) {
				keep[i] = true
			}
		}
		if v.Phase == ClockDispatched || v.Phase == ClockUncertain {
			keep[i] = true
		}
		if v.Intent.Window != nil {
			latestWindow = i
		}
	}
	var count int
	if err = tx.QueryRowContext(ctx, "SELECT count(*) FROM clock_epochs").Scan(&count); err != nil {
		return out, err
	}
	if count != epochCount {
		return out, errors.New("orphaned clock epoch obligation")
	}
	if latestWindow >= 0 {
		keep[latestWindow] = true
	}
	for i := max(0, len(keep)-int(keepRecent)); i < len(keep); i++ {
		keep[i] = true
	}
	// Retained commands keep their original Start provenance, including a terminal
	// epoch. No dependency is inferred merely from a matching session or world.
	for i, v := range attempts {
		if !keep[i] {
			continue
		}
		var original *k.Epoch
		if v.Intent.Command.Renew != nil {
			original = v.Intent.Command.Renew.Original
		}
		if v.Intent.Command.Speed != nil {
			original = v.Intent.Command.Speed.Original
		}
		if original == nil {
			continue
		}
		for j, owned := range epochs {
			if owned.Epoch != nil && proto.Equal(owned.Epoch.Owner, original.Owner) && proto.Equal(owned.Epoch.Origin, original.Origin) {
				keep[j] = true
			}
		}
	}
	retained := make([]uint64, 0, len(head.Retained))
	for i, sequence := range head.Retained {
		if keep[i] {
			retained = append(retained, sequence)
			continue
		}
		id := attempts[i].Intent.RequestID
		if epochs[i].Epoch != nil {
			if _, err = tx.ExecContext(ctx, "DELETE FROM clock_epochs WHERE start_request_id=?", id); err != nil {
				return out, err
			}
			out.RemovedEpochs++
		}
		if _, err = tx.ExecContext(ctx, "DELETE FROM clock_attempts WHERE request_id=?", id); err != nil {
			return out, err
		}
		out.RemovedAttempts++
	}
	head.RetiredThrough = head.LastAllocated
	head.Retained = retained
	if err = saveClockSequence(ctx, tx, head); err != nil {
		return ClockRetirement{}, err
	}
	if _, _, err = loadClockSequence(ctx, tx); err != nil {
		return ClockRetirement{}, err
	}
	out.State = ClockSequenceState{Namespace: namespace, LastAllocated: head.LastAllocated, RetiredThrough: head.RetiredThrough}
	out.RetainedAttempts = len(retained)
	if err = tx.Commit(); err != nil {
		return ClockRetirement{}, err
	}
	return out, nil
}
