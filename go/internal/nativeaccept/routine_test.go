package nativeaccept

import (
	"testing"
)

func TestRoutineWaitReportsInterruptionWithoutAcknowledgingOrReacquiring(t *testing.T) {
	var calls [][2]string
	http := func(method, path string) (map[string]any, error) {
		calls = append(calls, [2]string{method, path})
		if path == "/api/state" {
			return map[string]any{"mode": "manual"}, nil
		}
		return map[string]any{"holds": []any{map[string]any{"kind": "interruption"}}}, nil
	}
	if err := AssertRoutineRunning(http); err == nil {
		t.Fatal("expected an interruption error")
	}
	want := [][2]string{{"GET", "/api/state"}, {"GET", "/api/player/clock"}}
	if len(calls) != len(want) || calls[0] != want[0] || calls[1] != want[1] {
		t.Fatalf("unexpected calls: %v", calls)
	}
	running := func(method, path string) (map[string]any, error) {
		if method != "GET" || path != "/api/state" {
			t.Fatalf("unexpected call: %s %s", method, path)
		}
		return map[string]any{"mode": "automate"}, nil
	}
	if err := AssertRoutineRunning(running); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
}
