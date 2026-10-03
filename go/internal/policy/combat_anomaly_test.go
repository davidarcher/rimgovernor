package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// anomalyThreat is a live hostile pawn at cell carrying the Anomaly facts a.
func anomalyThreat(id PawnID, humanlike bool, a PawnAnomaly, cell domain.Cell) animalFacts {
	return func() (SquadThreatFacts, DefensiveThreatFacts, CombatPawnState) {
		s, d := combatRaider(id, cell)
		s.Humanlike, d.Humanlike = domain.Known(humanlike), domain.Known(humanlike)
		s.Animal = domain.Known(false)
		s.Anomaly = domain.Known(a)
		return s, d, CombatPawnState{ID: domain.PawnID(id), Cell: domain.Known(cell)}
	}
}

func flag(v bool) domain.Fact[bool] { return domain.Known(v) }

// shambler is a humanlike mutant that punches; fleshbeast an entity that bites.
func shambler(id PawnID, cell domain.Cell) animalFacts {
	return anomalyThreat(id, true, PawnAnomaly{Entity: flag(false), Mutant: flag(true), Shambler: flag(true), MeleeOnly: flag(true)}, cell)
}

func fleshbeast(id PawnID, cell domain.Cell) animalFacts {
	return anomalyThreat(id, false, PawnAnomaly{Entity: flag(true), Mutant: flag(false), Shambler: flag(false), MeleeOnly: flag(true)}, cell)
}

// {live hostiles all shamblers} -> the manhunter tactic, not the humanoid
// raid's wait; {fleshbeasts} -> the same; {an entity with a ranged or
// offensive-ability attack} or {one whose attack is unread} -> neither.
func TestDecideCombatMeleeEntityPackPicksManhunterTactic(t *testing.T) {
	for name, pack := range map[string][]animalFacts{
		"shamblers":   {shambler("s1", domain.Cell{X: 9, Z: 5}), shambler("s2", domain.Cell{X: 10, Z: 4})},
		"fleshbeasts": {fleshbeast("f1", domain.Cell{X: 9, Z: 5}), fleshbeast("f2", domain.Cell{X: 10, Z: 4})},
	} {
		view := withAnimals(holdView(), pack...)
		if _, m := decideStop(t, view, StopEvent{}, CombatMemory{}); m.Tactic != TacticManhunter || len(m.Roles) != 3 {
			t.Errorf("%s: %+v", name, m)
		}
	}
	shooter := anomalyThreat("n1", false, PawnAnomaly{Entity: flag(true), Mutant: flag(false), MeleeOnly: flag(false)}, domain.Cell{X: 9, Z: 5})
	unread := anomalyThreat("n2", false, PawnAnomaly{Entity: flag(true), Mutant: flag(false)}, domain.Cell{X: 9, Z: 5})
	for name, pack := range map[string][]animalFacts{"ranged": {shooter}, "unread": {unread}, "mixed": {shambler("s1", domain.Cell{X: 9, Z: 6}), shooter}} {
		if _, m := decideStop(t, withAnimals(holdView(), pack...), StopEvent{}, CombatMemory{}); m.Tactic == TacticManhunter {
			t.Errorf("%s took the pack tactic: %+v", name, m)
		}
	}
}

// A shambler assault is no humanoid raid: it never waits out the raid; a
// squad of raiders still is one (#1065).
func TestShamblersAreNoHumanoidRaid(t *testing.T) {
	view := withAnimals(holdView(), shambler("s1", domain.Cell{X: 9, Z: 5}))
	view.Layout = domain.Unknown[CombatLayout]()
	if humanoidRaid(view, CombatMemory{}) {
		t.Fatal("shamblers waited as a humanoid raid")
	}
	view = holdView()
	view.Layout = domain.Unknown[CombatLayout]()
	if !humanoidRaid(view, CombatMemory{}) {
		t.Fatal("raiders are a humanoid raid")
	}
}

// {a sightstealer hidden from the player among visible raiders} -> no role
// targets it; once revealed it ranks like any raider.
func TestHiddenThreatIsNeverATarget(t *testing.T) {
	hidden := anomalyThreat("h1", false, PawnAnomaly{Entity: flag(true), MeleeOnly: flag(true), HiddenFromPlayer: flag(true)}, domain.Cell{X: 9, Z: 4})
	view := withAnimals(holdView(), hidden, shambler("s1", domain.Cell{X: 9, Z: 6}))
	for _, h := range rankThreats(view) {
		if h.ID == "h1" {
			t.Fatalf("a hidden pawn ranked as a target: %+v", rankThreats(view))
		}
	}
	_, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	for _, r := range m.Roles {
		if r.Target == "h1" {
			t.Fatalf("a role targets the hidden pawn: %+v", m.Roles)
		}
	}
	revealed := anomalyThreat("h1", false, PawnAnomaly{Entity: flag(true), MeleeOnly: flag(true), HiddenFromPlayer: flag(false)}, domain.Cell{X: 9, Z: 4})
	if got := rankThreats(withAnimals(holdView(), revealed)); len(got) != 1 || got[0].ID != "h1" {
		t.Fatalf("a revealed pawn is a target: %+v", got)
	}
	if _, m := decideStop(t, withAnimals(holdView(), hidden), StopEvent{}, CombatMemory{}); m.Tactic != TacticManhunter {
		t.Fatalf("a hidden melee pack still counts as the pack: %+v", m)
	}
}

// {cultists, one the ritual's caster} -> the caster ranks ahead of the rest
// and every gunner shoots it; a caster no one can see is no target.
func TestRitualCasterIsFocusFired(t *testing.T) {
	caster := anomalyThreat("c1", true, PawnAnomaly{PsychicRitualInvoker: flag(true)}, domain.Cell{X: 12, Z: 3})
	view := withAnimals(holdView(), anomalyThreat("k1", true, PawnAnomaly{PsychicRitualInvoker: flag(false)}, domain.Cell{X: 9, Z: 5}), caster)
	if got := rankThreats(view); len(got) != 2 || got[0].ID != "c1" {
		t.Fatalf("the caster does not lead: %+v", got)
	}
	_, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	for _, r := range m.Roles {
		if r.Ranged && r.Target != "c1" {
			t.Fatalf("gunner %s not on the caster: %+v", r.Pawn, m.Roles)
		}
	}
}

// {a ritual caster in mortar range, no colonist near} -> HE on the caster;
// a colonist within the safe radius of it, or the caster dead, -> no aim.
func TestMortarShellsTheRitualCaster(t *testing.T) {
	caster := anomalyThreat("c1", true, PawnAnomaly{PsychicRitualInvoker: flag(true)}, domain.Cell{X: 12, Z: -40})
	view := withAnimals(holdView(), caster)
	mortar := CombatMortar{ID: "Thing_Turret_Mortar1", Cell: domain.Cell{X: 5, Z: 30}, MinRange: 29.9, MaxRange: 500}
	aim, shell, ok := mortarAim(view, mortar)
	if !ok || aim != (domain.Cell{X: 12, Z: -40}) || shell != ShellHE {
		t.Fatalf("aim %+v %s %v", aim, shell, ok)
	}
	near := withAnimals(holdView(), anomalyThreat("c1", true, PawnAnomaly{PsychicRitualInvoker: flag(true)}, domain.Cell{X: 3, Z: 27}))
	if _, _, ok := mortarAim(near, mortar); ok {
		t.Fatal("shelled a caster beside our pawns")
	}
	dead := withAnimals(holdView(), caster)
	dead.Pawns[len(dead.Pawns)-1].Dead = true
	if _, _, ok := mortarAim(dead, mortar); ok {
		t.Fatal("shelled a dead caster")
	}
}
