package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// EmergencyNeed reports whether a routine assessment is an emergency the
// EmergencySafeguard vetoes other work for: the rule and the development freeze both
// ask it, so they cannot drift (#1014). Only an unrecovered need below
// priority 2 qualifies, and not when it:
//   - is a choice dialog (answered by one native write),
//   - has no declared method to clear it (#435), or is optional relief
//     (EnsureMood: a mental break ends only as ticks pass), or
//   - is the squad-hunt origin of ActiveCombat (#2175): no hostile stands, so
//     the hunt keeps its combat window and drafts but suspends no other work.
func EmergencyNeed(a RoundsAssessment) bool {
	return a.Priority < 2 && a.ID != AnswerDialog && a.Finding != domain.FindingMet && !a.MethodUnavailable && len(a.Hunt) == 0
}
