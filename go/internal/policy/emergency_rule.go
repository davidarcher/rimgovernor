package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// EmergencyRule reports whether a routine assessment suspends every other
// goal: the review's emergency suspension and the development freeze both
// ask it, so they cannot drift (#1014). Only an unrecovered need below
// priority 2 qualifies, and not when it:
//   - is a colony-naming or choice dialog (answered by one native write),
//   - is a mental break's mood goal (it ends only as ticks pass, so
//     suspending everything would leave the clock no work),
//   - has no declared method to clear it (#435).
func EmergencyRule(a RoutineAssessment) bool {
	return a.Priority < 2 && a.ID != ConfirmColonyNames && a.ID != AnswerDialog && !IsMoodGoal(a.ID) && a.Need != domain.NeedRecovered && !a.MethodUnavailable
}
