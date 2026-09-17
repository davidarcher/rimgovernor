package sustainedfood

import (
	"errors"
	"strings"
	"testing"
	"time"
)

func TestLastStepFailure(t *testing.T) {
	log := strings.Join([]string{
		"[clock-scheduler] reached stepPlanners",
		"[clock-worker] step failed: routine review: context deadline exceeded",
		"[clock-scheduler] step done: err=<nil>",
		"  [clock-worker] step failed: Fields: context deadline exceeded  ",
		"[clock-scheduler] EvaluateClockWindow: work=false",
	}, "\n")
	if got := LastStepFailure(strings.NewReader(log)); got != "[clock-worker] step failed: Fields: context deadline exceeded" {
		t.Fatalf("last failure = %q", got)
	}
	if got := LastStepFailure(strings.NewReader("[clock-scheduler] step done: err=<nil>\n")); got != "" {
		t.Fatalf("expected no failure, got %q", got)
	}
}

func TestStepStallErrorNamesTheCause(t *testing.T) {
	err := stepStall(90*time.Second, "field,cooking", "[clock-worker] step failed: Fields: context deadline exceeded")
	var stall *StepStallError
	if !errors.As(err, &stall) {
		t.Fatalf("not a StepStallError: %v", err)
	}
	msg := err.Error()
	for _, want := range []string{"1m30s", `"field,cooking"`, "Fields: context deadline exceeded", "-families"} {
		if !strings.Contains(msg, want) {
			t.Errorf("message %q lacks %q", msg, want)
		}
	}
	if msg := stepStall(time.Minute, "", "").Error(); strings.Contains(msg, ": [") {
		t.Errorf("empty failure must not leave a dangling separator: %q", msg)
	}
}
