package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// combatCivilian is a colonist incapable of violence, unarmed.
func combatCivilian(id domain.PawnID) SquadDefenderFacts {
	d := combatRifleman(id)
	d.ViolenceCapable, d.Armed, d.RangedEquipped = domain.Known(false), domain.Known(false), domain.Known(false)
	return d
}

// podsView is one pod landing at (10,14) inside a doorless planned room,
// riflemen a and c near it and b far off, civilian n beside the landing
// room, and a landing-free room at (30,0). No pod has opened yet.
func podsView() CombatView {
	return CombatView{
		Tick:      100,
		Defenders: []SquadDefenderFacts{combatRifleman("a"), combatRifleman("b"), combatRifleman("c"), combatCivilian("n")},
		Pawns: []CombatPawnState{
			{ID: "a", Cell: domain.Known(domain.Cell{X: 10, Z: 10}), Stance: StanceIdle},
			{ID: "b", Cell: domain.Known(domain.Cell{X: 40, Z: 40}), Stance: StanceIdle},
			{ID: "c", Cell: domain.Known(domain.Cell{X: 12, Z: 10}), Stance: StanceIdle},
			{ID: "n", Cell: domain.Known(domain.Cell{X: 11, Z: 11}), Stance: StanceIdle},
		},
		Orderable: []domain.PawnID{"a", "b", "c", "n"},
		Pods:      domain.Known(PodArrival{Landing: []domain.Cell{{X: 10, Z: 14}}, Open: 620}),
		Rooms: []CombatRoom{
			{Interior: Rectangle{X: 5, Z: 12, Width: 10, Height: 6}},
			{Interior: Rectangle{X: 30, Z: 0, Width: 4, Height: 4}, Doors: []domain.Cell{{X: 29, Z: 1}}},
		},
	}
}

func podRoles(m CombatMemory) map[domain.PawnID]CombatRole {
	out := map[domain.PawnID]CombatRole{}
	for _, r := range m.Roles {
		out[r.Pawn] = r
	}
	return out
}

// A pods arrival picks the pods tactic, not the squad fallback a walk-in
// raider would get, and the responders engage the nearest pod raider once
// one is out.
func TestDecideCombatPodsPickPodTactic(t *testing.T) {
	view := podsView()
	s, d := combatRaider("r1", domain.Cell{X: 10, Z: 14})
	view.Threats, view.Positional = []SquadThreatFacts{s}, []DefensiveThreatFacts{d}
	orders, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	if memory.Tactic != TacticPods || memory.Pods == nil || memory.Pods.Open != 620 {
		t.Fatalf("%+v", memory)
	}
	roles := podRoles(memory)
	if roles["a"].Target != "r1" || roles["c"].Target != "r1" {
		t.Fatalf("responders do not engage the pod raider: %+v", memory.Roles)
	}
	attacks := 0
	for _, o := range orders {
		if o.Kind == OrderAttack && o.Target == "r1" {
			attacks++
		}
	}
	if attacks != 2 {
		t.Fatalf("%+v", orders)
	}
	// The arrival row gone from a later frame, the fight keeps its tactic.
	view.Pods = domain.Unknown[PodArrival]()
	view.Tick = 160
	if _, again := decideStop(t, view, StopEvent{}, memory); again.Tactic != TacticPods {
		t.Fatalf("%+v", again)
	}
}

// Before the pods open, the two armed colonists nearest the landing cell
// take roles (the fight drafts every role); the far rifleman does not.
func TestDecideCombatPodsDraftNearestArmed(t *testing.T) {
	_, memory := decideStop(t, podsView(), StopEvent{}, CombatMemory{})
	roles := podRoles(memory)
	if _, ok := roles["b"]; ok || len(roles) != 3 {
		t.Fatalf("%+v", memory.Roles)
	}
	for _, id := range []domain.PawnID{"a", "c"} {
		if r, ok := roles[id]; !ok || r.Duty != "" || r.Cell != nil {
			t.Fatalf("%s: %+v", id, memory.Roles)
		}
	}
}

// A non-combatant near the landing cell moves to the landing-free room's
// cell farthest from the pods.
func TestDecideCombatPodsEvacuateNonCombatants(t *testing.T) {
	orders, memory := decideStop(t, podsView(), StopEvent{}, CombatMemory{})
	want := []CombatOrder{{Pawn: "n", Kind: OrderMove, Cell: domain.Cell{X: 33, Z: 0}, Reason: ReasonFormation}}
	if !reflect.DeepEqual(orders, want) || podRoles(memory)["n"].Duty != DutyEvacuee {
		t.Fatalf("%+v %+v", orders, memory.Roles)
	}
	// Far from the pods and outside the landing room, nobody moves.
	view := podsView()
	view.Pawns[3].Cell = domain.Known(domain.Cell{X: 31, Z: 2})
	if _, memory = decideStop(t, view, StopEvent{}, CombatMemory{}); len(podRoles(memory)) != 2 {
		t.Fatalf("%+v", memory.Roles)
	}
}

// Pods landing behind the firing line (inside the perimeter) send the
// responders to the inner line.
func TestDecideCombatPodsInsidePerimeterTakeInnerLine(t *testing.T) {
	view := podsView()
	view.Layout = domain.Known(CombatLayout{Firing: []domain.Cell{{X: 9, Z: 5}, {X: 11, Z: 5}}, Retreat: []domain.Cell{{X: 9, Z: 6}, {X: 11, Z: 6}}, Toward: domain.North})
	layout, _ := view.Layout.Value()
	if !BehindFiringLine(layout.Firing, layout.Toward, domain.Cell{X: 10, Z: 14}) {
		t.Fatal("fixture: the landing cell is not inside the perimeter")
	}
	orders, _ := decideStop(t, view, StopEvent{}, CombatMemory{})
	want := map[domain.PawnID]domain.Cell{"a": {X: 9, Z: 6}, "c": {X: 11, Z: 6}}
	got := map[domain.PawnID]domain.Cell{}
	for _, o := range orders {
		if o.Kind == OrderMove && o.Reason == ReasonRetreat {
			got[o.Pawn] = o.Cell
		}
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("%+v", orders)
	}
}
