package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// peelView is holdView with an inner line at z=24 and one brawler p, the
// peeler, whose home is (9,25) behind gunner a.
func peelView() CombatView {
	view := holdView()
	layout, _ := view.Layout.Value()
	layout.Retreat = []domain.Cell{{X: 9, Z: 24}, {X: 8, Z: 24}, {X: 10, Z: 24}}
	view.Layout = domain.Known(layout)
	view.Defenders = append(view.Defenders, combatBrawler("p", .5))
	view.Pawns = append(view.Pawns, CombatPawnState{ID: "p", Cell: domain.Known(domain.Cell{X: 5, Z: 30}), Stance: StanceIdle})
	view.Orderable = append(view.Orderable, "p")
	return view
}

// settle puts every pawn at its role's cell, gunners shooting r2.
func settle(view *CombatView, memory CombatMemory) {
	cells := map[domain.PawnID]domain.Cell{}
	for _, r := range memory.Roles {
		if r.Cell != nil {
			cells[r.Pawn] = *r.Cell
		}
	}
	for i := range view.Pawns {
		p := &view.Pawns[i]
		p.Cell, p.Stance = domain.Known(cells[p.ID]), StanceMoving
		if p.ID != "p" {
			p.Target = "r2"
		}
	}
}

// ordersFor is the orders naming pawn.
func ordersFor(orders []CombatOrder, pawn domain.PawnID) []CombatOrder {
	var out []CombatOrder
	for _, o := range orders {
		if o.Pawn == pawn {
			out = append(out, o)
		}
	}
	return out
}

// {melee contact on gunner a by hostile r1, peeler p} → attack(p → r1);
// a stop without contact gives the peeler nothing.
func TestDecideCombatPeelerInterceptsGunnerAttacker(t *testing.T) {
	view := peelView()
	orders, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	home := domain.Cell{X: 9, Z: 25}
	if got := ordersFor(orders, "p"); len(got) != 1 || got[0].Kind != OrderMove || got[0].Cell != home {
		t.Fatalf("peeler formation %+v", got)
	}
	settle(&view, memory)
	view.Tick = 200
	orders, memory = decideStop(t, view, StopEvent{Kind: "entered_range", Pawn: "r1"}, memory)
	if got := ordersFor(orders, "p"); len(got) != 0 {
		t.Fatalf("no contact, yet %+v", got)
	}
	// r1 reaches a's cover row and starts a melee job on a.
	view.Tick = 260
	view.Positional[0].Position = domain.Known(domain.Cell{X: 9, Z: 22})
	view.Positional[0].NearestColonistDistance = domain.Known(1.0)
	orders, _ = decideStop(t, view, StopEvent{Kind: StopMeleeContact, Pawn: "r1", Target: "a"}, memory)
	want := CombatOrder{Pawn: "p", Kind: OrderAttack, Target: "r1", Reason: ReasonFormation}
	if got := ordersFor(orders, "p"); len(got) != 1 || got[0] != want {
		t.Fatalf("peeler %+v, want %+v", got, want)
	}
}

// Contact between a hostile and a brawler, not a gunner, is not the
// peeler's.
func TestDecideCombatPeelerIgnoresNonGunnerContact(t *testing.T) {
	view := peelView()
	_, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	settle(&view, memory)
	view.Tick = 200
	orders, _ := decideStop(t, view, StopEvent{Kind: StopMeleeContact, Pawn: "r1", Target: "p"}, memory)
	if got := ordersFor(orders, "p"); len(got) != 0 {
		t.Fatalf("%+v", got)
	}
}

// Once the peeler's target is down it returns to its home cell.
func TestDecideCombatPeelerReturnsAfterKill(t *testing.T) {
	view := peelView()
	_, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	settle(&view, memory)
	view.Tick = 260
	orders, memory := decideStop(t, view, StopEvent{Kind: StopMeleeContact, Pawn: "r1", Target: "a"}, memory)
	if len(ordersFor(orders, "p")) != 1 {
		t.Fatalf("%+v", orders)
	}
	// p chases r1 to (9,22) and downs it.
	view.Tick = 320
	for i := range view.Pawns {
		if view.Pawns[i].ID == "p" {
			view.Pawns[i].Cell, view.Pawns[i].Target, view.Pawns[i].Stance = domain.Known(domain.Cell{X: 9, Z: 21}), "r1", StanceMelee
		}
	}
	view.Threats[0].Downed = domain.Known(true)
	view.Positional[0].Downed = domain.Known(true)
	orders, memory = decideStop(t, view, StopEvent{Kind: "downed", Pawn: "r1"}, memory)
	want := CombatOrder{Pawn: "p", Kind: OrderMove, Cell: domain.Cell{X: 9, Z: 25}, Reason: ReasonFormation}
	if got := ordersFor(orders, "p"); len(got) != 1 || got[0] != want {
		t.Fatalf("peeler %+v, want %+v", got, want)
	}
	for _, r := range memory.Roles {
		if r.Pawn == "p" && (r.Duty != DutyPeeler || r.Target != "") {
			t.Fatalf("%+v", r)
		}
	}
}
