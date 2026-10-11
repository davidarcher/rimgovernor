package nativeaccept

import (
	"errors"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestLastStepFailure(t *testing.T) {
	path := writeLogs(t,
		stepRow("WARN", "rounds: context deadline exceeded", 4100),
		stepRow("INFO", "step done", 4150),
		stepRow("WARN", "Fields: context deadline exceeded", 4200),
		stepRow("INFO", "step done", 4300),
	)
	if got := lastStepFailure(path); got != "2026-09-18T19:46:03.123Z tick=4200 WARN failed step_error: Fields: context deadline exceeded" {
		t.Fatalf("last failure = %q", got)
	}
	if got := lastStepFailure(writeLogs(t, stepRow("INFO", "step done", 4100))); got != "" {
		t.Fatalf("expected no failure, got %q", got)
	}
	if got := lastStepFailure(filepath.Join(t.TempDir(), "missing.jsonl")); got != "" {
		t.Fatalf("a missing recording has no failure, got %q", got)
	}
}

func TestStepStallErrorNamesTheCause(t *testing.T) {
	var err error = &StepStallError{Stall: 90 * time.Second, Families: "field,cooking", LastFailure: "WARN step failed: Fields: context deadline exceeded"}
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
	if msg := (&StepStallError{Stall: time.Minute}).Error(); strings.Contains(msg, ": [") {
		t.Errorf("empty failure must not leave a dangling separator: %q", msg)
	}
}
