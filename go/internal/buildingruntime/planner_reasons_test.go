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
	want := map[policy.GoalID]policy.PlannerNote{
		policy.EnsureBasicPower:        {Text: "no space found for it (verified space)"},
		policy.MaintainEquipment:       {Text: "the shared admission check turned the plan down (retry bound: exhausted)"},
		policy.MaintainCleanFacilities: {Text: "already working on it", Waiting: true},
	}
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
	goals := map[string]policy.GoalID{"expansion": policy.MaintainHousing, "sleepingUpkeep": policy.MaintainHousing}
	verdicts := map[string]Verdict{"expansion": BuildingReasonNoDeficit, "sleepingUpkeep": BuildingSleepingUnavailable}
	got := wavePlannerReasons([]string{"expansion", "sleepingUpkeep"}, filingOf(goals, verdicts))
	if got[policy.MaintainHousing].Text != "no space found for it (sleeping bed)" || got[policy.MaintainHousing].Waiting {
		t.Fatalf("%v", got)
	}
}

// A wait is filed as a wait with its sentence, loses to a sibling's refusal,
// beats a sibling's idle clear, and clears when the planner admits or finds no
// deficit.
func TestWavePlannerReasonsFilesWaitsAndClearsThem(t *testing.T) {
	goals := map[string]policy.GoalID{"shelter": policy.MaintainShelter, "housing": policy.MaintainHousing, "sibling": policy.MaintainHousing, "comfort": policy.EnsureComfort, "off": policy.MaintainFireSafety, "on": policy.MaintainFireSafety}
	verdicts := map[string]Verdict{
		"shelter": BuildingBunksOpen,
		"housing": BuildingSleepingUseNeeded, "sibling": BuildingReasonNoDeficit,
		"comfort": BuildingComfortWait,
		"off":     BuildingReasonDisabled, "on": BuildingReasonNoDeficit,
	}
	names := []string{"shelter", "housing", "sibling", "comfort", "off", "on"}
	got := wavePlannerReasons(names, filingOf(goals, verdicts))
	want := map[policy.GoalID]policy.PlannerNote{
		policy.MaintainShelter:    {Text: "waiting on the shelter's open bunks", Waiting: true},
		policy.MaintainHousing:    {Text: "waiting for colonists to use the beds provided", Waiting: true},
		policy.EnsureComfort:      {Text: "waiting for colonists to use the comfort already provided", Waiting: true},
		policy.MaintainFireSafety: {},
	}
	if len(got) != len(want) {
		t.Fatalf("%v", got)
	}
	for g, r := range want {
		if got[g] != r {
			t.Fatalf("%s: %v, want %v", g, got[g], r)
		}
	}
	verdicts["shelter"], verdicts["housing"] = BuildingReasonAdmitted, BuildingReasonNoDeficit
	cleared := wavePlannerReasons(names, filingOf(goals, verdicts))
	if cleared[policy.MaintainShelter] != (policy.PlannerNote{}) || cleared[policy.MaintainHousing] != (policy.PlannerNote{}) {
		t.Fatalf("waits not cleared: %v", cleared)
	}
	verdicts["shelter"] = BuildingReasonExistingWork
	if got := wavePlannerReasons(names, filingOf(goals, verdicts)); got[policy.MaintainShelter] != (policy.PlannerNote{Text: "already working on it", Waiting: true}) {
		t.Fatalf("existing work not filed as a wait: %v", got)
	}
	verdicts["shelter"] = BuildingReasonNoDeficit
	if got := wavePlannerReasons(names, filingOf(goals, verdicts)); got[policy.MaintainShelter] != (policy.PlannerNote{}) {
		t.Fatalf("nothing to do did not clear the existing-work wait: %v", got)
	}
	for combat, text := range map[Verdict]string{BuildingReasonCombatOrders: "combat orders are running", BuildingReasonHoldFallback: "the hold line fell back to squad defense"} {
		verdicts["shelter"] = combat
		if got := wavePlannerReasons(names, filingOf(goals, verdicts)); got[policy.MaintainShelter] != (policy.PlannerNote{Text: text, Waiting: true}) {
			t.Fatalf("%s not filed as a wait: %v", combat, got)
		}
		verdicts["shelter"] = BuildingReasonAdmitted
		if got := wavePlannerReasons(names, filingOf(goals, verdicts)); got[policy.MaintainShelter] != (policy.PlannerNote{}) {
			t.Fatalf("admitting did not clear %s: %v", combat, got)
		}
		// Sibling precedence on one goal: an idle sibling does not clear the
		// fight's note, and a refusal outranks it.
		verdicts["housing"], verdicts["sibling"] = combat, BuildingReasonNoDeficit
		if got := wavePlannerReasons(names, filingOf(goals, verdicts)); got[policy.MaintainHousing] != (policy.PlannerNote{Text: text, Waiting: true}) {
			t.Fatalf("an idle sibling cleared %s: %v", combat, got)
		}
		verdicts["sibling"] = BuildingReasonNoSpace
		if got := wavePlannerReasons(names, filingOf(goals, verdicts)); got[policy.MaintainHousing].Waiting || got[policy.MaintainHousing].Text == "" {
			t.Fatalf("a refusal lost to %s: %v", combat, got)
		}
	}
	verdicts["shelter"] = BuildingReasonNoDeficit
	verdicts["housing"], verdicts["sibling"] = BuildingSleepingUseNeeded, BuildingReasonNoSpace
	if refused := wavePlannerReasons(names, filingOf(goals, verdicts)); refused[policy.MaintainHousing].Waiting || refused[policy.MaintainHousing].Text == "" {
		t.Fatalf("a refusal lost to a wait: %v", refused)
	}
}

// A switched-off planner files the opt-out hold; a planner with no review or
// a stale proposal says nothing about its goal and files nothing.
func TestPlannerRecordReasonOptOutAndSilentOutcomes(t *testing.T) {
	if note, ok := plannerRecordReason(BuildingReasonDisabled); !ok || note.Text != policy.PlannerOptOut || note.Waiting {
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
