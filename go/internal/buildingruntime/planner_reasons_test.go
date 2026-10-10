package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// filingOf serves wavePlannerReasons from a name -> (goal, verdict) table the
// way a finished wave does: the goal comes with the verdict.
func filingOf(goals map[string]policy.ConcernID, verdicts map[string]Verdict) func(string) (policy.ConcernID, Verdict, bool) {
	return func(name string) (policy.ConcernID, Verdict, bool) {
		verdict, ok := verdicts[name]
		goal := goals[name]
		return goal, verdict, ok && goal != ""
	}
}

func TestWavePlannerReasonsFilesRefusalsPerGoal(t *testing.T) {
	goals := map[string]policy.ConcernID{"power": policy.EnsureBasicPower, "equip": policy.MaintainEquipment, "gear": policy.MaintainEquipment, "research": policy.EnsureResearch, "clean": policy.MaintainCleanFacilities}
	verdicts := map[string]Verdict{
		"power":    noSpace("power_route"),
		"equip":    BuildingReasonAdmitted,
		"gear":     refuse(policy.CauseRetriesSpent, "gear_craft", ""),
		"research": BuildingReasonNoReview,
		"clean":    BuildingReasonExistingWork,
		"naming":   refuse(policy.CauseSharedAdmission, "already_reserved", "wood"),
	}
	got := wavePlannerReasons([]string{"power", "equip", "gear", "research", "clean", "naming"}, filingOf(goals, verdicts))
	want := map[policy.ConcernID]policy.PlannerNote{
		policy.EnsureBasicPower:        {Cause: policy.CauseNoSpace, Subject: "power_route"},
		policy.MaintainEquipment:       {Cause: policy.CauseRetriesSpent, Subject: "gear_craft"},
		policy.MaintainCleanFacilities: {Cause: policy.CauseExistingWork},
	}
	if len(got) != len(want) {
		t.Fatalf("%v", got)
	}
	for g, r := range want {
		if got[g].Note != r {
			t.Fatalf("%v", got)
		}
	}
	var log plannerReasonLog
	if len(log.changed(got, 10)) != 3 || len(log.changed(got, 11)) != 0 {
		t.Fatal("log not rate-limited on change")
	}
}

func TestWavePlannerReasonsFilesSleepingRefusalOnHousing(t *testing.T) {
	goals := map[string]policy.ConcernID{"expansion": policy.MaintainHousing, "sleepingUpkeep": policy.MaintainHousing}
	verdicts := map[string]Verdict{"expansion": BuildingReasonNoDeficit, "sleepingUpkeep": BuildingSleepingUnavailable}
	got := wavePlannerReasons([]string{"expansion", "sleepingUpkeep"}, filingOf(goals, verdicts))
	if got[policy.MaintainHousing].Note != (policy.PlannerNote{Cause: policy.CauseAwaitingPlan, Subject: "buildable_bed"}) {
		t.Fatalf("%v", got)
	}
}

// A wait is filed as a wait with its sentence, loses to a sibling's refusal,
// beats a sibling's idle clear, and clears when the planner admits or finds no
// deficit.
func TestWavePlannerReasonsFilesWaitsAndClearsThem(t *testing.T) {
	goals := map[string]policy.ConcernID{"shelter": policy.MaintainShelter, "housing": policy.MaintainHousing, "sibling": policy.MaintainHousing, "comfort": policy.EnsureComfort, "off": policy.MaintainFireSafety, "on": policy.MaintainFireSafety}
	verdicts := map[string]Verdict{
		"shelter": BuildingBunksOpen,
		"housing": BuildingSleepingUseNeeded, "sibling": BuildingReasonNoDeficit,
		"comfort": BuildingComfortWait,
		"off":     BuildingReasonDisabled, "on": BuildingReasonNoDeficit,
	}
	names := []string{"shelter", "housing", "sibling", "comfort", "off", "on"}
	got := wavePlannerReasons(names, filingOf(goals, verdicts))
	want := map[policy.ConcernID]policy.PlannerNote{
		policy.MaintainShelter:    {Cause: policy.CauseBunksOpen},
		policy.MaintainHousing:    {Cause: policy.CauseSleepingUse},
		policy.EnsureComfort:      {Cause: policy.CauseComfortUse},
		policy.MaintainFireSafety: {},
	}
	if len(got) != len(want) {
		t.Fatalf("%v", got)
	}
	for g, r := range want {
		if got[g].Note != r {
			t.Fatalf("%s: %v, want %v", g, got[g].Note, r)
		}
	}
	verdicts["shelter"], verdicts["housing"] = BuildingReasonAdmitted, BuildingReasonNoDeficit
	cleared := wavePlannerReasons(names, filingOf(goals, verdicts))
	if cleared[policy.MaintainShelter].Note != (policy.PlannerNote{}) || cleared[policy.MaintainHousing].Note != (policy.PlannerNote{}) {
		t.Fatalf("waits not cleared: %v", cleared)
	}
	verdicts["shelter"] = BuildingReasonExistingWork
	if got := wavePlannerReasons(names, filingOf(goals, verdicts)); got[policy.MaintainShelter].Note != (policy.PlannerNote{Cause: policy.CauseExistingWork}) {
		t.Fatalf("existing work not filed as a wait: %v", got)
	}
	verdicts["shelter"] = BuildingReasonNoDeficit
	if got := wavePlannerReasons(names, filingOf(goals, verdicts)); got[policy.MaintainShelter].Note != (policy.PlannerNote{}) {
		t.Fatalf("nothing to do did not clear the existing-work wait: %v", got)
	}
	for combat, text := range map[Verdict]policy.Cause{BuildingReasonCombatOrders: policy.CauseCombatOrders, BuildingReasonHoldFallback: policy.CauseHoldFallback} {
		verdicts["shelter"] = combat
		if got := wavePlannerReasons(names, filingOf(goals, verdicts)); got[policy.MaintainShelter].Note != (policy.PlannerNote{Cause: text}) {
			t.Fatalf("%s not filed as a wait: %v", combat, got)
		}
		verdicts["shelter"] = BuildingReasonAdmitted
		if got := wavePlannerReasons(names, filingOf(goals, verdicts)); got[policy.MaintainShelter].Note != (policy.PlannerNote{}) {
			t.Fatalf("admitting did not clear %s: %v", combat, got)
		}
		// Sibling precedence on one goal: an idle sibling does not clear the
		// fight's note, and a refusal outranks it.
		verdicts["housing"], verdicts["sibling"] = combat, BuildingReasonNoDeficit
		if got := wavePlannerReasons(names, filingOf(goals, verdicts)); got[policy.MaintainHousing].Note != (policy.PlannerNote{Cause: text}) {
			t.Fatalf("an idle sibling cleared %s: %v", combat, got)
		}
		verdicts["sibling"] = noSpace("power_route")
		if got := wavePlannerReasons(names, filingOf(goals, verdicts)); got[policy.MaintainHousing].Note.Cause.Waiting() || got[policy.MaintainHousing].Note.Cause == "" {
			t.Fatalf("a refusal lost to %s: %v", combat, got)
		}
	}
	verdicts["shelter"] = BuildingReasonNoDeficit
	verdicts["housing"], verdicts["sibling"] = BuildingSleepingUseNeeded, noSpace("power_route")
	if refused := wavePlannerReasons(names, filingOf(goals, verdicts)); refused[policy.MaintainHousing].Note.Cause.Waiting() || refused[policy.MaintainHousing].Note.Cause == "" {
		t.Fatalf("a refusal lost to a wait: %v", refused)
	}
}

// A switched-off planner files the opt-out hold; a planner with no review or
// a stale proposal says nothing about its goal and files nothing.
func TestPlannerRecordReasonOptOutAndSilentOutcomes(t *testing.T) {
	if note, ok := plannerRecordReason(BuildingReasonDisabled); !ok || note.Cause != policy.CauseHeldOptIn {
		t.Fatalf("disabled filed %+v %v", note, ok)
	}
	for _, v := range []Verdict{BuildingReasonNoReview, BuildingReasonExpired} {
		if _, ok := plannerRecordReason(v); ok {
			t.Fatalf("%s was filed", v)
		}
	}
}

func TestPlannerRecordReasonRejectsAnInvalidVerdict(t *testing.T) {
	bad := Verdict{Outcome: OutcomeRefused}
	if note, ok := plannerRecordReason(bad); ok || note != (policy.PlannerNote{}) {
		t.Fatalf("a refusal with no kind was filed: %+v %v", note, ok)
	}
	if _, ok := plannerRecordReason(Verdict{Outcome: OutcomeWaiting}); ok {
		t.Fatal("a wait with no kind was filed")
	}
	if _, ok := plannerRecordReason(Verdict{}); ok {
		t.Fatal("no verdict was filed")
	}
}
