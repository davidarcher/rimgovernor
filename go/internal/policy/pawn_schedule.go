package policy

import "sort"

// TimeAssignmentDef names, as native's timetable reports them.
const (
	ScheduleAnything = "Anything"
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

// scheduleTemplate is the role-based timetable for a profile (#1314): the
// native day sleeps 22h-5h; a NightOwl sleeps 10h-17h (the hours the trait
// penalises being awake) and is free overnight; a QuickSleeper needs half
// the rest, so its Sleep block loses two hours at the start (and, for a
// NightOwl, one at each end). Joy is the hour right before sleep and every
// other hour is Anything. The planner never writes Work: it ignores rest and
// recreation and wakes sleeping pawns (#1293).
func scheduleTemplate(effects TraitEffects) []string {
	slots := make([]string, 24)
	for h := range slots {
		slots[h] = ScheduleAnything
	}
	sleepFrom, sleepTo := 22, 5
	if effects.NightShift {
		sleepFrom, sleepTo = 10, 17
		if effects.QuickSleeper {
			sleepFrom, sleepTo = 11, 16
		}
	} else if effects.QuickSleeper {
		sleepFrom = 0
	}
	fill(slots, sleepFrom, sleepTo, ScheduleSleep)
	slots[(sleepFrom+23)%24] = ScheduleJoy
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
