package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// huntView is three riflemen (range 30) south of a mixed-species prey group.
func huntView(prey ...animalFacts) CombatView {
	view := holdView()
	view.Layout = domain.Fact[CombatLayout]{}
	view.Hunt = true
	for i := range view.Pawns {
		view.Pawns[i].WeaponRange = 30
		view.Pawns[i].WeaponFacts = WeaponDef{Ranged: true, Range: 30}
	}
	view = withAnimals(view, prey...)
	for i := range view.Threats {
		view.Threats[i].Manhunter = domain.Known(false)
	}
	return view
}

func wildGroup() []animalFacts {
	return []animalFacts{
		animal("p1", "Muffalo", domain.Cell{X: 40, Z: 10}, 4.5),
		animal("p2", "Deer", domain.Cell{X: 42, Z: 12}, 6),
		animal("p3", "Boar", domain.Cell{X: 41, Z: 11}, 5),
	}
}

// {wild group, three riflemen} -> TacticHunt; every gunner on a cell 28 cells
// from the group's centre, the cells spread over a half circle on the squad's
// side, all on one target.
func TestHuntStagesHalfCircleAtRange(t *testing.T) {
	orders, m := decideStop(t, huntView(wildGroup()...), StopEvent{}, CombatMemory{})
	if m.Tactic != TacticHunt || len(m.Roles) != 3 {
		t.Fatalf("%+v", m)
	}
	centre := domain.Cell{X: 41, Z: 11}
	var cells []domain.Cell
	for _, r := range m.Roles {
		if r.Cell == nil || !r.Ranged || r.Target != m.Roles[0].Target {
			t.Fatalf("role %+v", r)
		}
		d := math.Hypot(float64(r.Cell.X-centre.X), float64(r.Cell.Z-centre.Z))
		if math.Abs(d-28) > 1 {
			t.Fatalf("%v is %.1f from the group, want 28", *r.Cell, d)
		}
		// Squad is south-west (x<41, z>11): the arc stays on that side.
		if (r.Cell.X-centre.X)*(1-centre.X)+(r.Cell.Z-centre.Z)*(30-centre.Z) < -60 {
			t.Fatalf("%v behind the group", *r.Cell)
		}
		cells = append(cells, *r.Cell)
	}
	if cells[0] == cells[1] || cells[1] == cells[2] || cells[0] == cells[2] {
		t.Fatalf("cells overlap: %v", cells)
	}
	if len(orders) != 3 {
		t.Fatalf("%+v", orders)
	}
	for _, o := range orders {
		if o.Kind != OrderMove {
			t.Fatalf("%+v", o)
		}
	}
}

// {gunners in place} -> every one attacks the same prey; the focus holds
// while it lives, then moves to the next one.
func TestHuntFocusFiresTargetByTarget(t *testing.T) {
	view := huntView(wildGroup()...)
	_, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	for i, r := range m.Roles {
		view.Pawns[i].Cell = domain.Known(*r.Cell)
		view.Pawns[i].WeaponRange = 60
	}
	orders, m := decideStop(t, view, StopEvent{}, m)
	first := m.Roles[0].Target
	if len(orders) != 3 {
		t.Fatalf("%+v", orders)
	}
	for _, o := range orders {
		if o.Kind != OrderAttack || o.Target != first {
			t.Fatalf("%+v", orders)
		}
	}
	for i := range view.Threats {
		if string(view.Threats[i].ID) == string(first) {
			view.Threats[i].Dead = domain.Known(true)
		}
	}
	_, m = decideStop(t, view, StopEvent{}, m)
	if m.Tactic != TacticHunt || m.Roles[0].Target == first || m.Roles[0].Target == "" {
		t.Fatalf("focus did not move on: %+v", m.Roles)
	}
}

// {the whole group dead} -> no roles and no orders: the plan is over and the
// undraft sweep (#939) releases the squad.
func TestHuntEndsWithNoOrdersWhenGroupIsDead(t *testing.T) {
	view := huntView(wildGroup()...)
	_, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	for i := range view.Threats {
		view.Threats[i].Dead = domain.Known(true)
	}
	orders, m := decideStop(t, view, StopEvent{}, m)
	if len(orders) != 0 || len(m.Roles) != 0 {
		t.Fatalf("%+v %+v", orders, m)
	}
}

// {prey that all turn manhunter} -> the manhunter tactic takes over; one
// still-wild animal keeps the hunt.
func TestHuntHandsManhunterPreyToTheManhunterTactic(t *testing.T) {
	view := huntView(wildGroup()...)
	_, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	view.Threats[0].Manhunter = domain.Known(true)
	if _, m2 := decideStop(t, view, StopEvent{}, m); m2.Tactic != TacticHunt {
		t.Fatalf("one manhunter among wild prey: %+v", m2)
	}
	for i := range view.Threats {
		view.Threats[i].Manhunter = domain.Known(true)
	}
	if _, m2 := decideStop(t, view, StopEvent{}, m); m2.Tactic != TacticManhunter {
		t.Fatalf("manhunter pack: %+v", m2)
	}
}
