package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Only a genuine admission refusal is re-marked when the clock stalls. The
// exits relabelled from it (the development ranking passing a goal
// over, a lost arbiter claim, a blocked paste site, no buildable bed) wait
// on something that changes with a review or another planner's step, not on
// the tick, so a stalled clock leaves them unmarked and they run again when
// their evidence marks them.
func TestStalledClockMarksOnlyAdmissionRefusals(t *testing.T) {
	t.Parallel()
	refused := store.BuildingMethodDecision{Refused: []policy.Refusal{{Reason: "already_reserved", Resource: "wood"}}}
	cases := []struct {
		name    string
		verdict Verdict
		marked  bool
	}{
		{"admission refusal", admissionRefused(refused), true},
		{"development ranking", awaitingPlan("prerequisite", "EnsureFoodSupply"), false},
		{"lost claim", claimHeld("colonist"), false},
		{"blocked paste site", siteBlocked("paste_dispenser_site", "preview_refused"), false},
		{"no buildable bed", BuildingHospitalUnavailable, false},
		{"food plan gap", awaitingFoodPlan("housing_expansion"), false},
		{"unusable field zone", siteBlocked("field_zone", "preview_refused"), false},
		{"no firefighter", noWorker("firefighter"), false},
		{"commit failure", refuse(RefusalSharedAdmission, "commit_failed", "sleeping"), true},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			t.Parallel()
			s, _ := schedulerFixture(t)
			s.queue.catalog = func() []plannerEntry { return []plannerEntry{quickPlanner("sleeping", classOptional)} }
			s.queue.configured = nil
			s.queue.ran(plannerSelectionResult{planners: true, pick: func(plannerEntry) bool { return true }}, []string{"sleeping"}, func(string) (Verdict, bool) {
				return c.verdict, true
			}, 100, nil)
			s.noWork = true
			got, err := s.selectPlanners(context.Background(), StepReason{Cause: StepTimer}, 100)
			if err != nil {
				t.Fatal(err)
			}
			if marked := got.planners && got.pick(plannerEntry{name: "sleeping"}); marked != c.marked {
				t.Fatalf("%s: marked=%v, want %v", c.verdict, marked, c.marked)
			}
		})
	}
}
