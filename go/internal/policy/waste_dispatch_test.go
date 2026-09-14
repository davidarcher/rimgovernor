package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func wasteDispatchRequest(t *testing.T) WasteDispatchRequest {
	t.Helper()
	waste, _ := domain.NewWaste("hauler", "item", domain.Cell{X: 1, Z: 1})
	a, _ := domain.NewWasteAction("waste", waste)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Direction: 1, Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	pawn := WastePawnFacts{Pawn: "hauler", SnapshotToken: "pawn-cas", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), PlayerForced: domain.Known(false), QueuedJobs: domain.Known(uint32(0)), ExistingJobDef: domain.Known("")}
	item := WasteItemDispatchFacts{Item: "item", SnapshotToken: "item-cas", Exists: domain.Known(true)}
	return WasteDispatchRequest{Action: a, Progress: p, Current: s, MinimumTick: 11, Facts: WasteDispatchFacts{Snapshot: s, PawnTick: 12, PreviewTick: 13, Pawn: pawn, Item: item, NativeCanTry: domain.Known(true)}}
}

func TestWasteAdmission(t *testing.T) {
	r := wasteDispatchRequest(t)
	original := r.Progress
	for _, prepared := range []bool{false, true} {
		if prepared {
			var err error
			r.Progress, err = r.Progress.Prepare(r.Current, 11)
			if err != nil {
				t.Fatal(err)
			}
		}
		if d := EvaluateWaste(r); !d.Admitted || len(d.Refused) != 0 {
			t.Fatal(d)
		}
	}
	if original.View().Stage != domain.Pending {
		t.Fatal("mutated progress")
	}
}

func TestWasteDefenseHolds(t *testing.T) {
	cases := []struct {
		name   string
		change func(*WasteDispatchRequest)
	}{
		{"zero action", func(r *WasteDispatchRequest) { r.Action = domain.Action{} }},
		{"zero progress", func(r *WasteDispatchRequest) { r.Progress = domain.Progress{} }},
		{"cancelled", func(r *WasteDispatchRequest) { r.Progress, _ = r.Progress.Cancel() }},
		{"minimum", func(r *WasteDispatchRequest) { r.MinimumTick = 13 }},
		{"negative minimum", func(r *WasteDispatchRequest) { r.MinimumTick = -1 }},
		{"reversed interval", func(r *WasteDispatchRequest) { r.Facts.PreviewTick = 11 }},
		{"zero generation", func(r *WasteDispatchRequest) { r.Current.Native = 0 }},
		{"native", func(r *WasteDispatchRequest) { r.Current.Native++ }},
		{"direction", func(r *WasteDispatchRequest) { r.Current.Direction++ }},
		{"colony", func(r *WasteDispatchRequest) { r.Current.Colony = "other" }},
		{"load", func(r *WasteDispatchRequest) { r.Current.Load = "other" }},
		{"map", func(r *WasteDispatchRequest) { r.Current.Map++ }},
		{"plan", func(r *WasteDispatchRequest) { r.Current.Plan = "other" }},
		{"revision", func(r *WasteDispatchRequest) { r.Current.Revision++ }},
		{"pawn CAS", func(r *WasteDispatchRequest) { r.Facts.Pawn.SnapshotToken = "" }},
		{"item CAS", func(r *WasteDispatchRequest) { r.Facts.Item.SnapshotToken = "" }},
		{"wrong pawn", func(r *WasteDispatchRequest) { r.Facts.Pawn.Pawn = "other" }},
		{"wrong item", func(r *WasteDispatchRequest) { r.Facts.Item.Item = "other" }},
		{"pawn dead", func(r *WasteDispatchRequest) { r.Facts.Pawn.Dead = domain.Known(true) }},
		{"pawn downed", func(r *WasteDispatchRequest) { r.Facts.Pawn.Downed = domain.Known(true) }},
		{"pawn drafted", func(r *WasteDispatchRequest) { r.Facts.Pawn.Drafted = domain.Known(true) }},
		{"pawn mental state", func(r *WasteDispatchRequest) { r.Facts.Pawn.MentalState = domain.Known(true) }},
		{"pawn player forced", func(r *WasteDispatchRequest) { r.Facts.Pawn.PlayerForced = domain.Known(true) }},
		{"pawn queued", func(r *WasteDispatchRequest) { r.Facts.Pawn.QueuedJobs = domain.Known(uint32(1)) }},
		{"pawn unknown queue", func(r *WasteDispatchRequest) { r.Facts.Pawn.QueuedJobs = domain.Unknown[uint32]() }},
		{"pawn already hauling", func(r *WasteDispatchRequest) { r.Facts.Pawn.ExistingJobDef = domain.Known("HaulToCell") }},
		{"pawn already hauling to container", func(r *WasteDispatchRequest) { r.Facts.Pawn.ExistingJobDef = domain.Known("HaulToContainer") }},
		{"item unknown exists", func(r *WasteDispatchRequest) { r.Facts.Item.Exists = domain.Unknown[bool]() }},
		{"item vanished", func(r *WasteDispatchRequest) { r.Facts.Item.Exists = domain.Known(false) }},
		{"preview refusal", func(r *WasteDispatchRequest) { r.Facts.NativeCanTry = domain.Known(false) }},
		{"unknown preview", func(r *WasteDispatchRequest) { r.Facts.NativeCanTry = domain.Unknown[bool]() }},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := wasteDispatchRequest(t)
			c.change(&r)
			d := EvaluateWaste(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason == "" {
				t.Fatal(d)
			}
		})
	}
}
