package startuplabor

import "testing"

func pawnRow(id string, extra map[string]any) map[string]any {
	row := map[string]any{"pawn": map[string]any{"id": id}, "freeColonist": true}
	for k, v := range extra {
		row[k] = v
	}
	return row
}

func TestSamplesFromPawnSnapshot(t *testing.T) {
	reply := map[string]any{"observed": map[string]any{"pawns": []any{
		pawnRow("builder", map[string]any{"job": map[string]any{"defName": "FinishFrame"}}),
		pawnRow("waiting", map[string]any{"job": map[string]any{"defName": "Wait_Wander"}}),
		// No job block and no read issue: the pawn has nothing to do.
		pawnRow("jobless", nil),
		// No job block but a read issue: the job was not read.
		pawnRow("unread", map[string]any{"issues": []any{map[string]any{"code": "unavailable"}}}),
		pawnRow("dead", map[string]any{"dead": true}),
		// An animal is not the labor this accounts for.
		map[string]any{"pawn": map[string]any{"id": "husky"}, "animal": true},
	}}}
	samples := SamplesFromPawnSnapshot(700, reply, false)
	if len(samples) != 4 {
		t.Fatalf("got %d samples, want the four living free colonists: %+v", len(samples), samples)
	}
	want := map[string]Activity{"builder": ActivityWork, "waiting": ActivityIdle, "jobless": ActivityIdle, "unread": ActivityUnknown}
	for _, s := range samples {
		if s.Tick != 700 {
			t.Fatalf("%s sampled at %d", s.Pawn, s.Tick)
		}
		if got := s.Classify(); got != want[s.Pawn] {
			t.Fatalf("%s = %q, want %q", s.Pawn, got, want[s.Pawn])
		}
	}
}

func TestSamplesFromPawnSnapshotIgnoresUnreadableReplies(t *testing.T) {
	for _, reply := range []map[string]any{
		nil,
		{"unavailable": map[string]any{"reason": "no world"}},
		{"observed": map[string]any{}},
	} {
		if s := SamplesFromPawnSnapshot(1, reply, false); len(s) != 0 {
			t.Fatalf("reply %v produced samples %+v", reply, s)
		}
	}
}

func TestSamplesFromPawnSnapshotReadsAWrappedFlightRow(t *testing.T) {
	row := map[string]any{"tool": "rimgovernor/observations_list_pawns", "result": map[string]any{
		"observed": map[string]any{"pawns": []any{pawnRow("builder", map[string]any{"job": map[string]any{"defName": "FinishFrame"}})}}}}
	samples := SamplesFromPawnSnapshot(10, row, false)
	if len(samples) != 1 || samples[0].Classify() != ActivityWork {
		t.Fatalf("samples = %+v", samples)
	}
}
