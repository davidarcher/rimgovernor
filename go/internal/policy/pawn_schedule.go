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

// ScheduleDecision is the schedule planner's output: a row per pawn the
// planner owns (a native-default or planner-written timetable); pawns whose
// timetable the player edited are listed in Player and left alone.
type ScheduleDecision struct {
	Schedules []PawnSchedule
	Player    []PawnID
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

// plannerSchedules are every timetable the planner can have written; a
// current timetable outside this set and the native default is the
// player's.
func plannerSchedules() [][]string {
	return [][]string{
		nativeDefaultSchedule(),
		scheduleTemplate(TraitEffects{}),
		scheduleTemplate(TraitEffects{QuickSleeper: true}),
		scheduleTemplate(TraitEffects{NightShift: true}),
		scheduleTemplate(TraitEffects{NightShift: true, QuickSleeper: true}),
	}
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

// PlanSchedules chooses a timetable per available pawn from its profile.
// A pawn whose current timetable is neither the native default nor one the
// planner writes keeps the player's edit; an unknown timetable is skipped.
func PlanSchedules(pawns []WorkPawn) ScheduleDecision {
	var decision ScheduleDecision
	templates := plannerSchedules()
	for _, pawn := range pawns {
		available, ak := pawn.Available.Value()
		applies, pk := pawn.Applies.Value()
		current, ck := pawn.Schedule.Value()
		if !ak || !pk || !ck || !available || !applies {
			continue
		}
		owned := false
		for _, t := range templates {
			if sameSchedule(current, t) {
				owned = true
				break
			}
		}
		if !owned {
			decision.Player = append(decision.Player, pawn.ID)
			continue
		}
		want := scheduleTemplate(BuildProfile(pawn).Effects)
		decision.Schedules = append(decision.Schedules, PawnSchedule{Pawn: pawn.ID, Slots: want, Matches: sameSchedule(current, want)})
	}
	sort.Slice(decision.Schedules, func(i, j int) bool { return decision.Schedules[i].Pawn < decision.Schedules[j].Pawn })
	sort.Slice(decision.Player, func(i, j int) bool { return decision.Player[i] < decision.Player[j] })
	return decision
}
