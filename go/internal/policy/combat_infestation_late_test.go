package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// insect is a live Megaspider (not a manhunter animal) at cell and speed.
func insect(id PawnID, cell domain.Cell, speed float64) animalFacts {
	return func() (SquadThreatFacts, DefensiveThreatFacts, CombatPawnState) {
		s, d, p := combatAnimal(id, "Megaspider", cell, speed)
		s.Animal, s.Manhunter = domain.Known(false), domain.Known(false)
		return s, d, p
	}
}

// insectKiteView is kiteView's line, inner line and fast rifleman b facing
// the insects instead of muffalo.
func insectKiteView(insects ...animalFacts) CombatView {
	view := kiteView(3.5)
	view.Pawns = view.Pawns[:3]
	return withAnimals(view, insects...)
}

// {a late-stage infestation of slow insects, fast long-gunned rifleman b}
// -> b is the kiter and lures them out past the line; {more insects than
// we can fight} -> the fight evacuates to the inner line, nobody kites.
func TestInfestationKiteOut(t *testing.T) {
	view := insectKiteView(insect("i1", domain.Cell{X: 9, Z: 5}, 3.5), insect("i2", domain.Cell{X: 10, Z: 4}, 3.5))
	_, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	if m.Tactic != TacticInfestation || m.Kiter != "b" || m.Wait {
		t.Fatalf("%+v", m)
	}
	if r := role(m, "b"); r.Duty != DutyKiter || r.Target == "" {
		t.Fatalf("%+v", r)
	}

	var swarm []animalFacts
	for i := range 5 {
		swarm = append(swarm, insect(PawnID("i"+string(rune('1'+i))), domain.Cell{X: int32(6 + i), Z: 5}, 3.5))
	}
	orders, m := decideStop(t, insectKiteView(swarm...), StopEvent{}, CombatMemory{})
	if m.Tactic != TacticInfestation || !m.Wait || m.Kiter != "" {
		t.Fatalf("%+v", m)
	}
	for _, o := range orders {
		if o.Kind == OrderAttack {
			t.Fatalf("attacked while evacuating: %+v", orders)
		}
	}
}

// {insects tunnel up with no hive, rifleman a working the deep drill, a
// choke and brawlers} -> a evacuates to the rearmost inner-line cell and
// the brawlers melee-block the choke; next stop a no longer drills and
// still evacuates.
func TestDeepDrillEvacuate(t *testing.T) {
	view := infested(chokeView())
	view.Structures = nil
	layout, _ := view.Layout.Value()
	layout.Retreat = []domain.Cell{{X: 9, Z: 24}, {X: 8, Z: 26}, {X: 10, Z: 25}}
	view.Layout = domain.Known(layout)
	for i := range view.Pawns {
		if view.Pawns[i].ID == "a" {
			view.Pawns[i].Job = deepDrillJob
		}
	}
	want := domain.Cell{X: 8, Z: 26}
	orders, m := decideChoke(t, view, StopEvent{}, CombatMemory{})
	if m.Tactic != TacticInfestation || m.Driller != "a" {
		t.Fatalf("%+v", m)
	}
	evacuated := false
	for _, o := range orders {
		evacuated = evacuated || o.Pawn == "a" && o.Kind == OrderMove && o.Cell == want && o.Reason == ReasonRetreat
	}
	if !evacuated {
		t.Fatalf("driller not evacuated: %+v", orders)
	}
	blockers := 0
	for _, r := range m.Roles {
		if r.Duty == DutyBlocker {
			blockers++
		}
	}
	if blockers == 0 {
		t.Fatalf("no melee block: %+v", m.Roles)
	}

	view.Tick = 220
	for i := range view.Pawns {
		if view.Pawns[i].ID == "a" {
			view.Pawns[i].Job = "Goto"
		}
	}
	_, m = decideStop(t, view, StopEvent{}, m)
	if r := role(m, "a"); m.Driller != "a" || r.Cell == nil || *r.Cell != want || r.Target != "" {
		t.Fatalf("%+v %+v", m, r)
	}
	// With a hive it is no deep-drill spawn.
	hived := infested(chokeView())
	for i := range hived.Pawns {
		if hived.Pawns[i].ID == "a" {
			hived.Pawns[i].Job = deepDrillJob
		}
	}
	if _, m := decideChoke(t, hived, StopEvent{}, CombatMemory{}); m.Driller != "" {
		t.Fatalf("%+v", m)
	}
}
