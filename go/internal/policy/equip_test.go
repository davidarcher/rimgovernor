package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func equipRequest(t *testing.T) EquipRequest {
	t.Helper()
	equip, _ := domain.NewEquip("unarmed", "thing", "Gun_Revolver", domain.Cell{X: 1, Z: 1})
	a, _ := domain.NewEquipAction("equip", equip)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Direction: 1, Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	pawn := EquipPawnFacts{Pawn: "unarmed", SnapshotToken: "pawn-cas", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), ExistingJobDef: domain.Known("")}
	return EquipRequest{Action: a, Progress: p, Current: s, MinimumTick: 11, Facts: EquipFacts{Snapshot: s, PawnTick: 12, PreviewTick: 13, Pawn: pawn, ThingSnapshotToken: "thing-cas", NativeCanTry: domain.Known(true)}}
}

func TestEquipAdmission(t *testing.T) {
	r := equipRequest(t)
	original := r.Progress
	for _, prepared := range []bool{false, true} {
		if prepared {
			var err error
			r.Progress, err = r.Progress.Prepare(r.Current, 11)
			if err != nil {
				t.Fatal(err)
			}
		}
		if d := EvaluateEquip(r); !d.Admitted || len(d.Refused) != 0 {
			t.Fatal(d)
		}
	}
	if original.View().Stage != domain.Pending {
		t.Fatal("mutated progress")
	}
}

func TestEquipDefenseHolds(t *testing.T) {
	cases := []struct {
		name   string
		change func(*EquipRequest)
	}{
		{"zero action", func(r *EquipRequest) { r.Action = domain.Action{} }},
		{"zero progress", func(r *EquipRequest) { r.Progress = domain.Progress{} }},
		{"cancelled", func(r *EquipRequest) { r.Progress, _ = r.Progress.Cancel() }},
		{"minimum", func(r *EquipRequest) { r.MinimumTick = 13 }},
		{"negative minimum", func(r *EquipRequest) { r.MinimumTick = -1 }},
		{"reversed interval", func(r *EquipRequest) { r.Facts.PreviewTick = 11 }},
		{"zero generation", func(r *EquipRequest) { r.Current.Native = 0 }},
		{"native", func(r *EquipRequest) { r.Current.Native++ }},
		{"direction", func(r *EquipRequest) { r.Current.Direction++ }},
		{"colony", func(r *EquipRequest) { r.Current.Colony = "other" }},
		{"load", func(r *EquipRequest) { r.Current.Load = "other" }},
		{"map", func(r *EquipRequest) { r.Current.Map++ }},
		{"plan", func(r *EquipRequest) { r.Current.Plan = "other" }},
		{"revision", func(r *EquipRequest) { r.Current.Revision++ }},
		{"pawn CAS", func(r *EquipRequest) { r.Facts.Pawn.SnapshotToken = "" }},
		{"thing CAS", func(r *EquipRequest) { r.Facts.ThingSnapshotToken = "" }},
		{"wrong pawn", func(r *EquipRequest) { r.Facts.Pawn.Pawn = "other" }},
		{"pawn dead", func(r *EquipRequest) { r.Facts.Pawn.Dead = domain.Known(true) }},
		{"pawn downed", func(r *EquipRequest) { r.Facts.Pawn.Downed = domain.Known(true) }},
		{"pawn drafted", func(r *EquipRequest) { r.Facts.Pawn.Drafted = domain.Known(true) }},
		{"pawn mental state", func(r *EquipRequest) { r.Facts.Pawn.MentalState = domain.Known(true) }},
		{"pawn already equipping", func(r *EquipRequest) { r.Facts.Pawn.ExistingJobDef = domain.Known("Equip") }},
		{"preview refusal", func(r *EquipRequest) { r.Facts.NativeCanTry = domain.Known(false) }},
		{"unknown preview", func(r *EquipRequest) { r.Facts.NativeCanTry = domain.Unknown[bool]() }},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := equipRequest(t)
			c.change(&r)
			d := EvaluateEquip(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason == "" {
				t.Fatal(d)
			}
		})
	}
}

func TestSelectEquipPairsNearestWeaponToLowestPawnID(t *testing.T) {
	pawns := []EquipCandidatePawn{
		{Pawn: "armed", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), IncapableOfViolence: domain.Known(false), Armed: domain.Known(true), Position: domain.Cell{X: 0, Z: 0}},
		{Pawn: "b-unarmed", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), IncapableOfViolence: domain.Known(false), Armed: domain.Known(false), Position: domain.Cell{X: 0, Z: 0}},
		{Pawn: "a-unarmed", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), IncapableOfViolence: domain.Known(false), Armed: domain.Known(false), Position: domain.Cell{X: 5, Z: 5}},
	}
	weapons := []EquipCandidateWeapon{
		{Thing: "far", Definition: "Gun_Revolver", Cell: domain.Cell{X: 50, Z: 50}, Ranged: domain.Known(true)},
		{Thing: "near", Definition: "Gun_Revolver", Cell: domain.Cell{X: 6, Z: 5}, Ranged: domain.Known(true)},
	}
	pawn, weapon, ok := SelectEquip(pawns, weapons)
	if !ok || pawn != "a-unarmed" || weapon.Thing != "near" {
		t.Fatal(pawn, weapon, ok)
	}
}

func TestSelectEquipRequiresEligiblePawnAndWeapon(t *testing.T) {
	pawns := []EquipCandidatePawn{
		{Pawn: "armed", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), IncapableOfViolence: domain.Known(false), Armed: domain.Known(true)},
	}
	weapons := []EquipCandidateWeapon{{Thing: "gun", Definition: "Gun_Revolver"}}
	if _, _, ok := SelectEquip(pawns, weapons); ok {
		t.Fatal("expected no eligible pawn")
	}
	if _, _, ok := SelectEquip(nil, weapons); ok {
		t.Fatal("expected no pawns")
	}
	unarmed := []EquipCandidatePawn{{Pawn: "u", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), IncapableOfViolence: domain.Known(false), Armed: domain.Known(false)}}
	if _, _, ok := SelectEquip(unarmed, nil); ok {
		t.Fatal("expected no weapons")
	}
}
