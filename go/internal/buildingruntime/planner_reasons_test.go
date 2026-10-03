package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// filingOf serves wavePlannerReasons from a name -> (goal, verdict) table the
// way a finished wave does: the goal comes with the verdict.
func filingOf(goals map[string]policy.GoalID, verdicts map[string]Verdict) func(string) (policy.GoalID, Verdict, bool) {
	return func(name string) (policy.GoalID, Verdict, bool) {
		verdict, ok := verdicts[name]
		goal := goals[name]
		return goal, verdict, ok && goal != ""
	}
}

func TestWavePlannerReasonsFilesRefusalsPerGoal(t *testing.T) {
	goals := map[string]policy.GoalID{"power": policy.EnsureBasicPower, "equip": policy.MaintainEquipment, "gear": policy.MaintainEquipment, "research": policy.EnsureResearch, "clean": policy.MaintainCleanFacilities}
	verdicts := map[string]Verdict{
		"power":    BuildingReasonNoSpace,
		"equip":    BuildingReasonAdmitted,
		"gear":     BuildingReasonExhausted,
		"research": BuildingReasonNoReview,
		"clean":    BuildingReasonExistingWork,
		"naming":   BuildingReasonRefused,
	}
	got := wavePlannerReasons([]string{"power", "equip", "gear", "research", "clean", "naming"}, filingOf(goals, verdicts))
	want := map[policy.GoalID]string{policy.EnsureBasicPower: "no_space:verified_space", policy.MaintainEquipment: "shared_admission_refused:retry_bound:exhausted", policy.EnsureResearch: "", policy.MaintainCleanFacilities: ""}
	if len(got) != len(want) {
		t.Fatalf("%v", got)
	}
	for g, r := range want {
		if got[g] != r {
			t.Fatalf("%v", got)
		}
	}
	var log plannerReasonLog
	if len(log.changed(got)) != 4 || len(log.changed(got)) != 0 {
		t.Fatal("log not rate-limited on change")
	}
}

func TestWavePlannerReasonsFilesSleepingRefusalOnHousing(t *testing.T) {
	goals := map[string]policy.GoalID{"expansion": policy.MaintainHousing, "sleepingUpkeep": policy.MaintainHousing}
	verdicts := map[string]Verdict{"expansion": BuildingReasonNoDeficit, "sleepingUpkeep": BuildingSleepingUnavailable}
	got := wavePlannerReasons([]string{"expansion", "sleepingUpkeep"}, filingOf(goals, verdicts))
	if got[policy.MaintainHousing] != "no_space:sleeping_bed" {
		t.Fatalf("%v", got)
	}
}

func TestPlannerRecordReasonRejectsAnInvalidVerdict(t *testing.T) {
	bad := Verdict{Outcome: OutcomeRefused}
	if text, ok := plannerRecordReason(bad); ok || text != "" {
		t.Fatalf("a refusal with no kind was filed: %q %v", text, ok)
	}
	if _, ok := plannerRecordReason(Verdict{}); ok {
		t.Fatal("no verdict was filed")
	}
}
