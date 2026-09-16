package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func researchSelectRequest(t *testing.T) ResearchSelectRequest {
	t.Helper()
	value, _ := domain.NewResearchSelect("ProjectDef")
	a, _ := domain.NewResearchSelectAction("research-select", value)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	return ResearchSelectRequest{Action: a, Progress: p, Current: s, MinimumTick: 11, Facts: ResearchSelectFacts{Snapshot: s, Tick: 12, Current: domain.Known("")}}
}

func TestResearchSelectAdmission(t *testing.T) {
	r := researchSelectRequest(t)
	original := r.Progress
	for _, prepared := range []bool{false, true} {
		if prepared {
			var err error
			r.Progress, err = r.Progress.Prepare(r.Current, 11)
			if err != nil {
				t.Fatal(err)
			}
		}
		if d := EvaluateResearchSelect(r); !d.Admitted || len(d.Refused) != 0 {
			t.Fatal(d)
		}
	}
	if original.View().Stage != domain.Pending {
		t.Fatal("mutated progress")
	}
}

func TestResearchSelectDefenseHolds(t *testing.T) {
	cases := []struct {
		name   string
		change func(*ResearchSelectRequest)
	}{
		{"zero action", func(r *ResearchSelectRequest) { r.Action = domain.Action{} }},
		{"zero progress", func(r *ResearchSelectRequest) { r.Progress = domain.Progress{} }},
		{"cancelled", func(r *ResearchSelectRequest) { r.Progress, _ = r.Progress.Cancel() }},
		{"minimum", func(r *ResearchSelectRequest) { r.MinimumTick = 13 }},
		{"negative minimum", func(r *ResearchSelectRequest) { r.MinimumTick = -1 }},
		{"zero generation", func(r *ResearchSelectRequest) { r.Current.Native = 0 }},
		{"native", func(r *ResearchSelectRequest) { r.Current.Native++ }},
		{"colony", func(r *ResearchSelectRequest) { r.Current.Colony = "other" }},
		{"load", func(r *ResearchSelectRequest) { r.Current.Load = "other" }},
		{"map", func(r *ResearchSelectRequest) { r.Current.Map++ }},
		{"plan", func(r *ResearchSelectRequest) { r.Current.Plan = "other" }},
		{"revision", func(r *ResearchSelectRequest) { r.Current.Revision++ }},
		{"unknown current", func(r *ResearchSelectRequest) { r.Facts.Current = domain.Unknown[string]() }},
		{"already claimed", func(r *ResearchSelectRequest) { r.Facts.Current = domain.Known("OtherProject") }},
		{"stale tick", func(r *ResearchSelectRequest) { r.Facts.Tick = 5 }},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := researchSelectRequest(t)
			c.change(&r)
			d := EvaluateResearchSelect(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason == "" {
				t.Fatal(d)
			}
		})
	}
}
