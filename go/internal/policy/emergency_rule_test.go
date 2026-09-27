package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// One EmergencyRule answers both the review's suspension and the
// development freeze, so the two agree for every exclusion (#1014).
func TestEmergencyRuleExclusionsAgreeWithDevelopmentFreeze(t *testing.T) {
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
		{"mood goal", RoutineAssessment{ID: MoodGoal("pawn"), Priority: 1, Need: domain.NeedDeficit}, false},
		{"recovered", RoutineAssessment{ID: ActiveCombat, Priority: 0, Need: domain.NeedRecovered}, false},
		{"method unavailable", RoutineAssessment{ID: ActiveCombat, Priority: 0, Need: domain.NeedDeficit, MethodUnavailable: true}, false},
	} {
		t.Run(c.name, func(t *testing.T) {
			if got := EmergencyRule(c.a); got != c.want {
				t.Fatalf("EmergencyRule = %v, want %v", got, c.want)
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
