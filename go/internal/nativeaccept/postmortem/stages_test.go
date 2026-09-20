package postmortem

import (
	"context"
	"strings"
	"testing"
)

// The stage graph names the bundle the run opened on, each declared
// stage's outcome in chain order and the stage a -through run ended on,
// from a live report or a decoded result.json alike.
func TestStageGraphSection(t *testing.T) {
	live := map[string]any{
		"stages":         []map[string]any{{"name": "feed", "outcome": "hit", "wall_ms": int64(0)}, {"name": "kitchen", "outcome": "captured", "wall_ms": int64(61000)}},
		"staged_from":    map[string]any{"stage": "feed", "captured_at": "2026-09-20T01:02:03Z", "tick": 12345},
		"staged_through": map[string]any{"stage": "kitchen", "outcome": "captured"},
	}
	s := section(t, Collect(context.Background(), t.TempDir(), live), "stage graph")
	if !hasLine(s, "opened on stage feed (captured 2026-09-20T01:02:03Z, tick 12345)", "result.json staged_from") {
		t.Errorf("staged_from line missing: %+v", s)
	}
	if !hasLine(s, "feed:hit -> kitchen:captured 61s", "result.json stages") {
		t.Errorf("chain line missing: %+v", s)
	}
	if !hasLine(s, "ended after stage kitchen (captured): a -through run, the rest of the chain is another run's", "result.json staged_through") {
		t.Errorf("staged_through line missing: %+v", s)
	}
	decoded := map[string]any{"stages": []any{map[string]any{"name": "feed", "outcome": "failed", "wall_ms": 2500.0}}}
	s = section(t, Collect(context.Background(), t.TempDir(), decoded), "stage graph")
	if !hasLine(s, "opened fresh: every stage block ran", "result.json staged_from") || !hasLine(s, "feed:failed 2s", "result.json stages") {
		t.Errorf("decoded graph = %+v", s)
	}
	s = section(t, Collect(context.Background(), t.TempDir(), map[string]any{}), "stage graph")
	if !strings.Contains(s.Note, "no stages") {
		t.Errorf("note = %q", s.Note)
	}
}
