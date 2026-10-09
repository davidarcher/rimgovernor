package store

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"reflect"
	"strings"
	"testing"
)

func testCombatPlan(t *testing.T, s *Store, id string, orders []domain.CombatCommand) domain.PlanSpec {
	t.Helper()
	b, err := domain.NewCombatBatch("fight", id, orders)
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewCombatBatchAction(domain.ActionID(id), b)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan(domain.PlanID(id), 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(context.Background(), p); err != nil {
		t.Fatal(err)
	}
	return p
}
func uncertainCombat(t *testing.T, s *Store, p domain.PlanSpec) {
	t.Helper()
	ctx := context.Background()
	snap := scope()
	snap.Plan = p.ID()
	snap.Revision = 1
	if _, err := s.Prepare(ctx, p.ID(), p.Actions()[0].ID(), snap, 10); err != nil {
		t.Fatal(err)
	}
	progress, err := s.Dispatch(ctx, p.ID(), p.Actions()[0].ID(), snap, 10)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.RecordReceipt(ctx, p.ID(), p.Actions()[0].ID(), progress.View().Attempt, domain.ReceiptUnknown); err != nil {
		t.Fatal(err)
	}
}
func TestCombatSupersessionPreservesUncertaintyAndCleanup(t *testing.T) {
	ctx := context.Background()
	s := open(t, memoryPath(t))
	fight, _ := domain.NewPlan("fight", 1, nil)
	if err := s.CreatePlan(ctx, fight); err != nil {
		t.Fatal(err)
	}
	w := World{Colony: scope().Colony, Load: scope().Load, Map: scope().Map}
	if err := s.OpenCombatFight(ctx, "fight", policy.CombatMemory{}, w, []domain.PawnID{"a"}); err != nil {
		t.Fatal(err)
	}
	cell := domain.Cell{X: 2, Z: 3}
	old := testCombatPlan(t, s, "old", []domain.CombatCommand{{Kind: "door", Cell: cell, Door: "hold_open"}, {Kind: "attack", Pawn: "a", Target: "enemy"}})
	uncertainCombat(t, s, old)
	if err := s.RetireCombatBatch(ctx, old.ID()); err != nil {
		t.Fatal(err)
	}
	got, _ := s.LoadPlan(ctx, old.ID())
	if got.Retired {
		t.Fatal("dispatch return retired uncertainty")
	}
	next := testCombatPlan(t, s, "next", []domain.CombatCommand{{Kind: "move", Pawn: "a", Cell: cell}})
	uncertainCombat(t, s, next)
	stop := CombatStopRecord{Batch: next.ID(), Tick: 11, Orders: []CombatOrderRecord{{CombatOrder: policy.CombatOrder{Kind: policy.OrderMove, Pawn: "a", Cell: cell}, Uncertain: true}}}
	if err := s.RecordCombatStop(ctx, "fight", stop, policy.CombatMemory{}); err != nil {
		t.Fatal(err)
	}
	got, _ = s.LoadPlan(ctx, old.ID())
	if got.Retired {
		t.Fatal("cleanup lost on supersession")
	}
	kept := CombatRestoration{World: w, Owner: "fight", Doors: []CombatDoorRestoration{{Cell: cell, Forbidden: true}}}
	if err := s.SaveCombatRestoration(ctx, kept); err != nil {
		t.Fatal(err)
	}
	if err := s.RecordCombatStop(ctx, "fight", stop, policy.CombatMemory{}); err != nil {
		t.Fatal(err)
	}
	got, _ = s.LoadPlan(ctx, old.ID())
	receipt, known := got.Progress[0].View().Receipt.Value()
	if !got.Retired || got.SupersededBy != next.ID() || !known || receipt != domain.ReceiptUnknown || !got.Progress[0].View().Unresolved {
		t.Fatal("historical uncertainty changed", got)
	}
	if err := s.CloseCombatFight(ctx, "fight"); err == nil {
		t.Fatal("closed without restoring settings")
	}
}
func TestCombatRestorationSaveContainsOnlyOriginalSettings(t *testing.T) {
	ctx := context.Background()
	s := open(t, memoryPath(t))
	kept := CombatRestoration{World: World{Colony: "c", Load: "old", Map: 1}, Owner: "fight", Doors: []CombatDoorRestoration{{Cell: domain.Cell{X: 3, Z: 5}, HoldOpen: true, Forbidden: true}}, Animals: []CombatAnimalRestoration{{Pawn: "pet", Area: "Area_Safe"}}}
	if err := s.SaveCombatRestoration(ctx, kept); err != nil {
		t.Fatal(err)
	}
	saved, err := s.GovernorStateBlobs(ctx)
	if err != nil {
		t.Fatal(err)
	}
	for _, forbidden := range []string{"attack", "Attempt", "Receipt", "Issued", "Tactic"} {
		if strings.Contains(saved[GovernorCombatRestorationKey], forbidden) {
			t.Fatal("session history saved", saved)
		}
	}
	fresh := open(t, memoryPath(t))
	if err = fresh.RebuildFamilies(ctx, saved); err != nil {
		t.Fatal(err)
	}
	got, ok, err := fresh.LoadCombatRestoration(ctx)
	if err != nil || !ok || !reflect.DeepEqual(got, kept) {
		t.Fatal(got, ok, err)
	}
	actions, err := CombatRestorationActions(got, "restore")
	if err != nil {
		t.Fatal(err)
	}
	batch, _ := actions[0].CombatBatch()
	if batch.Orders()[0].Door != "hold_open" || batch.Orders()[1].Door != "forbid" || batch.Orders()[2].Kind != "animal_clear" {
		t.Fatal(batch.Orders())
	}
	area, _ := actions[1].Husbandry()
	if area.Argument() != "Area_Safe" {
		t.Fatal("lost original restriction", area)
	}
	if err = fresh.RebuildFamilies(ctx, nil); err != nil {
		t.Fatal(err)
	}
	_, ok, err = fresh.LoadCombatRestoration(ctx)
	if err != nil || ok {
		t.Fatal("stale restoration survived missing save", ok, err)
	}
}

func TestCombatRestorationReloadAuthorityAndBoundedRetries(t *testing.T) {
	ctx := context.Background()
	s := open(t, memoryPath(t))
	req := roundsRequest()
	req.Current.Native = 2
	reviewRounds(t, s, &req)
	kept := CombatRestoration{World: World{Colony: req.Current.Colony, Load: "old-load", Map: req.Current.Map}, Owner: "old-fight", Doors: []CombatDoorRestoration{{Cell: domain.Cell{X: 2, Z: 3}, HoldOpen: true, Forbidden: true}}}
	if err := s.SaveCombatRestoration(ctx, kept); err != nil {
		t.Fatal(err)
	}
	incident, err := s.OpenIncident(ctx, IncidentAssessment{Kind: policy.ActiveCombat, Trigger: "restore_combat_settings", Snapshot: req.Current, Tick: 10})
	if err != nil {
		t.Fatal(err)
	}
	var latest domain.PlanSpec
	for i := 0; i < 258; i++ {
		id := domain.PlanID(fmt.Sprintf("restore-%d", i))
		actions, err := CombatRestorationActions(kept, id)
		if err != nil {
			t.Fatal(err)
		}
		latest, err = domain.NewPlan(id, 1, actions)
		if err != nil {
			t.Fatal(err)
		}
		if err = s.CommitCombatRestoration(ctx, incident.Incident.ID, latest); err != nil {
			t.Fatalf("retry %d: %v", i, err)
		}
		target := req.Current
		target.Plan, target.Revision = id, 1
		if err = s.AuthorizeRoundsPlan(ctx, req.Current, target); err != nil {
			t.Fatal("reload cleanup authority", err)
		}
		if i == 0 {
			uncertainCombat(t, s, latest)
		}
	}
	first, err := s.LoadPlan(ctx, "restore-0")
	if err != nil || !first.Retired || first.SupersededBy != "restore-1" || !first.Progress[0].View().Unresolved || first.Progress[0].View().Receipt != domain.Known(domain.ReceiptUnknown) {
		t.Fatal("lost immutable uncertainty", first, err)
	}
	loaded, err := s.LoadIncident(ctx, incident.Incident.ID)
	if err != nil || len(loaded.Methods) != 1 {
		t.Fatal("cleanup history consumes live bound", loaded, err)
	}
	bad := kept
	bad.Doors = append([]CombatDoorRestoration(nil), kept.Doors...)
	bad.Doors[0].Forbidden = false
	if err = s.SaveCombatRestoration(ctx, bad); err == nil {
		t.Fatal("replaced original setting")
	}
	target := req.Current
	target.Plan, target.Revision = latest.ID(), 1
	if _, err = s.Prepare(ctx, latest.ID(), latest.Actions()[0].ID(), target, 10); err != nil {
		t.Fatal(err)
	}
	p, err := s.Dispatch(ctx, latest.ID(), latest.Actions()[0].ID(), target, 10)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.RecordReceipts(ctx, []BatchReceipt{{Plan: latest.ID(), Action: latest.Actions()[0].ID(), Attempt: p.View().Attempt, Receipt: domain.ReceiptAccepted, Combat: []domain.CombatResult{{Index: 0, Applied: true}, {Index: 1, Applied: true}}}}); err != nil {
		t.Fatal(err)
	}
	if err = s.FinishCombatRestoration(ctx, kept.Owner, latest.ID()); err != nil {
		t.Fatal(err)
	}
	if _, exists, err := s.LoadCombatRestoration(ctx); err != nil || exists {
		t.Fatal("completed restoration retained", exists, err)
	}
}
