package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func haulRequest(t *testing.T) HaulRequest {
	t.Helper()
	haul, _ := domain.NewHaul("hauler", "thing", "MealSimple", domain.Cell{X: 1, Z: 1})
	a, _ := domain.NewHaulAction("haul", haul)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	pawn := HaulPawnFacts{Pawn: "hauler", SnapshotToken: "pawn-cas", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), ExistingJobDef: domain.Known("")}
	return HaulRequest{Action: a, Progress: p, Current: s, MinimumTick: 11, Facts: HaulFacts{Snapshot: s, PawnTick: 12, PreviewTick: 13, Pawn: pawn, ThingSnapshotToken: "thing-cas", NativeCanTry: domain.Known(true)}}
}

func TestHaulAdmission(t *testing.T) {
	r := haulRequest(t)
	original := r.Progress
	for _, prepared := range []bool{false, true} {
		if prepared {
			var err error
			r.Progress, err = r.Progress.Prepare(r.Current, 11)
			if err != nil {
				t.Fatal(err)
			}
		}
		if d := EvaluateHaul(r); !d.Admitted || len(d.Refused) != 0 {
			t.Fatal(d)
		}
	}
	if original.View().Stage != domain.Pending {
		t.Fatal("mutated progress")
	}
}

func TestHaulDefenseHolds(t *testing.T) {
	cases := []struct {
		name   string
		change func(*HaulRequest)
	}{
		{"zero action", func(r *HaulRequest) { r.Action = domain.Action{} }},
		{"zero progress", func(r *HaulRequest) { r.Progress = domain.Progress{} }},
		{"cancelled", func(r *HaulRequest) { r.Progress, _ = r.Progress.Cancel() }},
		{"minimum", func(r *HaulRequest) { r.MinimumTick = 13 }},
		{"negative minimum", func(r *HaulRequest) { r.MinimumTick = -1 }},
		{"reversed interval", func(r *HaulRequest) { r.Facts.PreviewTick = 11 }},
		{"zero generation", func(r *HaulRequest) { r.Current.Native = 0 }},
		{"native", func(r *HaulRequest) { r.Current.Native++ }},
		{"colony", func(r *HaulRequest) { r.Current.Colony = "other" }},
		{"load", func(r *HaulRequest) { r.Current.Load = "other" }},
		{"map", func(r *HaulRequest) { r.Current.Map++ }},
		{"plan", func(r *HaulRequest) { r.Current.Plan = "other" }},
		{"revision", func(r *HaulRequest) { r.Current.Revision++ }},
		{"pawn CAS", func(r *HaulRequest) { r.Facts.Pawn.SnapshotToken = "" }},
		{"thing CAS", func(r *HaulRequest) { r.Facts.ThingSnapshotToken = "" }},
		{"wrong pawn", func(r *HaulRequest) { r.Facts.Pawn.Pawn = "other" }},
		{"pawn dead", func(r *HaulRequest) { r.Facts.Pawn.Dead = domain.Known(true) }},
		{"pawn downed", func(r *HaulRequest) { r.Facts.Pawn.Downed = domain.Known(true) }},
		{"pawn drafted", func(r *HaulRequest) { r.Facts.Pawn.Drafted = domain.Known(true) }},
		{"pawn mental state", func(r *HaulRequest) { r.Facts.Pawn.MentalState = domain.Known(true) }},
		{"pawn already hauling cell", func(r *HaulRequest) { r.Facts.Pawn.ExistingJobDef = domain.Known("HaulToCell") }},
		{"pawn already hauling container", func(r *HaulRequest) { r.Facts.Pawn.ExistingJobDef = domain.Known("HaulToContainer") }},
		{"preview refusal", func(r *HaulRequest) { r.Facts.NativeCanTry = domain.Known(false) }},
		{"unknown preview", func(r *HaulRequest) { r.Facts.NativeCanTry = domain.Unknown[bool]() }},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := haulRequest(t)
			c.change(&r)
			d := EvaluateHaul(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason == "" {
				t.Fatal(d)
			}
		})
	}
}

// A thing the cell-scoped read no longer finds refuses with thing_absent even
// though every other fact is stale-free, so the journal shows why the haul is
// held; a known-present or unknown presence changes nothing else.
func TestHaulRefusesAbsentThing(t *testing.T) {
	r := haulRequest(t)
	r.Facts.ThingSnapshotToken, r.Facts.ThingPresent, r.Facts.NativeCanTry = "", domain.Known(false), domain.Known(false)
	d := EvaluateHaul(r)
	if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason != ThingAbsent {
		t.Fatal(d)
	}
	r = haulRequest(t)
	r.Facts.ThingPresent = domain.Known(true)
	if d := EvaluateHaul(r); !d.Admitted {
		t.Fatal(d)
	}
}
