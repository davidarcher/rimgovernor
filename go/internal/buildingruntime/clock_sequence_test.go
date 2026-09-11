package buildingruntime

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func TestClockSequenceWindowPreparedReplayAndLogicalMismatch(t *testing.T) {
	for _, mismatch := range []bool{false, true} {
		t.Run(map[bool]string{false: "exact", true: "mismatched-key"}[mismatch], func(t *testing.T) {
			s, native := schedulerFixture(t)
			lease := s.session.clock.leases
			s.session.clock.leases = clockCoreLease{func(domain.GenerationSnapshot) (string, error) { return "", executor.ErrHeld }}
			first, err := s.Step(context.Background())
			if err == nil || first.Attempt == nil || first.Attempt.Phase != store.ClockPrepared || first.Attempt.Intent.Key == "" {
				t.Fatal(first, err)
			}
			s.session.clock.leases = lease
			if mismatch {
				altered := first.Attempt.Intent
				altered.RequestID = clockTestNextID(t, s.player.journal)
				start := *altered.Command.Start
				start.LeaseMS++
				altered.Command.Start = &start
				if _, _, err = s.player.journal.PrepareClock(context.Background(), altered); err != nil {
					t.Fatal(err)
				}
			}
			before, err := s.player.journal.ReadClockSequence(context.Background())
			if err != nil {
				t.Fatal(err)
			}
			second, err := s.Step(context.Background())
			after, e := s.player.journal.ReadClockSequence(context.Background())
			if e != nil || before != after {
				t.Fatal("replay allocated sequence", before, after, e)
			}
			if mismatch {
				if !errors.Is(err, executor.ErrEvidence) || native.writes != 0 {
					t.Fatal(second, err, native.writes)
				}
				return
			}
			if err != nil || second.Attempt == nil || second.Attempt.Intent.RequestID != first.Attempt.Intent.RequestID || native.writes != 1 {
				t.Fatal(second, err)
			}
		})
	}
}
func TestClockSequenceRenewUsesGlobalAllocation(t *testing.T) {
	s, _, _, start := renewalFixture(t)
	// An unrelated inert command consumes the global sequence. Renewal must not
	// derive a reusable identifier by counting only this epoch's renewal rows.
	inert := start.Intent
	inert.Key = ""
	inert.Window = nil
	inert.RequestID = clockTestNextID(t, s.player.journal)
	if _, _, err := s.player.journal.PrepareClock(context.Background(), inert); err != nil {
		t.Fatal(err)
	}
	expected := clockTestNextID(t, s.player.journal)
	result, err := s.RenewEpoch(context.Background())
	if err != nil || !result.Renewed || result.Attempt.Intent.RequestID != expected {
		t.Fatal(result, err)
	}
}

// This clock inserts a competing command between sequence selection and the
// coordinator's atomic Prepare. The scheduler must return rather than resend.
type sequenceCompetingClock struct{ before func() }

func (c *sequenceCompetingClock) Now() time.Time {
	if c.before != nil {
		f := c.before
		c.before = nil
		f()
	}
	return (boundaryClock{}).Now()
}
func TestClockSequenceCASConflictWaitsForNextStep(t *testing.T) {
	s, native := schedulerFixture(t)
	s.session.clock.clock = &sequenceCompetingClock{before: func() {
		inert := store.ClockIntent{RequestID: clockTestNextID(t, s.player.journal), Snapshot: s.session.State().Snapshot}
		start := s.config.Start
		inert.Command.Start = &start
		if _, _, err := s.player.journal.PrepareClock(context.Background(), inert); err != nil {
			t.Fatal(err)
		}
	}}
	first, err := s.Step(context.Background())
	if !errors.Is(err, store.ErrConflict) || !errors.Is(err, executor.ErrHeld) || native.writes != 0 {
		t.Fatal(first, err, native.writes)
	}
	expected := clockTestNextID(t, s.player.journal)
	second, err := s.Step(context.Background())
	if err != nil || second.Attempt == nil || second.Attempt.Intent.RequestID != expected || native.writes != 1 {
		t.Fatal(second, err, native.writes)
	}
}
