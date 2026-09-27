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
	plan, err := domain.NewPlan("fight", 1, nil)
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	world := World{Colony: "c", Load: "l", Map: 1}
	if err = s.OpenCombatFight(ctx, "fight", policy.CombatMemory{Tactic: policy.TacticHold}, world, []domain.PawnID{"a", "b"}); err != nil {
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
	if fight.World != world || len(fight.Claims) != 2 || fight.Claims["a"] != "" {
		t.Fatal("roster claims", fight)
	}
	// The admission batch's results (#910): a's claim learned, b refused.
	if err = s.RecordCombatClaims(ctx, "fight", map[domain.PawnID]string{"a": "claim-a"}, []domain.PawnID{"b"}); err != nil {
		t.Fatal(err)
	}
	if held, err := s.HeldCombatFights(ctx); err != nil || !held["fight"].Open || held["fight"].Claims["a"] != "claim-a" || len(held["fight"].Claims) != 1 {
		t.Fatal(held, err)
	}
	if err = s.CloseCombatFight(ctx, "fight"); err != nil {
		t.Fatal(err)
	}
	// Closed, the fight is held until its last claim is released.
	if held, err := s.HeldCombatFights(ctx); err != nil || held["fight"].Open || len(held["fight"].Claims) != 1 {
		t.Fatal(held, err)
	}
	if err = s.RecordCombatClaims(ctx, "fight", nil, []domain.PawnID{"a"}); err != nil {
		t.Fatal(err)
	}
	if held, err := s.HeldCombatFights(ctx); err != nil || len(held) != 0 {
		t.Fatal(held, err)
	}
	if err = s.RecordCombatClaims(ctx, "nofight", nil, nil); err == nil {
		t.Fatal("claims recorded for a missing fight")
	}
}
