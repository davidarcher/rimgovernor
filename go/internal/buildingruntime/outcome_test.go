package buildingruntime

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// TestAdmissionRefusedCarriesReason: a refused decision keeps the
// shared_admission_refused kind and names its first refusal.
func TestAdmissionRefusedCarriesReason(t *testing.T) {
	t.Parallel()
	got := admissionRefused(store.BuildingMethodDecision{Refused: []policy.Refusal{{Reason: policy.AlreadyReserved, Resource: "wood"}}})
	if !got.Is(policy.CauseSharedAdmission) || got.String() != "shared_admission_refused:already_reserved:wood" {
		t.Fatal(got.String())
	}
	if bare := admissionRefused(store.BuildingMethodDecision{}); bare.String() != "shared_admission_refused:candidates_left_unadmitted" {
		t.Fatal(bare)
	}
}

func TestVerdictRendersPerKind(t *testing.T) {
	for _, test := range []struct {
		verdict Verdict
		want    string
	}{
		{BuildingReasonAdmitted, "admitted"},
		{BuildingReasonNoDeficit, "nothing_to_do"},
		{BuildingReasonDisabled, "disabled"},
		{BuildingReasonNoReview, "no_current_review"},
		{BuildingReasonExpired, "expired"},
		{BuildingReasonExistingWork, "already_working_on_it"},
		{BuildingReasonCombatOrders, "combat_orders"},
		{BuildingReasonHoldFallback, "hold_fallback"},
		{collapsePending("excavation_site"), "collapse_pending:excavation_site"},
		{noWorker("squad"), "no_worker:squad"},
		{noWorker(""), "no_worker"},
		{awaitingFoodPlan("cooking-capacity"), "awaiting_plan:food_plan:cooking-capacity"},
		{researchWait("Electricity"), "awaiting_plan:research:Electricity"},
		{awaitingPlan("earlier_shell", ""), "awaiting_plan:earlier_shell"},
		{fieldUnavailable("Hopper_availability"), "field_unavailable:Hopper_availability"},
		{refuse(policy.CauseNoWorker, "builder_for_Cooler", "construction_skill_6"), "no_worker:builder_for_Cooler:construction_skill_6"},
		{excavationBlocked("way_in_closed"), "site_blocked:excavation_site:way_in_closed"},
		{refuse(policy.CauseRetriesSpent, "excavation_stage", ""), "retry_budget_spent:excavation_stage"},
		{retryBudgetWait("tend-alice", "no_path"), "retry_budget_waiting:tend-alice:no_path"},
		{BuildingTemperatureWait, "waiting_for_native_temperature"},
		{comfortAccessWait(policy.ComfortCapacity), "existing_facility_access_blocked:dining"},
		{noSpace("floor_cells"), "no_space:floor_cells"},
		{admissionRefused(store.BuildingMethodDecision{}), "shared_admission_refused:candidates_left_unadmitted"},
		{refuse(policy.CauseRetriesSpent, "gear_craft", ""), "retry_budget_spent:gear_craft"},
		{noSpace("walkable_layout"), "no_space:walkable_layout"},
		{waitFor(policy.CauseMethodUsed, "bill_method"), "method_already_used:bill_method"},
		{BuildingBunksOpen, "shelter_bunks_open"},
		{BuildingReasonHeld, "breach_held"},
		{BuildingComfortWait, "waiting_for_native_comfort_use"},
		{BuildingExistingFacility, "existing_facility_needs_bill_or_upkeep"},
		{BuildingHospitalConvert, "hospital_bed_convert_pending"},
		{BuildingSleepingUseNeeded, "sleeping_use_needed"},
		{BuildingReasonSeparation, "butcher_separation_pending"},
		{BuildingReasonNotInteractive, "dialog_not_interactive"},
		{BuildingReasonWaiting, "waiting_on_claim"},
		{awaitingPlan("prerequisite", "EnsureFoodSupply"), "awaiting_plan:prerequisite:EnsureFoodSupply"},
		{claimHeld("bench"), "waiting_on_claim:bench"},
		{awaitingPlan("feed_bench", "within_reach_of_animals"), "awaiting_plan:feed_bench:within_reach_of_animals"},
		{fieldUnavailable("acquisition_sources"), "field_unavailable:acquisition_sources"},
	} {
		if err := test.verdict.Validate(); err != nil {
			t.Fatalf("%v: %v", test.want, err)
		}
		if got := test.verdict.String(); got != test.want || strings.ContainsAny(got, " \t") {
			t.Fatalf("rendered %q, want %q", got, test.want)
		}
	}
}

func TestEveryKindHasASentence(t *testing.T) {
	for _, kind := range refusalKinds {
		if v := refuse(kind, "subject", ""); policy.Wording(v.Refusal.Kind, "subject") == "" {
			t.Fatalf("refusal %s has no sentence", kind)
		}
	}
	for _, kind := range waitKinds {
		if v := waitFor(kind, "subject"); policy.Wording(v.Refusal.Kind, "subject") == "" || v.Outcome != OutcomeWaiting {
			t.Fatalf("wait %s has no sentence", kind)
		}
	}
}

func TestEverySharedVerdictIsValid(t *testing.T) {
	for _, v := range []Verdict{BuildingReasonAdmitted, BuildingReasonDisabled, BuildingReasonNoReview, BuildingReasonNoDeficit, BuildingReasonExpired, BuildingReasonExistingWork, waitFor(policy.CauseMethodUsed, "bill_method"), BuildingBunksOpen, BuildingReasonHoldFallback, BuildingReasonCombatOrders, BuildingReasonSeparation, BuildingReasonNotInteractive, BuildingReasonWaiting, BuildingReasonHeld, BuildingComfortWait, BuildingExistingFacility, BuildingHospitalConvert, BuildingSleepingUseNeeded, noSpace("floor_cells"), admissionRefused(store.BuildingMethodDecision{}), refuse(policy.CauseRetriesSpent, "gear_craft", ""), BuildingReasonNoSquad, BuildingShellBlocked, BuildingShelterPending, excavationBlocked("roof_unsupported"), BuildingSuiteStock, BuildingWorkshopUnavailable, BuildingWorkshopResearch, BuildingResearchBench, BuildingResearchBenchUnavailable, BuildingHospitalUnavailable, BuildingSleepingUnavailable, BuildingNoWeaponBench, BuildingReasonDemand, stoneShellUnstocked, defensePerimeterNoStone} {
		if err := v.Validate(); err != nil || v.IsZero() {
			t.Fatalf("%+v: %v", v, err)
		}
	}
}

func TestRefusalWithoutAKindIsRejected(t *testing.T) {
	for _, v := range []Verdict{
		{Outcome: OutcomeRefused},
		{Outcome: OutcomeRefused, Refusal: Refusal{Kind: "unknown_prerequisite"}},
		{Outcome: OutcomeAdmitted, Refusal: Refusal{Kind: policy.CauseNoSpace}},
		{Outcome: "bogus"},
		{Outcome: OutcomeWaiting},
		{Outcome: OutcomeWaiting, Refusal: Refusal{Kind: policy.CauseNoSpace}},
		{Outcome: OutcomeRefused, Refusal: Refusal{Kind: policy.CauseClaim}},
		{Outcome: OutcomeNothingToDo, Refusal: Refusal{Kind: policy.CauseClaim}},
		{Refusal: Refusal{Kind: policy.CauseNoSpace}},
		{Outcome: OutcomeRefused, Refusal: Refusal{Kind: policy.CauseSharedAdmission}},
		{Outcome: OutcomeRefused, Refusal: Refusal{Kind: policy.CauseRetriesSpent}},
		{Outcome: OutcomeWaiting, Refusal: Refusal{Kind: policy.CauseMethodUsed}},
		{Outcome: OutcomeWaiting, Refusal: Refusal{Kind: policy.CauseMethodUsed, Detail: "x"}},
		{Outcome: OutcomeRefused, Refusal: Refusal{Kind: policy.CauseSharedAdmission, Detail: "wood"}},
	} {
		if v.Validate() == nil {
			t.Fatalf("accepted %+v", v)
		}
	}
	defer func() {
		if recover() == nil {
			t.Fatal("refuse accepted an empty kind")
		}
	}()
	refuse("", "subject", "")
}

func TestIsMatchesOnlyRefusalsOfTheKind(t *testing.T) {
	if !fieldUnavailable("x").Is(policy.CauseFieldUnavailable) || waitFor(policy.CauseMethodUsed, "x").Is(policy.CauseFieldUnavailable) || noSpace("x").Is(policy.CauseFieldUnavailable) {
		t.Fatal("Is")
	}
	if !fieldUnavailable("x").skipsToPlacement() || !noSpace("floor_cells").skipsToPlacement() || !waitFor(policy.CauseMethodUsed, "x").skipsToPlacement() || admissionRefused(store.BuildingMethodDecision{}).skipsToPlacement() {
		t.Fatal("skipsToPlacement")
	}
}

func TestNoUnknownPrerequisiteRemains(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	root := filepath.Join("..", "..", "..")
	var found []string
	err := filepath.Walk(root, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return err
		}
		if info.IsDir() {
			switch info.Name() {
			case ".git", ".claude", "node_modules", ".rimgovernor", "dist":
				return filepath.SkipDir
			}
			return nil
		}
		if !strings.HasSuffix(path, ".go") && !strings.HasSuffix(path, ".md") && !strings.HasSuffix(path, ".ts") || strings.HasSuffix(path, "outcome_test.go") {
			return nil
		}
		data, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		if strings.Contains(string(data), "unknown_prerequisite") || strings.Contains(string(data), "BuildingMethodUnknown") {
			found = append(found, path)
		}
		return nil
	})
	if err != nil || len(found) > 0 {
		t.Fatalf("unknown_prerequisite remains in %v (%v)", found, err)
	}
}
