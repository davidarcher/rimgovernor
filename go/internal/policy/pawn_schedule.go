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
	ScheduleMeditate = "Meditate"
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

// scheduleTemplate is the role-based timetable for a profile: the
// native day sleeps 22h-5h; a NightOwl sleeps 10h-17h (the hours the trait
// penalises being awake) and is free overnight; a QuickSleeper needs half
// the rest, so its Sleep block loses two hours at the start (and, for a
// NightOwl, one at each end). Joy is the hour right before sleep and every
// other hour is Anything. The planner never writes Work: that would suppress
// rest and recreation and wake sleeping pawns.
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

// Need bands for resizing the template. Hysteresis is pure
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
	// PsyfocusExit: a widened Meditate block enters when psyfocus is
	// below its target and stays wide until psyfocus reaches target+PsyfocusExit.
	PsyfocusExit = 0.10
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
// current timetable. It only ever adds Sleep, Joy or Meditate hours over Anything.
func plannedSchedule(effects TraitEffects, rest, joy domain.Fact[float64], current []string, joyOffset int, meditate *psyfocusBand) []string {
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
	// A staggered pawn takes its Joy hour, and the widened hour
	// before it, joyOffset hours earlier; Sleep never moves.
	slots[(from+23)%24] = ScheduleAnything
	// A psycaster meditates in the recreation block instead:
	// meditation also fills recreation, and low psyfocus widens it too.
	block := ScheduleJoy
	if meditate != nil {
		block = ScheduleMeditate
	}
	slots[(from+23-joyOffset)%24] = block
	prev := at(from - 2 - joyOffset)
	widened := prev == ScheduleJoy || prev == ScheduleMeditate
	wide := needBand(joy, widened, JoyEnter, JoyExit)
	if meditate != nil && needBand(meditate.focus, widened, meditate.target, meditate.target+PsyfocusExit) {
		wide = true
	}
	if wide {
		slots[(from+22-joyOffset)%24] = block
	}
	return slots
}

// maxJoyOffset bounds the recreation stagger to the evening: a
// pawn's Joy hours move at most this many hours earlier, which keeps them
// clear of the Sleep block (and its wake-end extension) even on the
// shortest (QuickSleeper NightOwl) day.
const maxJoyOffset = 3

// joyOffsets staggers recreation when the colony has fewer recreation
// places than people: scheduled pawns, sorted by ID, fill the
// places hour by hour back from sleep (wrapping past maxJoyOffset).
// Unknown counts, no places at all, or enough places: no stagger.
func joyOffsets(ids []PawnID, comfort domain.Fact[ComfortObservation]) map[PawnID]int {
	v, known := comfort.Value()
	places := len(v.Recreation)
	if !known || places == 0 || places >= len(v.People) {
		return nil
	}
	sort.Slice(ids, func(i, j int) bool { return ids[i] < ids[j] })
	offsets := map[PawnID]int{}
	for i, id := range ids {
		offsets[id] = (i / places) % (maxJoyOffset + 1)
	}
	return offsets
}

// psyfocusBand is a psycaster's psyfocus against its target.
type psyfocusBand struct {
	focus  domain.Fact[float64]
	target float64
}

// meditation is the pawn's psyfocus band when it should meditate: the
// Meditate def exists and the pawn has a known psylink.
func meditation(pawn WorkPawn, meditateAvailable bool) *psyfocusBand {
	level, lk := pawn.PsylinkLevel.Value()
	target, tk := pawn.PsyfocusTarget.Value()
	if !meditateAvailable || !lk || level < 1 || !tk {
		return nil
	}
	return &psyfocusBand{focus: pawn.Psyfocus, target: target}
}

func schedulable(pawn WorkPawn) ([]string, bool) {
	available, ak := pawn.Available.Value()
	applies, pk := pawn.Applies.Value()
	current, ck := pawn.Schedule.Value()
	return current, ak && pk && ck && available && applies
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
// needs (plannedSchedule), staggering Joy hours against the recreation
// census (joyOffsets); a pawn whose timetable is unknown is skipped.
// meditateAvailable is whether the Meditate TimeAssignmentDef exists
// (unknown reads as false): psycasters then meditate in their
// recreation block.
func PlanSchedules(pawns []WorkPawn, comfort domain.Fact[ComfortObservation], meditateAvailable bool) ScheduleDecision {
	return PlanSchedulesHeld(pawns, comfort, meditateAvailable, nil)
}

// PlanSchedulesHeld is PlanSchedules with the pawns in hold kept off Sleep
// (every Sleep hour planned Anything): a pending bestowing ceremony
// (CeremonyHold) needs its colonist and attendees awake to join the
// ritual. Rest below the sleep band still sends them to bed on their own.
func PlanSchedulesHeld(pawns []WorkPawn, comfort domain.Fact[ComfortObservation], meditateAvailable bool, hold map[PawnID]bool) ScheduleDecision {
	var decision ScheduleDecision
	var ids []PawnID
	for _, pawn := range pawns {
		if _, ok := schedulable(pawn); ok {
			ids = append(ids, pawn.ID)
		}
	}
	offsets := joyOffsets(ids, comfort)
	for _, pawn := range pawns {
		current, ok := schedulable(pawn)
		if !ok {
			continue
		}
		profile := BuildProfile(pawn)
		want := plannedSchedule(profile.Effects, pawn.Rest, pawn.Joy, current, offsets[pawn.ID], meditation(pawn, meditateAvailable))
		// A need an active gene removes needs no block for it.
		for h, slot := range want {
			if slot == ScheduleSleep && profile.Genes.NeedDisabled("Rest") || slot == ScheduleJoy && profile.Genes.NeedDisabled("Joy") {
				want[h] = ScheduleAnything
			}
		}
		if hold[pawn.ID] {
			for h, slot := range want {
				if slot == ScheduleSleep {
					want[h] = ScheduleAnything
				}
			}
		}
		decision.Schedules = append(decision.Schedules, PawnSchedule{Pawn: pawn.ID, Slots: want, Matches: sameSchedule(current, want)})
	}
	sort.Slice(decision.Schedules, func(i, j int) bool { return decision.Schedules[i].Pawn < decision.Schedules[j].Pawn })
	return decision
}
