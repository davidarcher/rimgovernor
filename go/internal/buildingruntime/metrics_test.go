package buildingruntime

import (
	"os"
	"path/filepath"
	"regexp"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
)

// The refusal vocabulary must name every policy.Reason constant, or a refusal
// the metrics cannot name would be filed under "other".
func TestRefusalReasonVocabularyCoversPolicyReasons(t *testing.T) {
	files, err := filepath.Glob("../policy/*.go")
	if err != nil || len(files) == 0 {
		t.Fatalf("policy sources: %v %v", files, err)
	}
	declared := regexp.MustCompile(`(?m)^\s*\w+\s+Reason\s+= "([a-z_]+)"`)
	found := 0
	for _, file := range files {
		source, err := os.ReadFile(file)
		if err != nil {
			t.Fatal(err)
		}
		for _, m := range declared.FindAllSubmatch(source, -1) {
			found++
			if refusalReasonVocabulary.Label(string(m[1])).String() != string(m[1]) {
				t.Errorf("policy reason %q (%s) is missing from refusalReasonVocabulary", m[1], filepath.Base(file))
			}
		}
	}
	if found == 0 {
		t.Fatal("no policy.Reason constants found; the pattern is stale")
	}
}

func TestDispatchAndAdmissionMetricsUseClosedSets(t *testing.T) {
	collect := telemetrytest.Metrics(t)
	recordDispatchOutcome(domain.BuildingAction, executor.Result{}, nil)
	recordDispatchOutcome(domain.BuildingAction, executor.Result{Refused: []policy.Refusal{{Reason: policy.StaleFacts}, {Reason: "free text with a pawn name"}}}, nil)
	recordDispatchOutcome(domain.ActionKind("made_up_kind"), executor.Result{}, os.ErrClosed)
	recordAdmission(true, nil)
	recordAdmission(false, []policy.Refusal{{Reason: policy.GeometryBlocked}})
	recordAdmission(false, nil)
	all := collect()

	outcomes, _ := telemetrytest.SeriesOf(all, telemetry.OutcomesMetric)
	seen := map[[3]string]float64{}
	for _, s := range outcomes {
		seen[[3]string{s.Attrs["action_kind"], s.Attrs["verdict"], s.Attrs["reason"]}] = s.Value
	}
	want := map[[3]string]float64{
		{"building", "applied", "none"}:        1,
		{"building", "refused", "stale_facts"}: 1,
		{"building", "refused", "other"}:       1,
		{"other", "failed", "none"}:            1,
	}
	if len(seen) != len(want) {
		t.Fatalf("outcomes = %v, want %v", seen, want)
	}
	for k, v := range want {
		if seen[k] != v {
			t.Errorf("outcome %v = %v, want %v (all %v)", k, seen[k], v, seen)
		}
	}
	admissions, _ := telemetrytest.SeriesOf(all, telemetry.AdmissionsMetric)
	got := map[[2]string]float64{}
	for _, s := range admissions {
		got[[2]string{s.Attrs["verdict"], s.Attrs["reason"]}] = s.Value
	}
	if got[[2]string{"admitted", "none"}] != 1 || got[[2]string{"refused", "geometry_conflict"}] != 1 || got[[2]string{"refused", "other"}] != 1 || len(got) != 3 {
		t.Fatalf("admissions = %v", got)
	}
}

func TestStepMetricsReadTheClockStepRow(t *testing.T) {
	collect := telemetrytest.Metrics(t)
	recordStepMetrics(StepWake, 40*time.Millisecond, 7*time.Millisecond, map[string]any{
		"stop_latency_ms": 12.0, "controller_pause_ms": 300.0, "stop_pause_s": 1.5, "effective_tps": 240.0,
	})
	recordStepMetrics(StepCause("surprise"), time.Millisecond, 0, map[string]any{})
	all := collect()
	steps, _ := telemetrytest.SeriesOf(all, telemetry.StepMetric)
	causes := map[string]float64{}
	for _, s := range steps {
		causes[s.Attrs["step_cause"]] = s.Value
	}
	if len(causes) != 2 || causes["wake"] != 1 || causes["other"] != 1 {
		t.Fatalf("step causes = %v", causes)
	}
	for _, name := range []string{telemetry.StopLatencyMetric, telemetry.ControllerPauseMetric, telemetry.StopPauseMetric, telemetry.CriticalWaveMetric, telemetry.TicksPerSecondMetric} {
		if series, _ := telemetrytest.SeriesOf(all, name); len(series) != 1 {
			t.Errorf("%s series = %+v, want exactly the first step's", name, series)
		}
	}
	if pause, _ := telemetrytest.SeriesOf(all, telemetry.StopPauseMetric); pause[0].Value != 1 {
		t.Fatalf("stop pause = %+v", pause)
	}
	if tps, _ := telemetrytest.SeriesOf(all, telemetry.TicksPerSecondMetric); tps[0].Value != 240 {
		t.Fatalf("tps = %+v", tps)
	}
}
