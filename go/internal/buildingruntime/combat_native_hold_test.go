package buildingruntime

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
)

// Native recorded colony facts establish the roster, weapon definitions and
// firing layout. Later observations stage arrival, native retargeting and an
// injury without requiring another gameplay run for a policy decision.
func TestRecordedCombatNativeHold(t *testing.T) {
	stops, err := snap.CombatStops("testdata/combat/lab-choke.json.gz")
	if err != nil {
		t.Fatal(err)
	}
	s := stops[0]
	combat, err := decodeCombatWithCatalog(s.Frame)
	if err != nil {
		t.Fatal(err)
	}
	in, reason, err := combatFrameInputs(combat, nil)
	if err != nil || !reason.IsZero() {
		t.Fatalf("facts: %v %v", reason, err)
	}
	view := combatView(combat, in, s.Orderable, domain.Known(*s.Layout))
	// The admission frame precedes draft receipts; stage the plan's owned
	// roster before exercising its subsequent combat orders.
	for _, defender := range view.Defenders {
		if !slices.Contains(view.Orderable, defender.ID) {
			view.Orderable = append(view.Orderable, defender.ID)
		}
	}
	decide := func(memory policy.CombatMemory, stop policy.StopEvent) ([]policy.CombatOrder, policy.CombatMemory) {
		t.Helper()
		orders, ask, next := policy.DecideCombat(view, policy.GeometryReply{}, stop, memory)
		if ask != nil {
			orders, _, next = policy.DecideCombat(view, s.Reply, stop, memory)
		}
		return orders, next
	}
	_, memory := decide(policy.CombatMemory{}, policy.StopEvent{})
	var holder domain.PawnID
	for _, r := range memory.Roles {
		if r.Ranged && r.Cell != nil && r.Duty == "" && r.Target == "" {
			holder = r.Pawn
			for i := range view.Pawns {
				if view.Pawns[i].ID == holder {
					view.Pawns[i].Cell = domain.Known(*r.Cell)
					view.Pawns[i].Job, view.Pawns[i].Stance = "Goto", policy.StanceIdle
					view.Pawns[i].FireMode = policy.FireAtWill
				}
			}
			break
		}
	}
	if holder == "" {
		t.Fatalf("recorded roster produced no native holder: %+v", memory.Roles)
	}
	orders, memory := decide(memory, policy.StopEvent{})
	if !slices.ContainsFunc(orders, func(o policy.CombatOrder) bool { return o.Pawn == holder && o.Kind == policy.OrderHoldPosition }) {
		t.Fatalf("arrival orders: %+v", orders)
	}
	for _, target := range []domain.PawnID{"", "new-native-target"} {
		for i := range view.Pawns {
			if view.Pawns[i].ID == holder {
				view.Pawns[i].Job, view.Pawns[i].Target = "Wait_Combat", target
			}
		}
		orders, memory = decide(memory, policy.StopEvent{})
		if slices.ContainsFunc(orders, func(o policy.CombatOrder) bool {
			return o.Pawn == holder && (o.Kind == policy.OrderAttack || o.Kind == policy.OrderHoldPosition)
		}) {
			t.Fatalf("native target %q renewed execution: %+v", target, orders)
		}
	}
	layout := *s.Layout
	layout.Retreat = make([]domain.Cell, len(layout.Firing))
	for i, cell := range layout.Firing {
		layout.Retreat[i] = domain.Cell{X: cell.X, Z: cell.Z + 1}
	}
	view.Layout = domain.Known(layout)
	orders, memory = decide(memory, policy.StopEvent{Kind: policy.StopSeriousInjury, Pawn: holder})
	if !slices.ContainsFunc(orders, func(o policy.CombatOrder) bool {
		return o.Pawn == holder && o.Kind == policy.OrderMove && o.Reason == policy.ReasonRetreat
	}) {
		t.Fatalf("unsafe hold did not retreat: %+v roles %+v", orders, memory.Roles)
	}
	view.Orderable = nil
	orders, _ = decide(memory, policy.StopEvent{})
	if slices.ContainsFunc(orders, func(o policy.CombatOrder) bool { return o.Pawn == holder }) {
		t.Fatalf("unowned pawn ordered: %+v", orders)
	}
}
