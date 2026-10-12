package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func combatant(id string, melee, shooting int) PawnProfile {
	return PawnProfile{ID: PawnID(id), Skills: map[string]ProfileSkill{
		"Melee":    {Name: "Melee", Level: melee},
		"Shooting": {Name: "Shooting", Level: shooting},
	}}
}

func TestTrainingReviewCountsTheGapAgainstShooting(t *testing.T) {
	t.Parallel()
	pacifist := PawnProfile{ID: "p", Skills: map[string]ProfileSkill{"Melee": {Name: "Melee", Disabled: true}, "Shooting": {Name: "Shooting", Disabled: true}}}
	child := combatant("kid", 0, 0)
	child.Child = true
	meleeOnly := PawnProfile{ID: "m", Skills: map[string]ProfileSkill{"Melee": {Name: "Melee", Level: 3}, "Shooting": {Name: "Shooting", Disabled: true}}}
	brawler := combatant("b", 3, 0)
	brawler.Effects.MeleeOnly = true
	for _, tc := range []struct {
		name     string
		profiles domain.Fact[[]PawnProfile]
		want     domain.Fact[TrainingReview]
	}{
		{"at the target is no gap", domain.Known([]PawnProfile{combatant("a", 0, TrainingTiers[0].Ceiling)}), domain.Known(TrainingReview{Capable: 1})},
		{"one below the target is a gap", domain.Known([]PawnProfile{combatant("a", TrainingTiers[0].Ceiling-2, TrainingTiers[0].Ceiling-1)}), domain.Known(TrainingReview{Capable: 1, Below: 1, ShootingBelow: 1})},
		{"each pawn is judged on its chosen skill", domain.Known([]PawnProfile{combatant("a", 0, TrainingTiers[0].Ceiling+3), combatant("b", 20, 1)}), domain.Known(TrainingReview{Capable: 2})},
		{"a pawn who cannot shoot trains melee", domain.Known([]PawnProfile{meleeOnly, brawler}), domain.Known(TrainingReview{Capable: 2, Below: 2, MeleeBelow: 2})},
		{"no combat-capable colonists", domain.Known([]PawnProfile{pacifist, child}), domain.Known(TrainingReview{})},
		{"no colonists", domain.Known([]PawnProfile{}), domain.Known(TrainingReview{})},
		{"unread profiles", domain.Unknown[[]PawnProfile](), domain.Unknown[TrainingReview]()},
		{"a pawn with unread skills and no gap elsewhere is unknown", domain.Known([]PawnProfile{{ID: "x"}, combatant("a", 15, 15)}), domain.Unknown[TrainingReview]()},
		{"a gap is a gap despite an unread pawn", domain.Known([]PawnProfile{{ID: "x"}, combatant("a", 1, 1)}), domain.Known(TrainingReview{Capable: 1, Below: 1, ShootingBelow: 1})},
	} {
		got, gk := ReviewTraining(tc.profiles, domain.Unknown[ResearchFacts]()).Value()
		want, wk := tc.want.Value()
		if gk != wk || got != want {
			t.Errorf("%s: %+v (known %v), want %+v (known %v)", tc.name, got, gk, want, wk)
		}
	}
}

func TestTrainingConcernRaisesNothingWithoutAGap(t *testing.T) {
	t.Parallel()
	for name, profiles := range map[string]domain.Fact[[]PawnProfile]{
		"everyone at the target": domain.Known([]PawnProfile{combatant("a", 0, TrainingTiers[0].Ceiling)}),
		"no combatants":          domain.Known([]PawnProfile{}),
		"unread":                 domain.Unknown[[]PawnProfile](),
	} {
		f := stableRounds()
		f.WorkProfiles = profiles
		findings, err := InspectRounds(f, RoundsLatches{}, DefaultRoundsPolicy())
		if err != nil {
			t.Fatal(err)
		}
		for _, c := range findings.Concerns {
			if c.ID == MaintainTraining {
				t.Errorf("%s raised MaintainTraining", name)
			}
		}
		if demand := DeclareStores(StoreView{TrainingStands: TrainingStands(profiles, domain.Unknown[ResearchFacts]())}).Apply(RoomDemand{}); demand.Ranges != 0 {
			t.Errorf("%s demands %d ranges", name, demand.Ranges)
		}
	}
}
