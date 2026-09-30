package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

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
// other hour is Anything. The planner never writes Work: that would suppress
// rest and recreation and wake sleeping pawns (#1293).
func scheduleTemplate(effects TraitEffects) []string {
	slots := make([]string, 24)
	for h := range slots {
		slots[h] = ScheduleAnything
	}
	sleepFrom, sleepTo := sleepBlock(effects)
	fill(slots, sleepFrom, sleepTo, ScheduleSleep)
	slots[(sleepFrom+23)%24] = ScheduleJoy
	return slots
}

// sleepBlock is the template's Sleep block, first and last hour inclusive.
func sleepBlock(effects TraitEffects) (from, to int) {
	sleepFrom, sleepTo := 22, 5
	if effects.NightShift {
		sleepFrom, sleepTo = 10, 17
		if effects.QuickSleeper {
			sleepFrom, sleepTo = 11, 16
		}
	} else if effects.QuickSleeper {
		sleepFrom = 0
	}
	return sleepFrom, sleepTo
}

// Need bands for resizing the template (#1315). Hysteresis is pure
// thresholds read against the pawn's current timetable, in the style of
// EnterC/ExitC in cleanliness.go: no carried history, no minimum hold.
//   - Sleep: rest below RestEnter (Drowsy is < 0.28) on a base-length
//     timetable extends Sleep by SleepExtension hours at the wake end; an
//     already-extended timetable stays extended while rest is below RestExit.
//   - Recreation: joy below JoyEnter widens Joy to two hours (the two hours
//     before sleep); a widened timetable stays wide while joy is below JoyExit.
//
// An unknown need plans the base block for that need.
const (
	RestEnter      = 0.28
	RestExit       = 0.60
	JoyEnter       = 0.30
	JoyExit        = 0.70
	SleepExtension = 2
)

// needBand answers whether a resize holds: enter below enter, stay below exit.
func needBand(level domain.Fact[float64], active bool, enter, exit float64) bool {
	v, ok := level.Value()
	if !ok {
		return false
	}
	if active {
		return v < exit
	}
	return v < enter
}

// plannedSchedule is the template resized from the pawn's needs against its
// current timetable. It only ever adds Sleep or Joy hours over Anything.
func plannedSchedule(effects TraitEffects, rest, joy domain.Fact[float64], current []string) []string {
	slots := scheduleTemplate(effects)
	from, to := sleepBlock(effects)
	at := func(h int) string {
		if len(current) != 24 {
			return ""
		}
		return current[(h+24)%24]
	}
	extended := at(to+1) == ScheduleSleep
	if needBand(rest, extended, RestEnter, RestExit) {
		for i := 1; i <= SleepExtension; i++ {
			slots[(to+i)%24] = ScheduleSleep
		}
	}
	widened := at(from-2) == ScheduleJoy
	if needBand(joy, widened, JoyEnter, JoyExit) {
		slots[(from+22)%24] = ScheduleJoy
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

// PlanSchedules chooses a timetable per available pawn from its profile and
// needs (plannedSchedule);
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
		want := plannedSchedule(BuildProfile(pawn).Effects, pawn.Rest, pawn.Joy, current)
		decision.Schedules = append(decision.Schedules, PawnSchedule{Pawn: pawn.ID, Slots: want, Matches: sameSchedule(current, want)})
	}
	sort.Slice(decision.Schedules, func(i, j int) bool { return decision.Schedules[i].Pawn < decision.Schedules[j].Pawn })
	return decision
}
