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
		a    RoutineAssessment
		want bool
	}{
		{"priority 0 deficit", RoutineAssessment{ID: ActiveCombat, Priority: 0, Need: domain.NeedDeficit}, true},
		{"priority 1 unknown", RoutineAssessment{ID: ActiveCombat, Priority: 1, Need: domain.NeedUnknown}, true},
		{"priority 2", RoutineAssessment{ID: ActiveCombat, Priority: 2, Need: domain.NeedDeficit}, false},
		{"colony naming", RoutineAssessment{ID: ConfirmColonyNames, Priority: 0, Need: domain.NeedDeficit}, false},
		{"choice dialog", RoutineAssessment{ID: AnswerDialog, Priority: 0, Need: domain.NeedDeficit}, false},
		{"mood relief", RoutineAssessment{ID: EnsureMood, Subject: "pawn", Priority: 1, Need: domain.NeedDeficit, MethodUnavailable: true}, false},
		{"recovered", RoutineAssessment{ID: ActiveCombat, Priority: 0, Need: domain.NeedRecovered}, false},
		{"method unavailable", RoutineAssessment{ID: ActiveCombat, Priority: 0, Need: domain.NeedDeficit, MethodUnavailable: true}, false},
	} {
		t.Run(c.name, func(t *testing.T) {
			if got := EmergencyNeed(c.a); got != c.want {
				t.Fatalf("EmergencyNeed = %v, want %v", got, c.want)
			}
			r := developmentFixture()
			r.Assessments = []RoutineAssessment{c.a}
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
