package light

import (
	"context"
	"errors"
	"testing"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func TestReleaseWaitsForEnabledLightingReview(t *testing.T) {
	// The failed smoke run completed construction, then read a disabled
	// review with no lighting latch while authority was being recovered.
	reviews := []store.RoutineReview{
		{Revision: 5},
		{Revision: 6, Enabled: true, Latches: policy.RoutineLatches{Lighting: []string{"stove"}}},
		{Revision: 7, Enabled: true},
	}
	for i, review := range reviews {
		if got := lightingReleased(review, "stove"); got != (i == 2) {
			t.Fatalf("review %d: released=%v", review.Revision, got)
		}
	}
}

func TestAuthorityWaitSurvivesTimeoutAndManualUntilRecovery(t *testing.T) {
	reads := 0
	get := func(_, path string) (map[string]any, error) {
		if path == "/api/player/clock" {
			return map[string]any{"holds": []any{}}, nil
		}
		reads++
		switch reads {
		case 1:
			return nil, context.DeadlineExceeded
		case 2:
			return map[string]any{"mode": "manual"}, nil
		default:
			return map[string]any{"mode": "automate"}, nil
		}
	}
	if err := waitRunning(context.Background(), get, na.Wait{Ceiling: time.Minute, Interval: time.Millisecond}); err != nil {
		t.Fatal(err)
	}
	if reads != 3 {
		t.Fatalf("state reads=%d, want timeout, Manual, then Auto", reads)
	}
}

func TestAuthorityWaitFailsWhenServiceExits(t *testing.T) {
	exited := errors.New("service exited")
	err := waitRunning(context.Background(), func(_, _ string) (map[string]any, error) {
		t.Fatal("must not read an exited service")
		return nil, nil
	}, na.Wait{Terminal: func() error { return exited }})
	if !errors.Is(err, exited) {
		t.Fatalf("got %v, want service exit", err)
	}
}
