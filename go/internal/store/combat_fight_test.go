package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A fight's evidence keeps the newest CombatEvidenceStops stops, refuses a
// stop with no orders, and closing the fight drops it from the open set.
func TestCombatFightEvidence(t *testing.T) {
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "combat.db"))
	draft, _ := domain.NewOwnedDraft("a")
	action, _ := domain.NewOwnedDraftAction("draft-a", draft)
	plan, err := domain.NewPlan("fight", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	if err = s.OpenCombatFight(ctx, "fight", policy.CombatMemory{Tactic: policy.TacticHold}); err != nil {
		t.Fatal(err)
	}
	if err = s.RecordCombatStop(ctx, "fight", CombatStopRecord{Tick: 1}, policy.CombatMemory{}); err == nil {
		t.Fatal("a stop with no orders was recorded")
	}
	for tick := range CombatEvidenceStops + 5 {
		order := CombatOrderRecord{CombatOrder: policy.CombatOrder{Pawn: "a", Kind: policy.OrderMove, Cell: domain.Cell{X: int32(tick)}}, Applied: true}
		if err = s.RecordCombatStop(ctx, "fight", CombatStopRecord{Tick: domain.Tick(tick), Orders: []CombatOrderRecord{order}}, policy.CombatMemory{Tactic: policy.TacticSquad, Tick: domain.Tick(tick)}); err != nil {
			t.Fatal(err)
		}
	}
	evidence, err := s.CombatEvidence(ctx, "fight")
	if err != nil || len(evidence) != CombatEvidenceStops || evidence[0].Tick != 5 || evidence[len(evidence)-1].Tick != CombatEvidenceStops+4 {
		t.Fatal(len(evidence), err)
	}
	fight, ok, err := s.LoadCombatFight(ctx, "fight")
	if err != nil || !ok || !fight.Open || fight.Memory.Tactic != policy.TacticSquad {
		t.Fatal(fight, ok, err)
	}
	if open, err := s.OpenCombatFights(ctx); err != nil || !open["fight"] {
		t.Fatal(open, err)
	}
	if err = s.CloseCombatFight(ctx, "fight"); err != nil {
		t.Fatal(err)
	}
	if open, err := s.OpenCombatFights(ctx); err != nil || open["fight"] {
		t.Fatal(open, err)
	}
}
