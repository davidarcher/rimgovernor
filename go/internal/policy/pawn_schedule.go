package policy

import "sort"

// TimeAssignmentDef names, as native's timetable reports them.
const (
	ScheduleAnything = "Anything"
	ScheduleWork     = "Work"
	ScheduleJoy      = "Joy"
	ScheduleSleep    = "Sleep"
)

// PawnSchedule is one pawn's planned timetable, hour 0 first.
type PawnSchedule struct {
	Pawn  PawnID
	Slots []string
	// Matches is whether the current timetable already is this one.
	Matches bool
}

// ScheduleDecision is the schedule planner's output: a row per available
// pawn with a known timetable. Whoever wrote the current timetable, Auto
// plans it fresh (control-loop.md, Manual control): a timetable edited
// under Manual is evidence of an old order, not authority over planning.
type ScheduleDecision struct {
	Schedules []PawnSchedule
}

// nativeDefaultSchedule is Pawn_TimetableTracker's constructor: Sleep 22h-5h,
// Anything otherwise.
func nativeDefaultSchedule() []string {
	slots := make([]string, 24)
	for h := range slots {
		slots[h] = ScheduleAnything
		if h > 21 || h <= 5 {
			slots[h] = ScheduleSleep
		}
	}
	return slots
}

func fill(slots []string, from, to int, def string) {
	for h := from; ; h = (h + 1) % 24 {
		slots[h] = def
		if h == to {
			return
		}
	}
}

// scheduleTemplate is the role-based timetable for a profile: the native
// day (Sleep 22h-5h) with a two-hour Joy block at 18h-19h before the evening
// recreation trough; a NightOwl works 23h-6h, plays 21h-22h and sleeps
// 10h-17h (the hours the trait penalises being awake); a QuickSleeper needs
// half the rest, so its Sleep block shrinks to six hours.
func scheduleTemplate(effects TraitEffects) []string {
	slots := nativeDefaultSchedule()
	if effects.NightShift {
		for h := range slots {
			slots[h] = ScheduleAnything
		}
		fill(slots, 23, 6, ScheduleWork)
		fill(slots, 21, 22, ScheduleJoy)
		fill(slots, 10, 17, ScheduleSleep)
		if effects.QuickSleeper {
			fill(slots, 10, 10, ScheduleAnything)
			fill(slots, 17, 17, ScheduleAnything)
		}
		return slots
	}
	fill(slots, 18, 19, ScheduleJoy)
	if effects.QuickSleeper {
		fill(slots, 22, 23, ScheduleAnything)
	}
	return slots
}

func sameSchedule(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

// PlanSchedules chooses a timetable per available pawn from its profile;
// a pawn whose timetable is unknown is skipped.
func PlanSchedules(pawns []WorkPawn) ScheduleDecision {
	var decision ScheduleDecision
	for _, pawn := range pawns {
		available, ak := pawn.Available.Value()
		applies, pk := pawn.Applies.Value()
		current, ck := pawn.Schedule.Value()
		if !ak || !pk || !ck || !available || !applies {
			continue
		}
		want := scheduleTemplate(BuildProfile(pawn).Effects)
		decision.Schedules = append(decision.Schedules, PawnSchedule{Pawn: pawn.ID, Slots: want, Matches: sameSchedule(current, want)})
	}
	sort.Slice(decision.Schedules, func(i, j int) bool { return decision.Schedules[i].Pawn < decision.Schedules[j].Pawn })
	return decision
}
