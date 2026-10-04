package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// One EmergencyNeed answers both the EmergencySafeguard veto and the
// development freeze, so the two agree for every exclusion (#1014).
func TestEmergencyNeedExclusionsAgreeWithDevelopmentFreeze(t *testing.T) {
	for _, c := range []struct {
		name string
		a    RoundsAssessment
		want bool
	}{
		{"priority 0 deficit", RoundsAssessment{ID: ActiveCombat, Priority: 0, Finding: domain.FindingUnmet}, true},
		{"priority 1 unknown", RoundsAssessment{ID: ActiveCombat, Priority: 1, Finding: domain.FindingUnclear}, true},
		{"priority 2", RoundsAssessment{ID: ActiveCombat, Priority: 2, Finding: domain.FindingUnmet}, false},
		{"colony naming", RoundsAssessment{ID: ConfirmColonyNames, Priority: 0, Finding: domain.FindingUnmet}, false},
		{"choice dialog", RoundsAssessment{ID: AnswerDialog, Priority: 0, Finding: domain.FindingUnmet}, false},
		{"mood relief", RoundsAssessment{ID: EnsureMood, Subject: "pawn", Priority: 1, Finding: domain.FindingUnmet, MethodUnavailable: true}, false},
		{"recovered", RoundsAssessment{ID: ActiveCombat, Priority: 0, Finding: domain.FindingMet}, false},
		{"method unavailable", RoundsAssessment{ID: ActiveCombat, Priority: 0, Finding: domain.FindingUnmet, MethodUnavailable: true}, false},
	} {
		t.Run(c.name, func(t *testing.T) {
			if got := EmergencyNeed(c.a); got != c.want {
				t.Fatalf("EmergencyNeed = %v, want %v", got, c.want)
			}
			r := developmentFixture()
			r.Assessments = []RoundsAssessment{c.a}
			frozen := false
			for _, row := range rank(t, r).Rows {
				frozen = frozen || row.Reason == DevelopmentEmergency
			}
			if frozen != c.want {
				t.Fatalf("development frozen = %v, want %v", frozen, c.want)
			}
		})
	}
}
