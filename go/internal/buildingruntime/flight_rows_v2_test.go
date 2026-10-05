package buildingruntime

import (
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// stepDecision files a step as admitted, refused (with the first window
// refusal as the reason), deferred, idle or failed, targeted by its cause.
func TestStepDecisionVerdicts(t *testing.T) {
	refusedOut := ClockSchedulerResult{Attempt: &store.ClockAttempt{Phase: store.ClockRefused}}
	refusedOut.Decision.Refused = []policy.ClockWindowReason{policy.ClockWindowReason("stale_facts")}
	for _, c := range []struct {
		name            string
		out             ClockSchedulerResult
		err             error
		verdict, reason string
	}{
		{"idle", ClockSchedulerResult{}, nil, "idle", "no_window"},
		{"deferred", ClockSchedulerResult{Deferred: true}, nil, "deferred", "deferred"},
		{"admitted", ClockSchedulerResult{Attempt: &store.ClockAttempt{}}, nil, "admitted", "window_admitted"},
		{"refused", refusedOut, nil, "refused", "stale_facts"},
		{"failed", ClockSchedulerResult{}, errors.New("boom"), "failed", "step_error"},
	} {
		d := stepDecision(c.out, c.err, StepLive, 3*time.Millisecond, map[string]any{})
		if d.Kind != "clock_step" || d.Target != "live" || d.Verdict != c.verdict || d.Reason != c.reason || d.Dur != 3*time.Millisecond {
			t.Errorf("%s: %+v", c.name, d)
		}
	}
}
