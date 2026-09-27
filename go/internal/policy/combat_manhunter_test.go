package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// combatAnimal is a live manhunter animal of kind at cell with speed.
func combatAnimal(id PawnID, kind string, cell domain.Cell, speed float64) (SquadThreatFacts, DefensiveThreatFacts, CombatPawnState) {
	s, d := combatRaider(id, cell)
	s.Humanlike, s.Animal, s.Manhunter, s.BodySize = domain.Known(false), domain.Known(true), domain.Known(true), domain.Known(1.0)
	d.Humanlike = domain.Known(false)
	d.LordJobClass, d.LordToilClass = domain.Unknown[string](), domain.Unknown[string]()
	return s, d, CombatPawnState{ID: domain.PawnID(id), Cell: domain.Known(cell), Kind: kind, MoveSpeed: speed}
}

type animalFacts func() (SquadThreatFacts, DefensiveThreatFacts, CombatPawnState)

func animal(id PawnID, kind string, cell domain.Cell, speed float64) animalFacts {
	return func() (SquadThreatFacts, DefensiveThreatFacts, CombatPawnState) {
		return combatAnimal(id, kind, cell, speed)
	}
}

// withAnimals replaces a view's hostiles with the animals.
func withAnimals(view CombatView, animals ...animalFacts) CombatView {
	view.Threats, view.Positional = nil, nil
	for _, a := range animals {
		s, d, p := a()
		view.Threats = append(view.Threats, s)
		view.Positional = append(view.Positional, d)
		view.Pawns = append(view.Pawns, p)
	}
	return view
}

// withBrawlers adds brawlers standing back at z=30 from x=4.
func withBrawlers(view CombatView, brawlers ...SquadDefenderFacts) CombatView {
	for i, b := range brawlers {
		view.Defenders = append(view.Defenders, b)
		view.Pawns = append(view.Pawns, CombatPawnState{ID: b.ID, Cell: domain.Known(domain.Cell{X: int32(4 + i), Z: 30}), Stance: StanceIdle})
		view.Orderable = append(view.Orderable, b.ID)
	}
	return view
}

// Slow is below a colonist's 4.6 cells/s (unknown is not slow); boomalopes
// and boomrats explode.
func TestClassifyManhunterAnimal(t *testing.T) {
	for _, c := range []struct {
		kind  string
		speed float64
		want  AnimalClass
	}{
		{"Boomalope", 2.9, AnimalClass{Slow: true, Exploder: true}},
		{"Boomrat", 5.4, AnimalClass{Exploder: true}},
		{"Muffalo", 4.5, AnimalClass{Slow: true}},
		{"Wolf_Timber", 6.8, AnimalClass{}},
		{"Bear_Grizzly", 0, AnimalClass{}},
	} {
		if got := classifyAnimal(CombatPawnState{Kind: c.kind, MoveSpeed: c.speed}); got != c.want {
			t.Errorf("%s at %v: %+v, want %+v", c.kind, c.speed, got, c.want)
		}
	}
}

// {live hostiles all manhunter animals} -> TacticManhunter; one raider
// among them keeps the ordinary formation.
func TestDecideCombatManhunterPicksManhunterTactic(t *testing.T) {
	view := withAnimals(holdView(), animal("w1", "Wolf_Timber", domain.Cell{X: 9, Z: 5}, 6.8), animal("w2", "Wolf_Timber", domain.Cell{X: 10, Z: 4}, 6.8))
	if _, m := decideStop(t, view, StopEvent{}, CombatMemory{}); m.Tactic != TacticManhunter || len(m.Roles) != 3 {
		t.Fatalf("%+v", m)
	}
	s, d := combatRaider("r1", domain.Cell{X: 9, Z: 6})
	view.Threats, view.Positional = append(view.Threats, s), append(view.Positional, d)
	if _, m := decideStop(t, view, StopEvent{}, CombatMemory{}); m.Tactic == TacticManhunter {
		t.Fatalf("mixed raid: %+v", m)
	}
}

// {an exploder in the pack, brawlers} -> no brawler holds an exploder as
// its target; they take the wolf.
func TestDecideCombatManhunterNeverMeleeBlocksExploder(t *testing.T) {
	view := withBrawlers(holdView(), combatBrawler("d", .3), combatBrawler("e", .9))
	view = withAnimals(view, animal("b1", "Boomalope", domain.Cell{X: 9, Z: 5}, 2.9), animal("b2", "Boomrat", domain.Cell{X: 8, Z: 5}, 5.4), animal("w1", "Wolf_Timber", domain.Cell{X: 10, Z: 4}, 6.8))
	orders, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	if m.Tactic != TacticManhunter {
		t.Fatalf("%+v", m)
	}
	melee := 0
	for _, r := range m.Roles {
		if r.Ranged {
			continue
		}
		melee++
		if r.Target == "b1" || r.Target == "b2" || r.Duty == "" && r.Target != "w1" {
			t.Fatalf("brawler not on the wolf: %+v", m.Roles)
		}
	}
	if melee != 2 {
		t.Fatalf("%+v", m.Roles)
	}
	for _, o := range orders {
		if (o.Pawn == "d" || o.Pawn == "e") && o.Target != "w1" {
			t.Fatalf("%+v", orders)
		}
	}
}

// {exploder within 6 cells of a colonist} -> no gunner targets it;
// {exploder farther} -> focus fire takes it.
func TestDecideCombatManhunterShootsExplodersAwayFromPawns(t *testing.T) {
	far := withAnimals(holdView(), animal("b1", "Boomalope", domain.Cell{X: 9, Z: 5}, 2.9), animal("w1", "Wolf_Timber", domain.Cell{X: 10, Z: 4}, 6.8))
	_, m := decideStop(t, far, StopEvent{}, CombatMemory{})
	shot := false
	for _, r := range m.Roles {
		shot = shot || r.Target == "b1"
	}
	if !shot {
		t.Fatalf("far exploder not shot: %+v", m.Roles)
	}
	near := withAnimals(holdView(), animal("b1", "Boomalope", domain.Cell{X: 3, Z: 27}, 2.9), animal("w1", "Wolf_Timber", domain.Cell{X: 10, Z: 4}, 6.8))
	orders, m := decideStop(t, near, StopEvent{}, CombatMemory{})
	for _, r := range m.Roles {
		if r.Target == "b1" {
			t.Fatalf("exploder beside the line shot: %+v", m.Roles)
		}
	}
	for _, o := range orders {
		if o.Target == "b1" {
			t.Fatalf("%+v", orders)
		}
	}
}
