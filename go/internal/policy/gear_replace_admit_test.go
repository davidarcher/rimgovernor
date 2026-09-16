package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func gearReplaceRequest(t *testing.T) GearReplaceRequest {
	t.Helper()
	replace, _ := domain.NewGearReplace("unarmed", "thing", "Apparel_Parka")
	a, _ := domain.NewGearReplaceAction("gear-replace", replace)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	pawn := GearReplacePawnFacts{Pawn: "unarmed", SnapshotToken: "pawn-cas", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), ExistingJobDef: domain.Known("")}
	return GearReplaceRequest{Action: a, Progress: p, Current: s, MinimumTick: 11, Facts: GearReplaceFacts{Snapshot: s, PawnTick: 12, PreviewTick: 13, Pawn: pawn, ThingSnapshotToken: "thing-cas", LoadoutToken: "loadout-cas", NativeCanTry: domain.Known(true)}}
}

func TestGearReplaceAdmission(t *testing.T) {
	r := gearReplaceRequest(t)
	original := r.Progress
	for _, prepared := range []bool{false, true} {
		if prepared {
			var err error
			r.Progress, err = r.Progress.Prepare(r.Current, 11)
			if err != nil {
				t.Fatal(err)
			}
		}
		if d := EvaluateGearReplace(r); !d.Admitted || len(d.Refused) != 0 {
			t.Fatal(d)
		}
	}
	if original.View().Stage != domain.Pending {
		t.Fatal("mutated progress")
	}
}

func TestGearReplaceDefenseHolds(t *testing.T) {
	cases := []struct {
		name   string
		change func(*GearReplaceRequest)
	}{
		{"zero action", func(r *GearReplaceRequest) { r.Action = domain.Action{} }},
		{"zero progress", func(r *GearReplaceRequest) { r.Progress = domain.Progress{} }},
		{"cancelled", func(r *GearReplaceRequest) { r.Progress, _ = r.Progress.Cancel() }},
		{"minimum", func(r *GearReplaceRequest) { r.MinimumTick = 13 }},
		{"negative minimum", func(r *GearReplaceRequest) { r.MinimumTick = -1 }},
		{"reversed interval", func(r *GearReplaceRequest) { r.Facts.PreviewTick = 11 }},
		{"zero generation", func(r *GearReplaceRequest) { r.Current.Native = 0 }},
		{"native", func(r *GearReplaceRequest) { r.Current.Native++ }},
		{"colony", func(r *GearReplaceRequest) { r.Current.Colony = "other" }},
		{"load", func(r *GearReplaceRequest) { r.Current.Load = "other" }},
		{"map", func(r *GearReplaceRequest) { r.Current.Map++ }},
		{"plan", func(r *GearReplaceRequest) { r.Current.Plan = "other" }},
		{"revision", func(r *GearReplaceRequest) { r.Current.Revision++ }},
		{"pawn CAS", func(r *GearReplaceRequest) { r.Facts.Pawn.SnapshotToken = "" }},
		{"thing CAS", func(r *GearReplaceRequest) { r.Facts.ThingSnapshotToken = "" }},
		{"loadout CAS", func(r *GearReplaceRequest) { r.Facts.LoadoutToken = "" }},
		{"wrong pawn", func(r *GearReplaceRequest) { r.Facts.Pawn.Pawn = "other" }},
		{"pawn dead", func(r *GearReplaceRequest) { r.Facts.Pawn.Dead = domain.Known(true) }},
		{"pawn downed", func(r *GearReplaceRequest) { r.Facts.Pawn.Downed = domain.Known(true) }},
		{"pawn drafted", func(r *GearReplaceRequest) { r.Facts.Pawn.Drafted = domain.Known(true) }},
		{"pawn mental state", func(r *GearReplaceRequest) { r.Facts.Pawn.MentalState = domain.Known(true) }},
		{"pawn unknown existing job", func(r *GearReplaceRequest) { r.Facts.Pawn.ExistingJobDef = domain.Unknown[string]() }},
		{"preview refusal", func(r *GearReplaceRequest) { r.Facts.NativeCanTry = domain.Known(false) }},
		{"unknown preview", func(r *GearReplaceRequest) { r.Facts.NativeCanTry = domain.Unknown[bool]() }},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := gearReplaceRequest(t)
			c.change(&r)
			d := EvaluateGearReplace(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason == "" {
				t.Fatal(d)
			}
		})
	}
}
