package telemetry_test

import (
	"fmt"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
)

func TestVocabularyCollapsesUnknownValues(t *testing.T) {
	voc := telemetry.Words("a", "b")
	if voc.Label("a").String() != "a" || voc.Label("pawn-17").String() != telemetry.Other {
		t.Fatal("a word outside the set must collapse to other")
	}
	pred := telemetry.Where(func(s string) bool { return s == "x" })
	if pred.Label("x").String() != "x" || pred.Label("y").String() != telemetry.Other {
		t.Fatal("a predicate vocabulary must collapse its rejects")
	}
}

// Every instrument is named, carries its unit and, for a histogram, the
// documented explicit boundaries; and no attribute can take a value outside its
// closed set however many distinct inputs are offered.
func TestMetricsInstrumentsUnitsBucketsAndCardinality(t *testing.T) {
	collect := telemetrytest.Metrics(t)
	tools := telemetry.Words("rimgovernor/known")
	for i := range 500 {
		junk := fmt.Sprintf("junk-%d", i)
		tool := tools.Label(junk)
		telemetry.NativePhase(tool, junk, 3)
		telemetry.NativeCall(tool, junk)
		telemetry.SchedulerStep(telemetry.Words("timer").Label(junk), 3)
		telemetry.ActionOutcome(telemetry.Words("building").Label(junk), junk, telemetry.Words("not_ready").Label(junk))
		telemetry.Admission(junk, telemetry.Words("not_ready").Label(junk))
	}
	telemetry.NativePhase(tools.Label("rimgovernor/known"), telemetry.PhaseCall, 3)
	telemetry.StopLatency(1)
	telemetry.ControllerPause(1)
	telemetry.StopPause(1)
	telemetry.CriticalWave(1)
	telemetry.TicksPerSecond(60)
	all := collect()

	type want struct {
		name, unit string
		bounds     []float64
		allowed    map[string][]string
		series     int
	}
	other := []string{telemetry.Other}
	for _, w := range []want{
		{telemetry.NativePhaseMetric, "ms", telemetry.NativeBuckets, map[string][]string{"native_tool": {"rimgovernor/known", telemetry.Other}, "phase": {telemetry.PhaseCall, telemetry.Other}}, 2},
		{telemetry.NativeCallsMetric, "{call}", nil, map[string][]string{"native_tool": {telemetry.Other}, "outcome": other}, 1},
		{telemetry.StepMetric, "ms", telemetry.SchedulerBuckets, map[string][]string{"step_cause": other}, 1},
		{telemetry.StopLatencyMetric, "ms", telemetry.SchedulerBuckets, map[string][]string{}, 1},
		{telemetry.ControllerPauseMetric, "ms", telemetry.SchedulerBuckets, map[string][]string{}, 1},
		{telemetry.StopPauseMetric, "ms", telemetry.SchedulerBuckets, map[string][]string{}, 1},
		{telemetry.CriticalWaveMetric, "ms", telemetry.SchedulerBuckets, map[string][]string{}, 1},
		{telemetry.TicksPerSecondMetric, "{tick}/s", nil, map[string][]string{}, 1},
		{telemetry.OutcomesMetric, "{action}", nil, map[string][]string{"action_kind": other, "verdict": other, "reason": other}, 1},
		{telemetry.AdmissionsMetric, "{decision}", nil, map[string][]string{"verdict": other, "reason": other}, 1},
	} {
		series, unit := telemetrytest.SeriesOf(all, w.name)
		if unit != w.unit {
			t.Errorf("%s unit = %q, want %q", w.name, unit, w.unit)
		}
		if len(series) != w.series {
			t.Errorf("%s has %d series, want %d: %+v", w.name, len(series), w.series, series)
		}
		for _, s := range series {
			if len(s.Attrs) != len(w.allowed) {
				t.Errorf("%s attribute keys = %v, want %d keys", w.name, s.Attrs, len(w.allowed))
			}
			for key, value := range s.Attrs {
				if !slices.Contains(w.allowed[key], value) {
					t.Errorf("%s %s=%q is outside its closed set %v", w.name, key, value, w.allowed[key])
				}
			}
			if w.bounds != nil && !slices.Equal(s.Bounds, w.bounds) {
				t.Errorf("%s bounds = %v, want %v", w.name, s.Bounds, w.bounds)
			}
		}
	}
}

func TestBucketBoundariesAreAscending(t *testing.T) {
	for name, bounds := range map[string][]float64{"native": telemetry.NativeBuckets, "scheduler": telemetry.SchedulerBuckets} {
		if !slices.IsSorted(bounds) || bounds[0] <= 0 {
			t.Errorf("%s buckets %v must ascend from a positive value", name, bounds)
		}
	}
}
