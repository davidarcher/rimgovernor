package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func squadThreat(id PawnID, ranged bool) SquadThreatFacts {
	return SquadThreatFacts{ID: id, Dead: domain.Known(false), Downed: domain.Known(false), Humanlike: domain.Known(true), Animal: domain.Known(false), RangedEquipped: domain.Known(ranged)}
}
func squadDefender(id domain.PawnID, ranged bool) SquadDefenderFacts {
	return SquadDefenderFacts{ID: id, Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), PlayerForced: domain.Known(false), QueuedJobs: domain.Known(uint32(0)), ViolenceCapable: domain.Known(true), NeedsTend: domain.Known(false), HealthFraction: domain.Known(1.0), RangedEquipped: domain.Known(ranged), MeleeEquipped: domain.Known(true), Armed: domain.Known(true)}
}

func TestSelectSquadDefenseAssignsTwoPerMeleeOpponent(t *testing.T) {
	threats := []SquadThreatFacts{squadThreat("raider", false)}
	defenders := []SquadDefenderFacts{squadDefender("a", false), squadDefender("b", false)}
	assignments, ok := SelectSquadDefense(threats, defenders)
	if !ok || len(assignments) != 2 {
		t.Fatal(assignments, ok)
	}
	for _, a := range assignments {
		if a.Target != "raider" || a.Mode != SquadMelee {
			t.Fatal(a)
		}
	}
}

func TestSelectSquadDefenseRequiresRangedDefenderForRangedOpponent(t *testing.T) {
	threats := []SquadThreatFacts{squadThreat("shooter", true)}
	// Two melee-only defenders cannot engage a ranged opponent.
	defenders := []SquadDefenderFacts{squadDefender("a", false), squadDefender("b", false)}
	if assignments, ok := SelectSquadDefense(threats, defenders); ok || len(assignments) != 0 {
		t.Fatal(assignments, ok)
	}
	defenders = append(defenders, squadDefender("c", true), squadDefender("d", true))
	assignments, ok := SelectSquadDefense(threats, defenders)
	if !ok || len(assignments) != 2 {
		t.Fatal(assignments, ok)
	}
	for _, a := range assignments {
		if a.Mode != SquadRanged {
			t.Fatal(a)
		}
	}
}

func TestSelectSquadDefenseBoundsOpponentsAndDefenders(t *testing.T) {
	var threats []SquadThreatFacts
	for i := 0; i < 6; i++ {
		threats = append(threats, squadThreat(PawnID(rune('a'+i)), false))
	}
	var defenders []SquadDefenderFacts
	for i := 0; i < 12; i++ {
		defenders = append(defenders, squadDefender(domain.PawnID(rune('A'+i)), false))
	}
	assignments, ok := SelectSquadDefense(threats, defenders)
	if !ok || len(assignments) != maxSquadDefenders {
		t.Fatal(assignments, ok)
	}
	targets := map[PawnID]int{}
	for _, a := range assignments {
		targets[a.Target]++
	}
	if len(targets) != maxSquadOpponents {
		t.Fatal("engaged more than the bounded opponent count", targets)
	}
	for _, count := range targets {
		if count != requiredDefendersPerFoe {
			t.Fatal("opponent did not get exactly the required defenders", targets)
		}
	}
}

func TestSelectSquadDefenseExcludesIneligibleCandidates(t *testing.T) {
	downedThreat := squadThreat("downed", false)
	downedThreat.Downed = domain.Known(true)
	deadDefender := squadDefender("dead", false)
	deadDefender.Dead = domain.Known(true)
	woundedDefender := squadDefender("wounded", false)
	woundedDefender.HealthFraction = domain.Known(float64(float32(0.5005)))
	fine1, fine2 := squadDefender("fine1", false), squadDefender("fine2", false)

	if _, ok := SelectSquadDefense([]SquadThreatFacts{downedThreat}, []SquadDefenderFacts{fine1, fine2}); ok {
		t.Fatal("selected a downed opponent")
	}
	live := squadThreat("live", false)
	if _, ok := SelectSquadDefense([]SquadThreatFacts{live}, []SquadDefenderFacts{deadDefender, woundedDefender}); ok {
		t.Fatal("selected ineligible defenders")
	}
	assignments, ok := SelectSquadDefense([]SquadThreatFacts{live}, []SquadDefenderFacts{deadDefender, woundedDefender, fine1, fine2})
	if !ok || len(assignments) != 2 {
		t.Fatal(assignments, ok)
	}
	for _, a := range assignments {
		if a.Defender == "dead" || a.Defender == "wounded" {
			t.Fatal("assigned an ineligible defender", a)
		}
	}
}

func squadAnimalThreat(id PawnID, bodySize float64, manhunter bool) SquadThreatFacts {
	return SquadThreatFacts{ID: id, Dead: domain.Known(false), Downed: domain.Known(false), Humanlike: domain.Known(false), Animal: domain.Known(true), BodySize: domain.Known(bodySize), Manhunter: domain.Known(manhunter), RangedEquipped: domain.Known(false)}
}

func TestSelectSquadDefenseAdmitsSmallManhunterAnimal(t *testing.T) {
	threats := []SquadThreatFacts{squadAnimalThreat("wolf", 1.4, true)}
	defenders := []SquadDefenderFacts{squadDefender("a", false), squadDefender("b", false)}
	assignments, ok := SelectSquadDefense(threats, defenders)
	if !ok || len(assignments) != 2 {
		t.Fatal(assignments, ok)
	}
	for _, a := range assignments {
		if a.Target != "wolf" || a.Mode != SquadMelee {
			t.Fatal(a)
		}
	}
}

func TestSelectSquadDefenseAdmitsAHuntingPredator(t *testing.T) {
	bear := squadAnimalThreat("bear", 2.15, false)
	bear.Hunting = domain.Known(true)
	defenders := []SquadDefenderFacts{squadDefender("a", false), squadDefender("b", false)}
	assignments, ok := SelectSquadDefense([]SquadThreatFacts{bear}, defenders)
	if !ok || len(assignments) != 2 || assignments[0].Target != "bear" {
		t.Fatal(assignments, ok)
	}
}

func TestSelectSquadDefenseExcludesNonManhunterAnimal(t *testing.T) {
	threats := []SquadThreatFacts{squadAnimalThreat("deer", 1.4, false)}
	defenders := []SquadDefenderFacts{squadDefender("a", false), squadDefender("b", false)}
	if _, ok := SelectSquadDefense(threats, defenders); ok {
		t.Fatal("selected a non-manhunter animal")
	}
}

func TestSelectSquadDefenseExcludesOversizedOrZeroSizedAnimal(t *testing.T) {
	defenders := []SquadDefenderFacts{squadDefender("a", false), squadDefender("b", false)}
	oversized := []SquadThreatFacts{squadAnimalThreat("elephant", 4.5, true)}
	if _, ok := SelectSquadDefense(oversized, defenders); ok {
		t.Fatal("selected an oversized manhunter animal")
	}
	zeroSized := []SquadThreatFacts{squadAnimalThreat("unknown-size", 0, true)}
	if _, ok := SelectSquadDefense(zeroSized, defenders); ok {
		t.Fatal("selected a zero body-size animal")
	}
}

func TestSelectSquadDefenseNoEligibleThreatOrDefender(t *testing.T) {
	if _, ok := SelectSquadDefense(nil, []SquadDefenderFacts{squadDefender("a", false)}); ok {
		t.Fatal("selected with no threats")
	}
	if _, ok := SelectSquadDefense([]SquadThreatFacts{squadThreat("raider", false)}, nil); ok {
		t.Fatal("selected with no defenders")
	}
}

func TestSelectTribalRaiderDefenseRequiresThreeArmedHealthyDefenders(t *testing.T) {
	threat := squadThreat("raider", false)
	defenders := []SquadDefenderFacts{squadDefender("a", false), squadDefender("b", false)}
	if _, ok := SelectTribalRaiderDefense(threat, defenders); ok {
		t.Fatal("selected with only two defenders")
	}
	defenders = append(defenders, squadDefender("c", true))
	assignments, ok := SelectTribalRaiderDefense(threat, defenders)
	if !ok || len(assignments) != 3 {
		t.Fatal(assignments, ok)
	}
	for _, a := range assignments {
		if a.Target != "raider" {
			t.Fatal(a)
		}
	}
	// The already-ranged defender sorts first (prefer ranged, per the sort
	// key), so it is assigned SquadRanged; melee-only ones get SquadMelee.
	modes := map[SquadMode]int{}
	for _, a := range assignments {
		modes[a.Mode]++
	}
	if modes[SquadRanged] != 1 || modes[SquadMelee] != 2 {
		t.Fatal(modes)
	}
}

func TestSelectTribalRaiderDefenseExcludesUnarmedUnhealthyOrRangedThreat(t *testing.T) {
	defenders := []SquadDefenderFacts{squadDefender("a", false), squadDefender("b", false), squadDefender("c", false)}
	if _, ok := SelectTribalRaiderDefense(squadThreat("shooter", true), defenders); ok {
		t.Fatal("selected a ranged threat")
	}
	unarmed := squadDefender("unarmed", false)
	unarmed.Armed = domain.Known(false)
	wounded := squadDefender("wounded", false)
	wounded.HealthFraction = domain.Known(0.5)
	pool := []SquadDefenderFacts{unarmed, wounded, squadDefender("fine", false)}
	if _, ok := SelectTribalRaiderDefense(squadThreat("raider", false), pool); ok {
		t.Fatal("selected with only one eligible defender")
	}
}

func TestSelectSquadDefensePrefersTheLineByOpponent(t *testing.T) {
	front := squadDefender("a", true)
	front.FrontLine = true
	shooter := squadDefender("b", true)
	// A melee opponent goes to the line holder first, a ranged one to the
	// shooter first; the tribal branch keeps ranged first, then the line.
	melee := squadDefender("d", false)
	melee.FrontLine = true
	assignments, ok := SelectSquadDefense([]SquadThreatFacts{squadThreat("raider", false)}, []SquadDefenderFacts{front, shooter, squadDefender("c", false), melee})
	if !ok || len(assignments) != 2 || assignments[0].Defender != "a" || assignments[1].Defender != "d" {
		t.Fatal(assignments, ok)
	}
	assignments, ok = SelectSquadDefense([]SquadThreatFacts{squadThreat("raider", true)}, []SquadDefenderFacts{front, shooter, squadDefender("c", true)})
	if !ok || len(assignments) != 2 || assignments[0].Defender != "b" || assignments[1].Defender != "c" {
		t.Fatal(assignments, ok)
	}
	assignments, ok = SelectTribalRaiderDefense(squadThreat("raider", false), []SquadDefenderFacts{squadDefender("c", false), melee, front, shooter})
	if !ok || len(assignments) != 3 || assignments[0].Defender != "a" || assignments[1].Defender != "b" || assignments[2].Defender != "d" {
		t.Fatal(assignments, ok)
	}
}

// A drafted pawn is busy only under an owned claim; a standing draft nobody
// claims (the player's, made under Manual) is a candidate, and a draft whose
// claim cannot be read is not (#461).
func TestSquadDefenderEligibleDistinguishesOwnedDrafts(t *testing.T) {
	owned := squadDefender("a", false)
	owned.Drafted, owned.DraftOwned = domain.Known(true), domain.Known(true)
	unowned := squadDefender("b", false)
	unowned.Drafted, unowned.DraftOwned = domain.Known(true), domain.Known(false)
	unknown := squadDefender("c", false)
	unknown.Drafted = domain.Known(true)
	if squadDefenderEligible(owned) || !squadDefenderEligible(unowned) || squadDefenderEligible(unknown) {
		t.Fatal(squadDefenderEligible(owned), squadDefenderEligible(unowned), squadDefenderEligible(unknown))
	}
	assignments, ok := SelectSquadDefense([]SquadThreatFacts{squadThreat("raider", false)}, []SquadDefenderFacts{owned, unowned, unknown, squadDefender("d", false)})
	if !ok || len(assignments) != 2 || assignments[0].Defender != "b" || assignments[1].Defender != "d" {
		t.Fatal(assignments, ok)
	}
}
