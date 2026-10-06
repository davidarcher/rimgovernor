package main

import (
	"bytes"
	"encoding/json"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func row(t *testing.T, kind string, tick int64, verdict, reason string, attrs map[string]any) nativeaccept.FlightRow {
	t.Helper()
	payload := map[string]any{"verdict": verdict, "reason": reason, "attrs": attrs}
	data, err := json.Marshal(payload)
	if err != nil {
		t.Fatal(err)
	}
	var round map[string]any
	if err := json.Unmarshal(data, &round); err != nil {
		t.Fatal(err)
	}
	return nativeaccept.FlightRow{Kind: kind, Tick: tick, Payload: round}
}

func TestSummarizeTalliesClassesAndBatches(t *testing.T) {
	div := func(id, class string, expected bool) map[string]any {
		return map[string]any{"id": id, "class": class, "expected": expected, "old": "admitted", "queue": "held:route_unsafe"}
	}
	rows := []nativeaccept.FlightRow{
		row(t, "recovery_shadow", 10, "expected", "none", map[string]any{"divergences": []any{div("a", "batch", true), div("b", "demand_hold_gone", true)}}),
		row(t, "recovery_shadow", 20, "diverged", "queue_holds", map[string]any{"divergences": []any{div("c", "queue_holds", false), div("d", "batch", true)}}),
		row(t, "recovery_shadow", 30, "agree", "none", map[string]any{"divergences": []any{}}),
		row(t, "recovery_batch", 20, "planned", "roof", nil),
		row(t, "recovery_batch", 30, "held", "none", nil),
		row(t, "dispatch", 30, "", "", nil),
	}
	s := Summarize(rows)
	if s.Reviews != 3 || s.Verdicts["agree"] != 1 || s.Unexplained() != 1 || s.Batches["planned/roof"] != 1 {
		t.Fatalf("summary = %+v", s)
	}
	if first := s.Classes[0]; first.Class != "queue_holds" || first.Expected || len(first.Examples) != 1 || first.Examples[0].Tick != 20 {
		t.Fatalf("unexplained classes sort first with examples: %+v", s.Classes)
	}
	var out bytes.Buffer
	s.Print(&out)
	for _, want := range []string{"3 reviews, 1 unexplained", "UNEXPLAINED", "tick 20 c:", "expected    batch", "planned/roof"} {
		if !strings.Contains(out.String(), want) {
			t.Fatalf("report lacks %q:\n%s", want, out.String())
		}
	}
}
