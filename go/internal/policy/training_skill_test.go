package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestTrainingSkillMirrorsTheNativeChoice(t *testing.T) {
	t.Parallel()
	with := func(melee, shooting ProfileSkill) PawnProfile {
		melee.Name, shooting.Name = "Melee", "Shooting"
		return PawnProfile{Skills: map[string]ProfileSkill{"Melee": melee, "Shooting": shooting}}
	}
	brawler := with(ProfileSkill{Level: 3}, ProfileSkill{Level: 9})
	brawler.Effects.MeleeOnly = true
	for name, tc := range map[string]struct {
		p    PawnProfile
		want string
		ok   bool
	}{
		"higher level wins":      {with(ProfileSkill{Level: 14}, ProfileSkill{Level: 6}), "Melee", true},
		"passion breaks a tie":   {with(ProfileSkill{Level: 6, Passion: "Major"}, ProfileSkill{Level: 6, Passion: "Minor"}), "Melee", true},
		"shooting wins the tie":  {with(ProfileSkill{Level: 6}, ProfileSkill{Level: 6}), "Shooting", true},
		"brawler never shoots":   {brawler, "Melee", true},
		"disabled shooting":      {with(ProfileSkill{Level: 1}, ProfileSkill{Level: 9, Disabled: true}), "Melee", true},
		"disabled melee":         {with(ProfileSkill{Level: 9, Disabled: true}, ProfileSkill{Level: 1}), "Shooting", true},
		"neither is usable":      {with(ProfileSkill{Disabled: true}, ProfileSkill{Disabled: true}), "", false},
		"an unread row is unset": {PawnProfile{Skills: map[string]ProfileSkill{"Melee": {Name: "Melee", Level: 3}}}, "", false},
	} {
		if got, ok := TrainingSkill(tc.p); got != tc.want || ok != tc.ok {
			t.Errorf("%s: %q %v, want %q %v", name, got, ok, tc.want, tc.ok)
		}
	}
}

func TestTrainingDemandIsPerSkillCeiling(t *testing.T) {
	t.Parallel()
	none := domain.Known(ResearchFacts{})
	finished := func(ids ...ResearchProjectID) domain.Fact[ResearchFacts] {
		return domain.Known(ResearchFacts{Finished: ids})
	}
	count := func(f domain.Fact[int]) int { n, _ := f.Value(); return n }
	melee := func(id string) PawnProfile { return combatant(id, 3, 1) }
	shooter := func(id string) PawnProfile { return combatant(id, 1, 3) }

	pair := domain.Known([]PawnProfile{melee("a"), melee("b")})
	if count(TrainingRings(pair, none)) != RingMinMarkers || count(TrainingStands(pair, none)) != 0 {
		t.Fatal("a melee-only pair asks for a ring and no range")
	}
	lone := domain.Known([]PawnProfile{melee("a"), shooter("s")})
	if count(TrainingRings(lone, none)) != 0 {
		t.Fatal("one melee pawn has no partner")
	}
	if count(TrainingStands(lone, none)) == 0 {
		t.Fatal("the shooter asks for stands")
	}

	shot := domain.Known([]PawnProfile{combatant("s", 1, 12)})
	if count(TrainingStands(shot, finished("Gunsmithing"))) != 0 || count(TrainingStands(shot, finished("Gunsmithing", "ChargedShot"))) == 0 {
		t.Fatal("a shooter below 20 asks for stands once its tier is researched")
	}
	pairAt9 := domain.Known([]PawnProfile{combatant("a", 9, 1), combatant("b", 9, 1)})
	if count(TrainingRings(pairAt9, none)) != 0 || count(TrainingRings(pairAt9, finished("Smithing"))) == 0 {
		t.Fatal("melee 9 is past the club ceiling and below the sword's")
	}

	both := domain.Known([]PawnProfile{combatant("a", 14, 6), combatant("b", 14, 6)})
	if count(TrainingRings(both, none)) != 0 || count(TrainingStands(both, none)) != 0 {
		t.Fatal("melee 14 is judged on melee, past the club ceiling")
	}

	var many []PawnProfile
	for i := range 20 {
		many = append(many, melee(string(rune('a'+i))))
	}
	if count(TrainingRings(domain.Known(many), none)) != RingMaxMarkers {
		t.Fatal("markers cap at the maximum")
	}
	if _, known := TrainingRings(domain.Unknown[[]PawnProfile](), none).Value(); known {
		t.Fatal("unread profiles stay unknown")
	}
}
