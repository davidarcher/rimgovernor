package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

func workTestIntent(t *testing.T, w domain.WorkAssignment, err error) *op.WorkSettingsIntent {
	t.Helper()
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewWorkAssignmentAction("work-0", w)
	if err != nil {
		t.Fatal(err)
	}
	built, err := workSettingsAction(action)
	if err != nil || built.GetWorkSettings().GetPawnId() != "pawn" {
		t.Fatal(built, err)
	}
	return built.GetWorkSettings()
}

func TestWorkSettingsIntent(t *testing.T) {
	w, err := domain.NewWorkAssignment("pawn", "before", true, []domain.WorkSetting{{Definition: "Cooking", Priority: 1}})
	if v := workTestIntent(t, w, err); len(v.Work) != 1 || v.Work[0].GetWorkTypeDef() != "Cooking" || v.Work[0].GetPriority() != 1 || v.MedicalCare != nil || v.DrugPolicy != nil {
		t.Fatal(v)
	}
	w, err = domain.NewMedicalCareAssignment("pawn", "before", "HerbalOrWorse")
	if v := workTestIntent(t, w, err); v.GetMedicalCare() != op.MedicalCare_MEDICAL_CARE_HERBAL_OR_WORSE || len(v.Work) != 0 {
		t.Fatal(v)
	}
	w, err = domain.NewDrugPolicyAssignment("pawn", "before", "social")
	if v := workTestIntent(t, w, err); v.GetDrugPolicy() != "social" || v.MedicalCare != nil || len(v.Work) != 0 {
		t.Fatal(v)
	}
	w, err = domain.NewFoodAssignment("pawn", "before", []string{"MealSimple"})
	if v := workTestIntent(t, w, err); len(v.GetFoodAllow().GetDefs()) != 1 || v.GetFoodAllow().Defs[0] != "MealSimple" {
		t.Fatal(v)
	}
	w, err = domain.NewAreaAssignment("pawn", "before", true, "")
	if v := workTestIntent(t, w, err); v.GetAllowedArea().GetClear() == nil {
		t.Fatal(v)
	}
	w, err = domain.NewAreaAssignment("pawn", "before", false, "Area_7")
	if v := workTestIntent(t, w, err); v.GetAllowedArea().GetEntityId() != "Area_7" {
		t.Fatal(v)
	}
	if _, err := workSettingsAction(domain.Action{}); err == nil {
		t.Fatal("built a work intent from a foreign action")
	}
}
