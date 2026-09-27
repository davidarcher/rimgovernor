package boundary

import n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"

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
