package boundary

import n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"

// HourOfDay ports RimWorld's local-hour-of-day math -- RimWorld.GenDate's
// HourOfDay/HourInteger and Verse.GenMath's PositiveModRemap, verified
// against github.com/josh-m/RW-Decompile's RimWorld/GenDate.cs and
// Verse/GenMath.cs:
//
//	TicksPerHour = 2500; TimeZoneWidth = 15f
//	TimeZoneAt(longitude) = round(longitude / 15)              // MidpointRounding.AwayFromZero
//	LocalTicksOffsetFromLongitude(longitude) = TimeZoneAt(longitude) * 2500
//	HourOfDay(absTicks, longitude):
//	    x = absTicks + LocalTicksOffsetFromLongitude(longitude)
//	    return PositiveModRemap(x, 2500, 24)
//	PositiveModRemap(x, d, m):
//	    if x < 0: x -= (d - 1)
//	    return (x/d % m + m) % m
//
// It is a pure function of the absolute game tick and the map's world-tile
// longitude, independent of any running RimWorld session.
func HourOfDay(absTicks int64, longitude float64) int {
	offset := timeZoneAt(longitude) * 2500
	return positiveModRemap(absTicks+offset, 2500, 24)
}

// timeZoneAt ports GenDate.TimeZoneAt: round(longitude/15) with .NET's
// MidpointRounding.AwayFromZero (round-half-away-from-zero), matching
// Mathf.RoundToInt's behavior on the .5 boundary.
func timeZoneAt(longitude float64) int64 {
	f := longitude / 15.0
	if f >= 0 {
		return int64(f + 0.5)
	}
	return -int64(-f + 0.5)
}

// positiveModRemap ports GenMath.PositiveModRemap(long x, int d, int m).
func positiveModRemap(x, d, m int64) int {
	if x < 0 {
		x -= d - 1
	}
	q := x / d
	return int((q%m + m) % m)
}

// ExpectedScheduleDef selects the def name of the pawn's current timetable
// assignment for the given absolute tick and map longitude, mirroring the
// native schedule fencing check
// (integrations/rimgovernor-native/src/Bridge/Protocol/NativeMoodReliefOperations.cs:
// "pawn.timetable?.CurrentAssignment?.defName"), which RimWorld resolves from
// the pawn's TimetableSlot list keyed by the map-local hour of day. Ambiguous
// (duplicate-hour) or incomplete data returns unknown rather than guessing.
func ExpectedScheduleDef(slots []*n.TimetableSlot, absTicks int64, longitude float64) (string, bool) {
	hour := uint32(HourOfDay(absTicks, longitude))
	found := ""
	ok := false
	for _, slot := range slots {
		if slot == nil || slot.Hour == nil || slot.AssignmentDefName == nil {
			return "", false
		}
		if slot.GetHour() != hour {
			continue
		}
		if ok {
			return "", false
		}
		def := slot.GetAssignmentDefName()
		if def == "" {
			return "", false
		}
		found, ok = def, true
	}
	return found, ok
}

// ScheduleMatches reports whether the observed timetable slots are exactly
// the wanted hour-indexed assignment list: every hour present once with
// that def, nothing else.
func ScheduleMatches(slots []*n.TimetableSlot, wanted []string) bool {
	if len(wanted) != 24 || len(slots) != 24 {
		return false
	}
	seen := make([]bool, 24)
	for _, slot := range slots {
		if slot == nil || slot.Hour == nil || slot.AssignmentDefName == nil || slot.GetHour() >= 24 || seen[slot.GetHour()] || slot.GetAssignmentDefName() != wanted[slot.GetHour()] {
			return false
		}
		seen[slot.GetHour()] = true
	}
	return true
}
