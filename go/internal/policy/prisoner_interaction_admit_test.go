package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func prisonerInteractionRequest(t *testing.T) PrisonerInteractionRequest {
	t.Helper()
	recruit, _ := domain.NewPrisonerInteraction("prisoner", domain.PrisonerInteractionRecruit)
	a, _ := domain.NewPrisonerInteractionAction("prisoner-interaction-1", recruit)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Direction: 1, Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	pawn := PrisonerFacts{Pawn: "prisoner", SnapshotToken: "prisoner-cas", Dead: domain.Known(false), Prisoner: domain.Known(true), Recruitable: domain.Known(true), CurrentInteraction: domain.Known(domain.PrisonerInteractionMaintain)}
	return PrisonerInteractionRequest{Action: a, Progress: p, Current: s, MinimumTick: 11, Facts: PrisonerInteractionFacts{Snapshot: s, PawnTick: 12, PreviewTick: 13, Pawn: pawn, NativeCanTry: domain.Known(true)}}
}

func TestPrisonerInteractionAdmission(t *testing.T) {
	r := prisonerInteractionRequest(t)
	original := r.Progress
	for _, prepared := range []bool{false, true} {
		if prepared {
			var err error
			r.Progress, err = r.Progress.Prepare(r.Current, 11)
			if err != nil {
				t.Fatal(err)
			}
		}
		if d := EvaluatePrisonerInteraction(r); !d.Admitted || len(d.Refused) != 0 {
			t.Fatal(d)
		}
	}
	if original.View().Stage != domain.Pending {
		t.Fatal("mutated progress")
	}
}

func TestPrisonerInteractionMaintainAdmissionDoesNotRequireRecruitable(t *testing.T) {
	maintain, _ := domain.NewPrisonerInteraction("prisoner", domain.PrisonerInteractionMaintain)
	a, _ := domain.NewPrisonerInteractionAction("prisoner-interaction-1", maintain)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Direction: 1, Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	pawn := PrisonerFacts{Pawn: "prisoner", SnapshotToken: "prisoner-cas", Dead: domain.Known(false), Prisoner: domain.Known(true), Recruitable: domain.Unknown[bool](), CurrentInteraction: domain.Known(domain.PrisonerInteractionRecruit)}
	r := PrisonerInteractionRequest{Action: a, Progress: p, Current: s, MinimumTick: 11, Facts: PrisonerInteractionFacts{Snapshot: s, PawnTick: 12, PreviewTick: 13, Pawn: pawn, NativeCanTry: domain.Known(true)}}
	if d := EvaluatePrisonerInteraction(r); !d.Admitted || len(d.Refused) != 0 {
		t.Fatal(d)
	}
}

func TestPrisonerInteractionDefenseHolds(t *testing.T) {
	cases := []struct {
		name   string
		change func(*PrisonerInteractionRequest)
	}{
		{"zero action", func(r *PrisonerInteractionRequest) { r.Action = domain.Action{} }},
		{"zero progress", func(r *PrisonerInteractionRequest) { r.Progress = domain.Progress{} }},
		{"cancelled", func(r *PrisonerInteractionRequest) { r.Progress, _ = r.Progress.Cancel() }},
		{"minimum", func(r *PrisonerInteractionRequest) { r.MinimumTick = 13 }},
		{"negative minimum", func(r *PrisonerInteractionRequest) { r.MinimumTick = -1 }},
		{"reversed interval", func(r *PrisonerInteractionRequest) { r.Facts.PreviewTick = 11 }},
		{"zero generation", func(r *PrisonerInteractionRequest) { r.Current.Native = 0 }},
		{"native", func(r *PrisonerInteractionRequest) { r.Current.Native++ }},
		{"direction", func(r *PrisonerInteractionRequest) { r.Current.Direction++ }},
		{"colony", func(r *PrisonerInteractionRequest) { r.Current.Colony = "other" }},
		{"load", func(r *PrisonerInteractionRequest) { r.Current.Load = "other" }},
		{"map", func(r *PrisonerInteractionRequest) { r.Current.Map++ }},
		{"plan", func(r *PrisonerInteractionRequest) { r.Current.Plan = "other" }},
		{"revision", func(r *PrisonerInteractionRequest) { r.Current.Revision++ }},
		{"prisoner CAS", func(r *PrisonerInteractionRequest) { r.Facts.Pawn.SnapshotToken = "" }},
		{"wrong pawn", func(r *PrisonerInteractionRequest) { r.Facts.Pawn.Pawn = "other" }},
		{"unknown dead", func(r *PrisonerInteractionRequest) { r.Facts.Pawn.Dead = domain.Unknown[bool]() }},
		{"dead", func(r *PrisonerInteractionRequest) { r.Facts.Pawn.Dead = domain.Known(true) }},
		{"unknown prisoner", func(r *PrisonerInteractionRequest) { r.Facts.Pawn.Prisoner = domain.Unknown[bool]() }},
		{"not a prisoner", func(r *PrisonerInteractionRequest) { r.Facts.Pawn.Prisoner = domain.Known(false) }},
		{"unknown current interaction", func(r *PrisonerInteractionRequest) { r.Facts.Pawn.CurrentInteraction = domain.Unknown[domain.PrisonerInteractionMode]() }},
		{"already settled", func(r *PrisonerInteractionRequest) { r.Facts.Pawn.CurrentInteraction = domain.Known(domain.PrisonerInteractionRecruit) }},
		{"unknown recruitable", func(r *PrisonerInteractionRequest) { r.Facts.Pawn.Recruitable = domain.Unknown[bool]() }},
		{"not recruitable", func(r *PrisonerInteractionRequest) { r.Facts.Pawn.Recruitable = domain.Known(false) }},
		{"preview refusal", func(r *PrisonerInteractionRequest) { r.Facts.NativeCanTry = domain.Known(false) }},
		{"unknown preview", func(r *PrisonerInteractionRequest) { r.Facts.NativeCanTry = domain.Unknown[bool]() }},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := prisonerInteractionRequest(t)
			c.change(&r)
			d := EvaluatePrisonerInteraction(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason == "" {
				t.Fatal(d)
			}
		})
	}
}
