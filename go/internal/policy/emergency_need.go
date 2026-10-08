package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// EmergencyNeed classifies an unrecovered priority-0 or priority-1 assessment
// with an available response method. Dialogs and squad hunts do not qualify.
func EmergencyNeed(a RoundsAssessment) bool {
	return a.Priority < 2 && a.ID != AnswerDialog && a.Finding != domain.FindingMet && !a.MethodUnavailable && len(a.Hunt) == 0
}
