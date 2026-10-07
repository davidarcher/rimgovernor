package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// squadRosterView is a squad fight (no layout) of five riflemen against two
// raiders; the selector assigns four, so one stays in reserve.
func squadRosterView() CombatView {
	view := holdView()
	view.Layout = domain.Fact[CombatLayout]{}
	for _, id := range []domain.PawnID{"d", "e"} {
		view.Defenders = append(view.Defenders, combatRifleman(id))
		view.Pawns = append(view.Pawns, CombatPawnState{ID: id, Cell: domain.Known(domain.Cell{X: 4, Z: 30}), Stance: StanceIdle})
		view.Orderable = append(view.Orderable, id)
	}
	return view
}

func roleHolders(m CombatMemory) map[domain.PawnID]CombatRole {
	out := map[domain.PawnID]CombatRole{}
	for _, r := range m.Roles {
		out[r.Pawn] = r
	}
	return out
}

func downDefender(view *CombatView, id domain.PawnID) {
	for i := range view.Defenders {
		if view.Defenders[i].ID == id {
			view.Defenders[i].Downed = domain.Known(true)
		}
	}
	for i := range view.Pawns {
		if view.Pawns[i].ID == id {
			view.Pawns[i].Downed = true
		}
	}
}

// A role holder going down mid-fight hands its slot to a reserve colonist at
// that stop (#2350).
func TestDecideCombatSquadReplacesDownedRoleHolder(t *testing.T) {
	view := squadRosterView()
	_, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	held := roleHolders(memory)
	if memory.Tactic != TacticSquad || len(held) != 4 {
		t.Fatalf("formed %v %+v", memory.Tactic, memory.Roles)
	}
	var reserve, down domain.PawnID
	for _, id := range []domain.PawnID{"a", "b", "c", "d", "e"} {
		if _, ok := held[id]; !ok {
			reserve = id
		} else if down == "" {
			down = id
		}
	}
	view.Tick = 150
	downDefender(&view, down)
	// Every surviving holder is doing its last order.
	orders, next := decideStop(t, view, StopEvent{Kind: StopDowned, Pawn: down}, memory)
	if _, ok := roleHolders(next)[reserve]; !ok {
		t.Fatalf("reserve %s got no role: %+v", reserve, next.Roles)
	}
	if !slicesHasOrder(orders, reserve) {
		t.Fatalf("reserve %s got no order: %+v", reserve, orders)
	}
	if _, ok := roleHolders(next)[down]; ok {
		t.Fatalf("downed %s kept a role", down)
	}
}

// A seriously injured role holder is replaced and pulled back from the line.
func TestDecideCombatSquadFallsBackSeriouslyHurtHolder(t *testing.T) {
	view := squadRosterView()
	_, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	held := roleHolders(memory)
	var hurt, reserve domain.PawnID
	for _, id := range []domain.PawnID{"a", "b", "c", "d", "e"} {
		if _, ok := held[id]; !ok {
			reserve = id
		} else if hurt == "" {
			hurt = id
		}
	}
	view.Tick = 150
	for i := range view.Defenders {
		if view.Defenders[i].ID == hurt {
			view.Defenders[i].HealthFraction = domain.Known(0.3)
		}
	}
	orders, next := decideStop(t, view, StopEvent{Kind: StopSeriousInjury, Pawn: hurt}, memory)
	role, ok := roleHolders(next)[hurt]
	if !ok || !role.Retreat || role.Cell == nil {
		t.Fatalf("hurt %s not pulled back: %+v", hurt, next.Roles)
	}
	var moved bool
	for _, o := range orders {
		if o.Pawn == hurt && o.Kind == OrderMove && o.Reason == ReasonRetreat {
			moved = true
		}
	}
	if !moved {
		t.Fatalf("no retreat move for %s: %+v", hurt, orders)
	}
	if _, ok := roleHolders(next)[reserve]; !ok || !slicesHasOrder(orders, reserve) {
		t.Fatalf("reserve %s not drawn in: %+v %+v", reserve, next.Roles, orders)
	}
	// The next stop keeps the pull-back and does not re-form again.
	view.Tick = 160
	for _, o := range orders {
		for i := range view.Pawns {
			if view.Pawns[i].ID != o.Pawn {
				continue
			}
			view.Pawns[i].Target = o.Target
			if o.Kind == OrderMove {
				view.Pawns[i].Cell = domain.Known(o.Cell)
			}
		}
	}
	again, kept := decideStop(t, view, StopEvent{}, next)
	if kept.Formed != next.Formed || len(again) != 0 {
		t.Fatalf("re-formed or re-ordered: %+v %+v", again, kept)
	}
}

// With every role holder down the roles are empty and the next stop forms
// anew from whoever is still fit.
func TestDecideCombatSquadReformsFromEmptyRoles(t *testing.T) {
	view := squadRosterView()
	_, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	for id := range roleHolders(memory) {
		downDefender(&view, id)
	}
	view.Tick = 150
	orders, next := decideStop(t, view, StopEvent{Kind: StopDowned}, memory)
	if len(next.Roles) == 0 || len(orders) == 0 {
		t.Fatalf("no replacement formed: %+v %+v", next.Roles, orders)
	}
}

func slicesHasOrder(orders []CombatOrder, pawn domain.PawnID) bool {
	for _, o := range orders {
		if o.Pawn == pawn {
			return true
		}
	}
	return false
}
