package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func homeCoverageRequest(t *testing.T) HomeCoverageRequest {
	t.Helper()
	coverage, _ := domain.NewHomeCoverage("building-1", "shape-token")
	a, _ := domain.NewHomeCoverageAction("home-coverage-1", coverage)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	facts := HomeCoverageFacts{
		Snapshot:        s,
		ObservationTick: 12,
		PreviewTick:     13,
		CurrentShape:    domain.Known("shape-token"),
		Revision:        domain.Known(int64(4)),
		Excluded:        domain.Known(int64(0)),
		Missing:         domain.Known(int64(3)),
		NativeCanTry:    domain.Known(true),
	}
	return HomeCoverageRequest{Action: a, Progress: p, Current: s, MinimumTick: 11, Facts: facts}
}

func TestHomeCoverageAdmission(t *testing.T) {
	r := homeCoverageRequest(t)
	original := r.Progress
	for _, prepared := range []bool{false, true} {
		if prepared {
			var err error
			r.Progress, err = r.Progress.Prepare(r.Current, 11)
			if err != nil {
				t.Fatal(err)
			}
		}
		if d := EvaluateHomeCoverage(r); !d.Admitted || len(d.Refused) != 0 {
			t.Fatal(d)
		}
	}
	if original.View().Stage != domain.Pending {
		t.Fatal("mutated progress")
	}
}

func TestHomeCoverageDefenseHolds(t *testing.T) {
	cases := []struct {
		name   string
		change func(*HomeCoverageRequest)
	}{
		{"zero action", func(r *HomeCoverageRequest) { r.Action = domain.Action{} }},
		{"zero progress", func(r *HomeCoverageRequest) { r.Progress = domain.Progress{} }},
		{"cancelled", func(r *HomeCoverageRequest) { r.Progress, _ = r.Progress.Cancel() }},
		{"minimum", func(r *HomeCoverageRequest) { r.MinimumTick = 14 }},
		{"negative minimum", func(r *HomeCoverageRequest) { r.MinimumTick = -1 }},
		{"reversed interval", func(r *HomeCoverageRequest) { r.Facts.PreviewTick = 11 }},
		{"observation row outrun", func(r *HomeCoverageRequest) { r.Facts.PreviewTick = 12 + domain.PlanningTickTolerance + 1 }},
		{"prepared past preview", func(r *HomeCoverageRequest) { r.Progress, _ = r.Progress.Prepare(r.Current, 14) }},
		{"zero generation", func(r *HomeCoverageRequest) { r.Current.Native = 0 }},
		{"native", func(r *HomeCoverageRequest) { r.Current.Native++ }},
		{"colony", func(r *HomeCoverageRequest) { r.Current.Colony = "other" }},
		{"load", func(r *HomeCoverageRequest) { r.Current.Load = "other" }},
		{"map", func(r *HomeCoverageRequest) { r.Current.Map++ }},
		{"plan", func(r *HomeCoverageRequest) { r.Current.Plan = "other" }},
		{"revision", func(r *HomeCoverageRequest) { r.Current.Revision++ }},
		{"unknown shape", func(r *HomeCoverageRequest) { r.Facts.CurrentShape = domain.Unknown[string]() }},
		{"shape changed", func(r *HomeCoverageRequest) { r.Facts.CurrentShape = domain.Known("different-shape") }},
		{"unknown excluded", func(r *HomeCoverageRequest) { r.Facts.Excluded = domain.Unknown[int64]() }},
		{"invalid excluded count", func(r *HomeCoverageRequest) { r.Facts.Excluded = domain.Known(int64(4)) }},
		{"negative missing", func(r *HomeCoverageRequest) { r.Facts.Missing = domain.Known(int64(-1)) }},
		{"negative revision", func(r *HomeCoverageRequest) { r.Facts.Revision = domain.Known(int64(-1)) }},
		{"unknown missing", func(r *HomeCoverageRequest) { r.Facts.Missing = domain.Unknown[int64]() }},
		{"nothing missing", func(r *HomeCoverageRequest) { r.Facts.Missing = domain.Known(int64(0)) }},
		{"unknown revision", func(r *HomeCoverageRequest) { r.Facts.Revision = domain.Unknown[int64]() }},
		{"preview refusal", func(r *HomeCoverageRequest) { r.Facts.NativeCanTry = domain.Known(false) }},
		{"unknown preview", func(r *HomeCoverageRequest) { r.Facts.NativeCanTry = domain.Unknown[bool]() }},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := homeCoverageRequest(t)
			c.change(&r)
			d := EvaluateHomeCoverage(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason == "" {
				t.Fatal(d)
			}
		})
	}
}

// TestHomeCoverageAdmitsCachedPawnRowBehindPreparedTick is the #306 shape (#323):
// the executor's second inspection under a running window is served the
// observation row the first read (tick 12) cached, while the first preview (tick
// 13) prepared the action and raised the minimum; the second preview (tick
// 15) anchors the admission, so the older row is fresh evidence.
func TestHomeCoverageAdmitsCachedPawnRowBehindPreparedTick(t *testing.T) {
	r := homeCoverageRequest(t)
	var err error
	if r.Progress, err = r.Progress.Prepare(r.Current, 13); err != nil {
		t.Fatal(err)
	}
	r.MinimumTick, r.Facts.ObservationTick, r.Facts.PreviewTick = 13, 12, 15
	if d := EvaluateHomeCoverage(r); !d.Admitted || len(d.Refused) != 0 {
		t.Fatal(d)
	}
}
