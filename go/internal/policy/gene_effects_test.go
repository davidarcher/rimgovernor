package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func genePawn(id string, effects GeneEffects) WorkPawn {
	p := testWorkPawn(PawnID(id), true, false, nil)
	p.Schedule = domain.Known(scheduleTemplate(TraitEffects{}))
	p.Biotech = domain.Known(PawnBiotech{Effects: domain.Known(effects)})
	return p
}

// TestGeneStatEffectsFoldIntoProfile (#1689): gene offsets add to the trait
// offsets and gene factors scale the sum, for work speed and learning.
func TestGeneStatEffectsFoldIntoProfile(t *testing.T) {
	p := testWorkPawn(PawnID("a"), true, false, nil, testTrait("Industrious", 0))
	base := BuildProfile(p).Effects.WorkSpeed
	p.Biotech = domain.Known(PawnBiotech{Effects: domain.Known(GeneEffects{Stats: map[string]StatModifier{
		"WorkSpeedGlobal":      {Offset: .1, Factor: 1.2},
		"GlobalLearningFactor": {Factor: .5},
	}})})
	got := BuildProfile(p).Effects
	if want := (1+base+.1)*1.2 - 1; got.WorkSpeed != want {
		t.Fatalf("work speed %v, want %v", got.WorkSpeed, want)
	}
	if got.LearnRate != -.5 {
		t.Fatalf("learn rate %v, want -0.5", got.LearnRate)
	}
	if (GeneEffects{}).Stat("MoveSpeed") != (StatModifier{Factor: 1}) {
		t.Fatal("untouched stat is not the identity")
	}
}

// TestGeneDisabledNeedsShapeSchedule (#1689): an active gene that removes
// the Rest or Joy need leaves no Sleep or Joy block; one that gives the need
// back keeps it.
func TestGeneDisabledNeedsShapeSchedule(t *testing.T) {
	plan := func(e GeneEffects) []string {
		d := PlanSchedules([]WorkPawn{genePawn("p", e)}, domain.Unknown[ComfortObservation](), false)
		if len(d.Schedules) != 1 {
			t.Fatal(d)
		}
		return d.Schedules[0].Slots
	}
	if s := plan(GeneEffects{}); len(hours(s, ScheduleSleep)) == 0 || len(hours(s, ScheduleJoy)) == 0 {
		t.Fatal("baseline pawn needs Sleep and Joy", s)
	}
	s := plan(GeneEffects{DisabledNeeds: map[string]bool{"Rest": true}})
	if len(hours(s, ScheduleSleep)) != 0 || len(hours(s, ScheduleJoy)) == 0 {
		t.Fatal("Rest-less pawn keeps Sleep or loses Joy", s)
	}
	s = plan(GeneEffects{DisabledNeeds: map[string]bool{"Joy": true}})
	if len(hours(s, ScheduleJoy)) != 0 || len(hours(s, ScheduleSleep)) == 0 {
		t.Fatal("Joy-less pawn keeps Joy or loses Sleep", s)
	}
	s = plan(GeneEffects{DisabledNeeds: map[string]bool{"Rest": true}, EnabledNeeds: map[string]bool{"Rest": true}})
	if len(hours(s, ScheduleSleep)) == 0 {
		t.Fatal("a gene giving Rest back must keep Sleep", s)
	}
}
