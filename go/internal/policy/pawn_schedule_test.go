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
	day := scheduleTemplate(TraitEffects{})
	if len(day) != 24 || len(hours(day, ScheduleSleep)) != 8 || day[22] != ScheduleSleep || day[5] != ScheduleSleep || day[6] != ScheduleAnything || day[18] != ScheduleJoy || day[19] != ScheduleJoy || day[20] != ScheduleAnything {
		t.Fatal(day)
	}
	quick := scheduleTemplate(TraitEffects{QuickSleeper: true})
	if len(hours(quick, ScheduleSleep)) != 6 || quick[22] != ScheduleAnything || quick[0] != ScheduleSleep {
		t.Fatal(quick)
	}
	owl := scheduleTemplate(TraitEffects{NightShift: true})
	if len(hours(owl, ScheduleWork)) != 8 || owl[23] != ScheduleWork || owl[6] != ScheduleWork || owl[7] != ScheduleAnything || len(hours(owl, ScheduleSleep)) != 8 || owl[10] != ScheduleSleep || owl[17] != ScheduleSleep || owl[21] != ScheduleJoy || owl[22] != ScheduleJoy {
		t.Fatal(owl)
	}
	quickOwl := scheduleTemplate(TraitEffects{NightShift: true, QuickSleeper: true})
	if len(hours(quickOwl, ScheduleSleep)) != 6 || quickOwl[10] != ScheduleAnything || quickOwl[11] != ScheduleSleep {
		t.Fatal(quickOwl)
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
	if len(d.Schedules) != 2 || d.Schedules[0].Pawn != "owl" || d.Schedules[0].Matches || !sameSchedule(d.Schedules[0].Slots, scheduleTemplate(TraitEffects{NightShift: true})) {
		t.Fatal(d)
	}
	if d.Schedules[1].Pawn != "plain" || !d.Schedules[1].Matches {
		t.Fatal(d)
	}
	if len(d.Player) != 1 || d.Player[0] != "edited" {
		t.Fatal(d.Player)
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
