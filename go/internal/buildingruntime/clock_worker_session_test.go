package buildingruntime

import (
	"context"
	"errors"
	"testing"
)

type clockStopFunc func(context.Context) error

func (f clockStopFunc) Stop(ctx context.Context) error { return f(ctx) }

func TestClockWorkerSessionRetainsOwnerUntilJoinedCleanup(t *testing.T) {
	t.Parallel()
	_, db, native, _ := clockCoreFixture(t)
	s, _, dir := newClockSessionTest(t, db, native)
	calls := 0
	blocked := errors.New("worker still draining")
	worker := clockStopFunc(func(ctx context.Context) error {
		calls++
		if err := s.disableClockWorker(); err != nil {
			t.Fatal(err)
		}
		if err := s.attachClockWorker(clockStopFunc(func(context.Context) error { return nil })); err == nil {
			t.Fatal("attached during close")
		}
		if calls == 1 {
			return blocked
		}
		return s.CleanupClock(ctx)
	})
	if err := s.attachClockWorker(worker); err != nil {
		t.Fatal(err)
	}
	if err := s.attachClockWorker(worker); err == nil {
		t.Fatal("duplicate worker attached")
	}
	if err := s.Close(context.Background()); !errors.Is(err, blocked) {
		t.Fatal(err)
	}
	if owner, err := AcquireProfile(context.Background(), dir); err == nil {
		owner.Close()
		t.Fatal("released owner before worker joined")
	}
	if s.clock.stopped {
		t.Fatal("stopped coordinator before worker finished")
	}
	if err := s.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	if calls != 2 || !s.clock.stopped {
		t.Fatal(calls, s.clock.stopped)
	}
	owner, err := AcquireProfile(context.Background(), dir)
	if err != nil {
		t.Fatal(err)
	}
	if err = owner.Close(); err != nil {
		t.Fatal(err)
	}
	if err = s.attachClockWorker(worker); err == nil {
		t.Fatal("attached after close")
	}
}
