package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestWavePlannerReasonsFilesRefusalsPerGoal(t *testing.T) {
	reasons := map[string]RoutineBuildingReason{
		"power":    BuildingMethodNoSpace,
		"equip":    BuildingMethodAdmitted,
		"gear":     BuildingMethodExhausted,
		"research": BuildingMethodNoReview,
		"clean":    BuildingMethodExistingWork,
		"naming":   BuildingMethodRefused,
	}
	got := wavePlannerReasons([]string{"power", "equip", "gear", "research", "clean", "naming"}, func(n string) (RoutineBuildingReason, bool) { r, ok := reasons[n]; return r, ok })
	want := map[policy.GoalID]string{policy.EnsureBasicPower: "insufficient_verified_space", policy.MaintainEquipment: "retry_bound_exhausted", policy.MaintainCleanFacilities: ""}
	if len(got) != len(want) {
		t.Fatalf("%v", got)
	}
	for g, r := range want {
		if got[g] != r {
			t.Fatalf("%v", got)
		}
	}
	var log plannerReasonLog
	if len(log.changed(got)) != 3 || len(log.changed(got)) != 0 {
		t.Fatal("log not rate-limited on change")
	}
}

func TestWavePlannerReasonsFilesSleepingRefusalOnHousing(t *testing.T) {
	reasons := map[string]RoutineBuildingReason{"expansion": BuildingMethodNoDeficit, "sleepingUpkeep": BuildingSleepingUnavailable}
	got := wavePlannerReasons([]string{"expansion", "sleepingUpkeep"}, func(n string) (RoutineBuildingReason, bool) { r, ok := reasons[n]; return r, ok })
	if got[policy.MaintainHousing] != "sleeping_bed_unavailable" {
		t.Fatalf("%v", got)
	}
}
