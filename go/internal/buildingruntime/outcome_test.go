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
	}{
		{BuildingReasonAdmitted, "admitted"},
		{BuildingReasonNoDeficit, "no_active_deficit"},
		{BuildingReasonExistingWork, "existing_work"},
		{collapsePending("excavation_site"), "collapse_pending:excavation_site"},
		{noWorker("negotiator"), "no_worker:negotiator"},
		{awaitingFoodPlan("cooking-capacity"), "awaiting_plan:food_plan:cooking-capacity"},
		{researchWait("Electricity"), "awaiting_plan:research:Electricity"},
		{fieldUnavailable("food_plan"), "field_unavailable:food_plan"},
		{noSpace("defense_tier"), "no_space:defense_tier"},
		{BuildingReasonRefused, "shared_admission_refused"},
	} {
		if err := test.verdict.Validate(); err != nil {
			t.Fatalf("%v: %v", test.want, err)
		}
		if got := test.verdict.String(); got != test.want {
			t.Fatalf("rendered %q, want %q", got, test.want)
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
		{Outcome: OutcomeInProgress, Refusal: Refusal{Kind: RefusalNoSpace}},
		{Outcome: "bogus"},
		{Cause: "existing_work"},
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
			case ".git", "node_modules", ".rimgovernor", "dist":
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
