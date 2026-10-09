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

func TestTrainingReviewCountsTheGapAgainstTheBestCombatSkill(t *testing.T) {
	t.Parallel()
	pacifist := PawnProfile{ID: "p", Skills: map[string]ProfileSkill{"Melee": {Name: "Melee", Disabled: true}, "Shooting": {Name: "Shooting", Disabled: true}}}
	child := combatant("kid", 0, 0)
	child.Child = true
	meleeOnly := PawnProfile{ID: "m", Skills: map[string]ProfileSkill{"Melee": {Name: "Melee", Level: 3}, "Shooting": {Name: "Shooting", Disabled: true}}}
	for _, tc := range []struct {
		name     string
		profiles domain.Fact[[]PawnProfile]
		want     domain.Fact[TrainingReview]
	}{
		{"at the target is no gap", domain.Known([]PawnProfile{combatant("a", TrainingSkillTarget, 0)}), domain.Known(TrainingReview{Capable: 1})},
		{"one below the target is a gap", domain.Known([]PawnProfile{combatant("a", TrainingSkillTarget-1, TrainingSkillTarget-2)}), domain.Known(TrainingReview{Capable: 1, Below: 1})},
		{"best skill decides", domain.Known([]PawnProfile{combatant("a", 2, TrainingSkillTarget+3), combatant("b", 1, 1)}), domain.Known(TrainingReview{Capable: 2, Below: 1})},
		{"a disabled skill is skipped", domain.Known([]PawnProfile{meleeOnly}), domain.Known(TrainingReview{Capable: 1, Below: 1})},
		{"no combat-capable colonists", domain.Known([]PawnProfile{pacifist, child}), domain.Known(TrainingReview{})},
		{"no colonists", domain.Known([]PawnProfile{}), domain.Known(TrainingReview{})},
		{"unread profiles", domain.Unknown[[]PawnProfile](), domain.Unknown[TrainingReview]()},
		{"a pawn with unread skills and no gap elsewhere is unknown", domain.Known([]PawnProfile{{ID: "x"}, combatant("a", 15, 15)}), domain.Unknown[TrainingReview]()},
		{"a gap is a gap despite an unread pawn", domain.Known([]PawnProfile{{ID: "x"}, combatant("a", 1, 1)}), domain.Known(TrainingReview{Capable: 1, Below: 1})},
	} {
		got, gk := ReviewTraining(tc.profiles).Value()
		want, wk := tc.want.Value()
		if gk != wk || got != want {
			t.Errorf("%s: %+v (known %v), want %+v (known %v)", tc.name, got, gk, want, wk)
		}
	}
}

func TestTrainingConcernRaisesTheRangeDeficit(t *testing.T) {
	t.Parallel()
	f := stableRounds()
	f.WorkProfiles = domain.Known([]PawnProfile{combatant("a", 2, 3), combatant("b", 12, 4)})
	findings, err := InspectRounds(f, RoundsLatches{}, DefaultRoundsPolicy())
	if err != nil {
		t.Fatal(err)
	}
	var raised *RoundsConcern
	for i, c := range findings.Concerns {
		if c.ID == MaintainTraining {
			raised = &findings.Concerns[i]
		}
	}
	if raised == nil {
		t.Fatal("an open skill gap raised no MaintainTraining deficit")
	}
	if share, _ := raised.Deficit.Value(); share != 0.5 {
		t.Fatalf("deficit %v, want half the colonists", share)
	}
	if DepartmentOf(MaintainTraining) != DepartmentMilitary || ConcernTypeOf(MaintainTraining) != StandardConcern {
		t.Fatal("MaintainTraining is a Military Standard")
	}

	// The open gap reaches layout as a range the plan lacks, through the
	// shared store declaration.
	view := StoreView{TrainingGap: TrainingGap(f.WorkProfiles)}
	demand := DeclareStores(view).Apply(RoomDemand{})
	if demand.Ranges != 1 {
		t.Fatalf("open gap demands %d ranges, want 1", demand.Ranges)
	}
	plan, _ := rangePlan(t)
	if RangesOwed(LayoutPlan{}, demand) != 1 || RangesOwed(plan, demand) != 0 {
		t.Fatalf("owed %d without a range and %d with one", RangesOwed(LayoutPlan{}, demand), RangesOwed(plan, demand))
	}
}

func TestTrainingConcernRaisesNothingWithoutAGap(t *testing.T) {
	t.Parallel()
	for name, profiles := range map[string]domain.Fact[[]PawnProfile]{
		"everyone at the target": domain.Known([]PawnProfile{combatant("a", TrainingSkillTarget, 0)}),
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
		if demand := DeclareStores(StoreView{TrainingGap: TrainingGap(profiles)}).Apply(RoomDemand{}); demand.Ranges != 0 {
			t.Errorf("%s demands %d ranges", name, demand.Ranges)
		}
	}
}
