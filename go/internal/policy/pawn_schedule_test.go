package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func hours(slots []string, def string) []int {
	var out []int
	for h, s := range slots {
		if s == def {
			out = append(out, h)
		}
	}
	return out
}

func TestScheduleTemplates(t *testing.T) {
	build := func(sleep []int, joy int) []string {
		slots := make([]string, 24)
		for h := range slots {
			slots[h] = ScheduleAnything
		}
		for _, h := range sleep {
			slots[h] = ScheduleSleep
		}
		slots[joy] = ScheduleJoy
		return slots
	}
	for name, c := range map[string]struct {
		effects TraitEffects
		want    []string
	}{
		"day":      {TraitEffects{}, build([]int{22, 23, 0, 1, 2, 3, 4, 5}, 21)},
		"quick":    {TraitEffects{QuickSleeper: true}, build([]int{0, 1, 2, 3, 4, 5}, 23)},
		"owl":      {TraitEffects{NightShift: true}, build([]int{10, 11, 12, 13, 14, 15, 16, 17}, 9)},
		"quickOwl": {TraitEffects{NightShift: true, QuickSleeper: true}, build([]int{11, 12, 13, 14, 15, 16}, 10)},
	} {
		if got := scheduleTemplate(c.effects); !sameSchedule(got, c.want) {
			t.Fatalf("%s: %v", name, got)
		}
	}
	if !sameSchedule(nativeDefaultSchedule(), []string{"Sleep", "Sleep", "Sleep", "Sleep", "Sleep", "Sleep", "Anything", "Anything", "Anything", "Anything", "Anything", "Anything", "Anything", "Anything", "Anything", "Anything", "Anything", "Anything", "Anything", "Anything", "Anything", "Anything", "Sleep", "Sleep"}) {
		t.Fatal(nativeDefaultSchedule())
	}
}

func TestPlanSchedules(t *testing.T) {
	owl := testWorkPawn("owl", true, false, nil, PawnTrait{Name: "NightOwl"})
	owl.Schedule = domain.Known(nativeDefaultSchedule())
	plain := testWorkPawn("plain", true, false, nil)
	plain.Schedule = domain.Known(scheduleTemplate(TraitEffects{}))
	edited := testWorkPawn("edited", true, false, nil)
	custom := nativeDefaultSchedule()
	custom[12] = ScheduleJoy
	edited.Schedule = domain.Known(custom)
	unknown := testWorkPawn("unknown", true, false, nil)
	away := testWorkPawn("away", true, false, nil)
	away.Available = domain.Known(false)
	away.Schedule = domain.Known(nativeDefaultSchedule())
	d := PlanSchedules([]WorkPawn{plain, owl, edited, unknown, away})
	if len(d.Schedules) != 3 || d.Schedules[1].Pawn != "owl" || d.Schedules[1].Matches || !sameSchedule(d.Schedules[1].Slots, scheduleTemplate(TraitEffects{NightShift: true})) {
		t.Fatal(d)
	}
	if d.Schedules[2].Pawn != "plain" || !d.Schedules[2].Matches {
		t.Fatal(d)
	}
	// A timetable edited by hand (under Manual) is replanned like any other
	// (#461): provenance is not authority over fresh planning.
	if d.Schedules[0].Pawn != "edited" || d.Schedules[0].Matches || !sameSchedule(d.Schedules[0].Slots, scheduleTemplate(TraitEffects{})) {
		t.Fatal(d.Schedules[0])
	}
	// A planner-written timetable is rewritten when the profile changes
	// (the owl read back its own night shift, then loses the trait).
	owl.Schedule = domain.Known(scheduleTemplate(TraitEffects{NightShift: true}))
	owl.Traits = domain.Known([]PawnTrait{})
	d = PlanSchedules([]WorkPawn{owl})
	if len(d.Schedules) != 1 || d.Schedules[0].Matches || !sameSchedule(d.Schedules[0].Slots, scheduleTemplate(TraitEffects{})) {
		t.Fatal(d)
	}
}
