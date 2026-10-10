package bridge

import (
	"context"
	"log/slog"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

func TestExplainRetentionDefaultsAreSmall(t *testing.T) {
	r, err := NewExplainRecorder(filepath.Join(t.TempDir(), "explain.jsonl"))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { r.Close() })
	stats := r.FlightRecorderStats()
	if stats.SegmentBytes != 1<<20 || stats.RetentionSegments != 4 {
		t.Fatalf("explain retention = %d x %d, want 1 MiB x 4", stats.SegmentBytes, stats.RetentionSegments)
	}
}

func TestExplanationKindsLandOnlyInExplainRing(t *testing.T) {
	dir := t.TempDir()
	flightPath, explainPath := filepath.Join(dir, "flight.jsonl"), filepath.Join(dir, "explain.jsonl")
	flight, err := NewFlightRecorder(flightPath)
	if err != nil {
		t.Fatal(err)
	}
	explain, err := NewExplainRecorder(explainPath)
	if err != nil {
		t.Fatal(err)
	}
	logger := telemetry.New(telemetry.RouteExplanations(flight, explain))
	ctx := context.Background()
	logger.LogAttrs(ctx, slog.LevelInfo, "step", slog.String(telemetry.KindKey, "planner_step"))
	logger.LogAttrs(ctx, slog.LevelInfo, "move", slog.String(telemetry.KindKey, telemetry.ConcernTransitionKind))
	logger.LogAttrs(ctx, slog.LevelInfo, "go", slog.String(telemetry.KindKey, "dispatch"))
	if err := flight.Close(); err != nil {
		t.Fatal(err)
	}
	if err := explain.Close(); err != nil {
		t.Fatal(err)
	}
	kinds := func(path string) []string {
		rows, err := ReadTimeline(path)
		if err != nil {
			t.Fatal(err)
		}
		var out []string
		for _, row := range rows {
			out = append(out, row.Kind)
		}
		return out
	}
	if got := strings.Join(kinds(flightPath), ","); got != "coverage,planner_step,dispatch" {
		t.Fatalf("flight.jsonl kinds = %s", got)
	}
	if got := strings.Join(kinds(explainPath), ","); got != telemetry.ConcernTransitionKind {
		t.Fatalf("explain.jsonl kinds = %s", got)
	}
}

func TestExplainRingRotatesWithinRetentionAndContinuesSequence(t *testing.T) {
	path := filepath.Join(t.TempDir(), "explain.jsonl")
	open := func(run string) *FlightRecorder {
		r, err := NewExplainRecorder(path, FlightSegmentBytes(minSegmentBytes), FlightSegments(3), FlightRunID(run))
		if err != nil {
			t.Fatal(err)
		}
		return r
	}
	first := open("first")
	var last uint64
	for i := 0; i < 80; i++ {
		seq, err := first.Event(telemetry.ConcernTransitionKind, nil, false, map[string]any{"pad": strings.Repeat("z", 60)})
		if err != nil {
			t.Fatal(err)
		}
		last = seq
	}
	if err := first.Close(); err != nil {
		t.Fatal(err)
	}
	if last != 80 {
		t.Fatalf("explain sequence starts at 1 with no coverage row: last = %d", last)
	}
	files, err := os.ReadDir(filepath.Dir(path))
	if err != nil {
		t.Fatal(err)
	}
	if len(files) != 3 || first.FlightRecorderStats().Rotations == 0 {
		t.Fatalf("ring should hold 3 files after rotating, got %d (rotations %d)", len(files), first.FlightRecorderStats().Rotations)
	}
	second := open("second")
	seq, err := second.Event(telemetry.ConcernTransitionKind, nil, false, nil)
	if err != nil {
		t.Fatal(err)
	}
	if seq != 81 {
		t.Fatalf("sequence after reopen = %d, want 81", seq)
	}
	second.Close()
}
