package buildingruntime

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestVerdictRendersPerKind(t *testing.T) {
	for _, test := range []struct {
		verdict Verdict
		want    string
		text    string
	}{
		{BuildingReasonAdmitted, "admitted", "plan admitted"},
		{BuildingReasonNoDeficit, "nothing_to_do", "nothing to do right now"},
		{BuildingReasonDisabled, "disabled", "this planner is switched off"},
		{BuildingReasonNoReview, "no_current_review", "no current review to judge"},
		{BuildingReasonExpired, "expired", "the proposal went stale"},
		{BuildingReasonExistingWork, "already_working_on_it", "already working on it"},
		{BuildingReasonCombatOrders, "combat_orders", "combat orders are running"},
		{BuildingReasonHoldFallback, "hold_fallback", "the hold line fell back to squad defense"},
		{collapsePending("excavation_site"), "collapse_pending:excavation_site", "a collapse is pending at the excavation site"},
		{noWorker("squad"), "no_worker:squad", "no colonist free to do it (squad)"},
		{noWorker(""), "no_worker", "no colonist free to do it"},
		{awaitingFoodPlan("cooking-capacity"), "awaiting_plan:food_plan:cooking-capacity", "waiting on food plan (cooking capacity)"},
		{researchWait("Electricity"), "awaiting_plan:research:Electricity", "waiting on research (Electricity)"},
		{awaitingPlan("earlier_shell", ""), "awaiting_plan:earlier_shell", "waiting on earlier shell"},
		{fieldUnavailable("builder_available"), "field_unavailable:builder_available", "the game did not report builder available"},
		{noSpace("verified_space"), "no_space:verified_space", "no space found for it (verified space)"},
		{BuildingReasonRefused, "shared_admission_refused", "the shared admission check turned the plan down"},
		{BuildingReasonExhausted, "retry_budget_spent", "tried as often as it may"},
		{awaitingSlot("clear_ancient_shrine"), "awaiting_plan:development_slot:clear_ancient_shrine", "waiting on development slot (clear ancient shrine)"},
		{noSpace("walkable_layout"), "no_space:walkable_layout", "no space found for it (walkable layout)"},
		{BuildingReasonUsed, "method_already_used", "waiting for work it already started"},
		{BuildingBunksOpen, "shelter_bunks_open", "waiting on the shelter's open bunks"},
		{BuildingReasonHeld, "breach_held", "holding off on the ancient shrine breach"},
		{BuildingComfortWait, "waiting_for_native_comfort_use", "waiting for colonists to use the comfort already provided"},
		{BuildingExistingFacility, "existing_facility_needs_bill_or_upkeep", "an existing facility needs a bill or upkeep first"},
		{BuildingHospitalConvert, "hospital_bed_convert_pending", "waiting for a bed to become a hospital bed"},
		{BuildingSleepingUseNeeded, "sleeping_use_needed", "waiting for colonists to use the beds provided"},
		{BuildingReasonSeparation, "butcher_separation_pending", "waiting for the butcher spot to be built apart"},
		{BuildingReasonNotInteractive, "dialog_not_interactive", "waiting for the choice dialog to accept an answer"},
		{BuildingReasonWaiting, "waiting_on_claim", "waiting on a claim held by a higher-ranked proposal"},
	} {
		if err := test.verdict.Validate(); err != nil {
			t.Fatalf("%v: %v", test.want, err)
		}
		if got := test.verdict.String(); got != test.want || strings.ContainsAny(got, " \t") {
			t.Fatalf("rendered %q, want %q", got, test.want)
		}
		if got := test.verdict.Text(); got != test.text {
			t.Fatalf("%s reads %q, want %q", test.want, got, test.text)
		}
	}
}

func TestEveryKindHasASentence(t *testing.T) {
	for _, kind := range refusalKinds {
		if v := refuse(kind, "", ""); v.Text() == "" {
			t.Fatalf("refusal %s has no sentence", kind)
		}
	}
	for _, kind := range waitKinds {
		if v := waitOn(kind); v.Text() == "" || v.Outcome != OutcomeWaiting {
			t.Fatalf("wait %s has no sentence", kind)
		}
	}
}

func TestEverySharedVerdictIsValid(t *testing.T) {
	for _, v := range []Verdict{BuildingReasonAdmitted, BuildingReasonDisabled, BuildingReasonNoReview, BuildingReasonNoDeficit, BuildingReasonExpired, BuildingReasonExistingWork, BuildingReasonUsed, BuildingBunksOpen, BuildingReasonHoldFallback, BuildingReasonCombatOrders, BuildingReasonSeparation, BuildingReasonNotInteractive, BuildingReasonWaiting, BuildingReasonHeld, BuildingComfortWait, BuildingExistingFacility, BuildingHospitalConvert, BuildingSleepingUseNeeded, BuildingReasonNoSpace, BuildingReasonRefused, BuildingReasonExhausted, BuildingReasonNoSquad, BuildingShellBlocked, BuildingShelterPending, BuildingExcavationBlocked, BuildingSuiteStock, BuildingWorkshopUnavailable, BuildingWorkshopResearch, BuildingResearchBench, BuildingResearchBenchUnavailable, BuildingHospitalUnavailable, BuildingSleepingUnavailable, BuildingNoWeaponBench, BuildingReasonDemand, stoneShellUnstocked, defensePerimeterNoStone} {
		if err := v.Validate(); err != nil || v.IsZero() {
			t.Fatalf("%+v: %v", v, err)
		}
	}
}

func TestRefusalWithoutAKindIsRejected(t *testing.T) {
	for _, v := range []Verdict{
		{Outcome: OutcomeRefused},
		{Outcome: OutcomeRefused, Refusal: Refusal{Kind: "unknown_prerequisite"}},
		{Outcome: OutcomeAdmitted, Refusal: Refusal{Kind: RefusalNoSpace}},
		{Outcome: "bogus"},
		{Outcome: OutcomeWaiting},
		{Outcome: OutcomeWaiting, Refusal: Refusal{Kind: RefusalNoSpace}},
		{Outcome: OutcomeRefused, Refusal: Refusal{Kind: WaitClaim}},
		{Outcome: OutcomeNothingToDo, Refusal: Refusal{Kind: WaitClaim}},
		{Refusal: Refusal{Kind: RefusalNoSpace}},
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
	if !fieldUnavailable("x").Is(RefusalFieldUnavailable) || BuildingReasonUsed.Is(RefusalFieldUnavailable) || noSpace("x").Is(RefusalFieldUnavailable) {
		t.Fatal("Is")
	}
	if !fieldUnavailable("x").skipsToPlacement() || !BuildingReasonNoSpace.skipsToPlacement() || !BuildingReasonUsed.skipsToPlacement() || BuildingReasonRefused.skipsToPlacement() {
		t.Fatal("skipsToPlacement")
	}
}

func TestNoUnknownPrerequisiteRemains(t *testing.T) {
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
