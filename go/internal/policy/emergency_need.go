package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// EmergencyNeed reports whether a routine assessment is an emergency the
// EmergencyRule vetoes other work for: the rule and the development freeze both
// ask it, so they cannot drift (#1014). Only an unrecovered need below
// priority 2 qualifies, and not when it:
//   - is a colony-naming or choice dialog (answered by one native write),
//   - is a mental break's mood goal (it ends only as ticks pass, so
//     vetoing everything would leave the clock no work),
//   - has no declared method to clear it (#435).
func EmergencyNeed(a RoutineAssessment) bool {
	return a.Priority < 2 && a.ID != ConfirmColonyNames && a.ID != AnswerDialog && !IsMoodGoal(a.ID) && a.Need != domain.NeedRecovered && !a.MethodUnavailable
}
