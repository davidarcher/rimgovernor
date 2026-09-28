package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// bleedView is a squad fight (no layout): riflemen a-d against raiders r1
// and r2. r1 is bleeding (downs in ~6 h, dies in 20 h) and 40 cells from
// the nearest colonist with a 25-cell weapon; r2 is healthy and charging.
func bleedView() CombatView {
	s1, d1 := combatRaider("r1", domain.Cell{X: 9, Z: 5})
	s2, d2 := combatRaider("r2", domain.Cell{X: 10, Z: 4})
	d2.NearestColonistDistance = domain.Known(5.0)
	return CombatView{
		Tick:       100,
		Population: domain.Known(4),
		Defenders:  []SquadDefenderFacts{combatRifleman("a"), combatRifleman("b"), combatRifleman("c"), combatRifleman("d")},
		Threats:    []SquadThreatFacts{s1, s2},
		Positional: []DefensiveThreatFacts{d1, d2},
		Pawns: []CombatPawnState{
			{ID: "a", Cell: domain.Known(domain.Cell{X: 1, Z: 30}), Stance: StanceIdle},
			{ID: "b", Cell: domain.Known(domain.Cell{X: 2, Z: 30}), Stance: StanceIdle},
			{ID: "c", Cell: domain.Known(domain.Cell{X: 3, Z: 30}), Stance: StanceIdle},
			{ID: "d", Cell: domain.Known(domain.Cell{X: 4, Z: 30}), Stance: StanceIdle},
			{ID: "r1", Cell: domain.Known(domain.Cell{X: 9, Z: 5}), WeaponRange: 25,
				BloodLoss: domain.Known(0.35), BleedRatePerDay: domain.Known(1.0), HoursUntilBleedDeath: domain.Known(20.0)},
			{ID: "r2", Cell: domain.Known(domain.Cell{X: 10, Z: 4}), Target: "a", WeaponRange: 25},
		},
		Orderable: []domain.PawnID{"a", "b", "c", "d"},
	}
}

func targeted(m CombatMemory, id domain.PawnID) bool {
	return slices.ContainsFunc(m.Roles, func(r CombatRole) bool { return r.Target == id })
}

func TestContainedBleedingRaiderGetsNoAttacker(t *testing.T) {
	_, m := decideStop(t, bleedView(), StopEvent{}, CombatMemory{})
	if targeted(m, "r1") || !targeted(m, "r2") {
		t.Fatalf("%+v", m.Roles)
	}
}

func TestUncontainedBleedingRaiderIsAttacked(t *testing.T) {
	v := bleedView()
	v.Pawns[4].Target = "b" // shooting at a colonist
	if Contained(v, "r1") {
		t.Fatal("a raider targeting a colonist is contained")
	}
	_, m := decideStop(t, v, StopEvent{}, CombatMemory{})
	if !targeted(m, "r1") {
		t.Fatalf("%+v", m.Roles)
	}
}

func TestBleedingRaiderAttackedAtPopulationTarget(t *testing.T) {
	v := bleedView()
	v.Population = domain.Known(domain.PopulationTarget)
	_, m := decideStop(t, v, StopEvent{}, CombatMemory{})
	if !targeted(m, "r1") {
		t.Fatalf("%+v", m.Roles)
	}
}

func TestUnknownHealthRaiderIsAttacked(t *testing.T) {
	v := bleedView()
	v.Pawns[4].HoursUntilBleedDeath = domain.Unknown[float64]()
	_, m := decideStop(t, v, StopEvent{}, CombatMemory{})
	if !targeted(m, "r1") {
		t.Fatalf("%+v", m.Roles)
	}
}

func TestRaiderDyingBeforeDowningIsAttacked(t *testing.T) {
	if willBleedDown(CombatPawnState{BloodLoss: domain.Known(0.3), BleedRatePerDay: domain.Known(4.0), HoursUntilBleedDeath: domain.Known(1.0)}) {
		t.Fatal("dies in 1 h but downs in 1.8 h")
	}
}

// A lone fleeing, bleeding raider is left to go down: the gunners already
// shooting it stop and hold fire, and no attack names it.
func TestLoneFleeingBleederIsLeftToGoDown(t *testing.T) {
	s1, d1 := combatRaider("r1", domain.Cell{X: 9, Z: 5})
	d1.LordToilClass = domain.Known("LordToil_PanicFlee")
	d1.NearestColonistDistance = domain.Known(10.0)
	view := CombatView{
		Tick:       200,
		Population: domain.Known(3),
		Defenders:  []SquadDefenderFacts{combatRifleman("a")},
		Threats:    []SquadThreatFacts{s1},
		Positional: []DefensiveThreatFacts{d1},
		Pawns: []CombatPawnState{
			{ID: "a", Cell: domain.Known(domain.Cell{X: 1, Z: 10}), Stance: StanceWarmup, Target: "r1", FireMode: FireAtWill},
			{ID: "r1", Cell: domain.Known(domain.Cell{X: 9, Z: 5}), Stance: StanceMoving, WeaponRange: 25,
				BloodLoss: domain.Known(0.5), BleedRatePerDay: domain.Known(2.0), HoursUntilBleedDeath: domain.Known(6.0)},
		},
		Orderable: []domain.PawnID{"a"},
	}
	memory := CombatMemory{Tactic: TacticSquad, Formed: 100, Roles: []CombatRole{{Pawn: "a", Target: "r1", Ranged: true}}}
	orders, next := decideStop(t, view, StopEvent{}, memory)
	if targeted(next, "r1") || slices.ContainsFunc(orders, func(o CombatOrder) bool { return o.Kind == OrderAttack }) {
		t.Fatalf("%+v %+v", orders, next.Roles)
	}
	if !slices.ContainsFunc(orders, func(o CombatOrder) bool { return o.Kind == OrderFireMode && o.FireMode == HoldFire }) {
		t.Fatalf("no hold fire: %+v", orders)
	}
}
