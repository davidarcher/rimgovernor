package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func settlementGiftRequest(t *testing.T) SettlementGiftRequest {
	t.Helper()
	gift, _ := domain.NewSettlementGift("caravan-1", "settlement-1", "faction-1", []domain.PawnID{"pawn-1", "pawn-2"}, 500)
	a, _ := domain.NewSettlementGiftAction("settlement-gift-1", gift)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Direction: 1, Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	facts := SettlementGiftFacts{
		Caravan: "caravan-1", CaravanToken: "caravan-cas", Moving: domain.Known(false),
		CrewIDs: domain.Known([]domain.PawnID{"pawn-1", "pawn-2"}), Silver: domain.Known(int32(500)),
		Settlement: "settlement-1", AtTarget: domain.Known(true),
		Faction: "faction-1", FactionToken: "faction-cas", Player: domain.Known(false),
		Hostile: domain.Known(false), Goodwill: domain.Known(int32(10)),
	}
	return SettlementGiftRequest{
		Action: a, Progress: p, Current: s, MinimumTick: 11,
		Facts: SettlementGiftAdmissionFacts{Snapshot: s, CaravanTick: 12, WorldTick: 12, PreviewTick: 13, Gift: facts, NativeCanTry: domain.Known(true)},
	}
}

func TestSettlementGiftAdmission(t *testing.T) {
	r := settlementGiftRequest(t)
	original := r.Progress
	for _, prepared := range []bool{false, true} {
		if prepared {
			var err error
			r.Progress, err = r.Progress.Prepare(r.Current, 11)
			if err != nil {
				t.Fatal(err)
			}
		}
		if d := EvaluateSettlementGift(r); !d.Admitted || len(d.Refused) != 0 {
			t.Fatal(d)
		}
	}
	if original.View().Stage != domain.Pending {
		t.Fatal("mutated progress")
	}
}

func TestSettlementGiftDefenseHolds(t *testing.T) {
	cases := []struct {
		name   string
		change func(*SettlementGiftRequest)
	}{
		{"zero action", func(r *SettlementGiftRequest) { r.Action = domain.Action{} }},
		{"zero progress", func(r *SettlementGiftRequest) { r.Progress = domain.Progress{} }},
		{"cancelled", func(r *SettlementGiftRequest) { r.Progress, _ = r.Progress.Cancel() }},
		{"minimum", func(r *SettlementGiftRequest) { r.MinimumTick = 13 }},
		{"negative minimum", func(r *SettlementGiftRequest) { r.MinimumTick = -1 }},
		{"reversed interval", func(r *SettlementGiftRequest) { r.Facts.PreviewTick = 11 }},
		{"zero generation", func(r *SettlementGiftRequest) { r.Current.Native = 0 }},
		{"native", func(r *SettlementGiftRequest) { r.Current.Native++ }},
		{"direction", func(r *SettlementGiftRequest) { r.Current.Direction++ }},
		{"colony", func(r *SettlementGiftRequest) { r.Current.Colony = "other" }},
		{"load", func(r *SettlementGiftRequest) { r.Current.Load = "other" }},
		{"map", func(r *SettlementGiftRequest) { r.Current.Map++ }},
		{"plan", func(r *SettlementGiftRequest) { r.Current.Plan = "other" }},
		{"revision", func(r *SettlementGiftRequest) { r.Current.Revision++ }},
		{"caravan CAS", func(r *SettlementGiftRequest) { r.Facts.Gift.CaravanToken = "" }},
		{"wrong caravan", func(r *SettlementGiftRequest) { r.Facts.Gift.Caravan = "other" }},
		{"faction CAS", func(r *SettlementGiftRequest) { r.Facts.Gift.FactionToken = "" }},
		{"wrong faction", func(r *SettlementGiftRequest) { r.Facts.Gift.Faction = "other" }},
		{"wrong settlement", func(r *SettlementGiftRequest) { r.Facts.Gift.Settlement = "other" }},
		{"unknown moving", func(r *SettlementGiftRequest) { r.Facts.Gift.Moving = domain.Unknown[bool]() }},
		{"still moving", func(r *SettlementGiftRequest) { r.Facts.Gift.Moving = domain.Known(true) }},
		{"unknown crew", func(r *SettlementGiftRequest) { r.Facts.Gift.CrewIDs = domain.Unknown[[]domain.PawnID]() }},
		{"missing crew member", func(r *SettlementGiftRequest) {
			r.Facts.Gift.CrewIDs = domain.Known([]domain.PawnID{"pawn-1"})
		}},
		{"extra crew member", func(r *SettlementGiftRequest) {
			r.Facts.Gift.CrewIDs = domain.Known([]domain.PawnID{"pawn-1", "pawn-2", "pawn-3"})
		}},
		{"swapped crew member", func(r *SettlementGiftRequest) {
			r.Facts.Gift.CrewIDs = domain.Known([]domain.PawnID{"pawn-1", "pawn-3"})
		}},
		{"unknown at target", func(r *SettlementGiftRequest) { r.Facts.Gift.AtTarget = domain.Unknown[bool]() }},
		{"not at target", func(r *SettlementGiftRequest) { r.Facts.Gift.AtTarget = domain.Known(false) }},
		{"unknown player", func(r *SettlementGiftRequest) { r.Facts.Gift.Player = domain.Unknown[bool]() }},
		{"own settlement", func(r *SettlementGiftRequest) { r.Facts.Gift.Player = domain.Known(true) }},
		{"unknown hostile", func(r *SettlementGiftRequest) { r.Facts.Gift.Hostile = domain.Unknown[bool]() }},
		{"hostile", func(r *SettlementGiftRequest) { r.Facts.Gift.Hostile = domain.Known(true) }},
		{"unknown goodwill", func(r *SettlementGiftRequest) { r.Facts.Gift.Goodwill = domain.Unknown[int32]() }},
		{"goodwill capped", func(r *SettlementGiftRequest) { r.Facts.Gift.Goodwill = domain.Known(int32(100)) }},
		{"unknown silver", func(r *SettlementGiftRequest) { r.Facts.Gift.Silver = domain.Unknown[int32]() }},
		{"insufficient silver", func(r *SettlementGiftRequest) { r.Facts.Gift.Silver = domain.Known(int32(100)) }},
		{"preview refusal", func(r *SettlementGiftRequest) { r.Facts.NativeCanTry = domain.Known(false) }},
		{"unknown preview", func(r *SettlementGiftRequest) { r.Facts.NativeCanTry = domain.Unknown[bool]() }},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := settlementGiftRequest(t)
			c.change(&r)
			d := EvaluateSettlementGift(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason == "" {
				t.Fatal(d)
			}
		})
	}
}

func TestSettlementGiftRefusesInsufficientReserveDistinctly(t *testing.T) {
	r := settlementGiftRequest(t)
	r.Facts.Gift.Silver = domain.Known(int32(100))
	d := EvaluateSettlementGift(r)
	if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason != InsufficientReserve {
		t.Fatal(d)
	}
}
