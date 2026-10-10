package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Every refusal and wait cause is a member of the closed policy.Cause enum.
func TestEveryRefusalAndWaitKindIsACause(t *testing.T) {
	if len(refusalKinds)+len(waitKinds) != 23 {
		t.Fatalf("%d kinds, want 23", len(refusalKinds)+len(waitKinds))
	}
	for _, k := range append(append([]policy.Cause{}, refusalKinds...), waitKinds...) {
		if err := k.Validate(); err != nil {
			t.Errorf("kind %q: %v", k, err)
		}
	}
}

// Every constructor and shared verdict yields a refusal or wait whose cause is
// valid and has a plain-language sentence.
func TestEveryConstructorYieldsAValidCause(t *testing.T) {
	var verdicts []Verdict
	for _, k := range refusalKinds {
		verdicts = append(verdicts, refuse(k, "subject", ""))
	}
	for _, k := range waitKinds {
		verdicts = append(verdicts, waitFor(k, "subject"))
	}
	verdicts = append(verdicts,
		fieldUnavailable("field"), noSpace("cells"), rockNotDug("dig", "2"), noWorker("squad"),
		siteBlocked("site", "why"), collapsePending("roof"), awaitingPlan("plan", ""),
		claimHeld("bench"), retryBudgetWait("step", "why"), awaitingMethod("method"),
		researchWait("project"), awaitingFoodPlan("capacity"),
		BuildingReasonExistingWork, BuildingBunksOpen, BuildingReasonSeparation, BuildingReasonNotInteractive,
		BuildingReasonWaiting, BuildingReasonHeld, BuildingComfortWait, BuildingTemperatureWait,
		BuildingExistingFacility, BuildingHospitalConvert, BuildingSleepingUseNeeded, BuildingReasonNoSquad,
		BuildingShellBlocked, BuildingReasonDemand, stoneShellUnstocked, defensePerimeterNoStone)
	for _, v := range verdicts {
		if err := v.Refusal.Kind.Validate(); err != nil {
			t.Errorf("%s: %v", v, err)
		}
		if got := policy.Wording(v.Refusal.Kind, v.Refusal.Subject); got == "" {
			t.Errorf("%s has no wording", v)
		}
	}
}
