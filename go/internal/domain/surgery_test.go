package domain

import (
	"strings"
	"testing"
)

func TestSurgeryIntentAndClosedVariants(t *testing.T) {
	intent, err := NewSurgery("patient", "RemoveBodyPart", 3)
	if err != nil || intent.Patient() != "patient" || intent.Recipe() != "RemoveBodyPart" || intent.Part() != 3 {
		t.Fatal(intent, err)
	}
	action, err := NewSurgeryAction("surgery", intent)
	if err != nil || action.ID() != "surgery" || action.Kind() != SurgeryAction {
		t.Fatal(action, err)
	}
	if got, ok := action.Surgery(); !ok || got != intent {
		t.Fatal(got, ok)
	}
	if _, ok := action.Building(); ok {
		t.Fatal("surgery exposed building")
	}
	if _, ok := action.Tend(); ok {
		t.Fatal("surgery exposed tend")
	}
	if _, ok := action.BedAssign(); ok {
		t.Fatal("surgery exposed bed assign")
	}
	if _, err := NewSurgeryAction("surgery", Surgery{}); err == nil {
		t.Fatal("zero intent accepted")
	}
	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewSurgery(PawnID(invalid), "RemoveBodyPart", 3); err == nil {
			t.Fatal("invalid patient accepted")
		}
		if _, err := NewSurgery("patient", invalid, 3); err == nil {
			t.Fatal("invalid recipe accepted")
		}
		if _, err := NewSurgeryAction(ActionID(invalid), intent); err == nil {
			t.Fatal("invalid action accepted")
		}
	}
	if _, err := NewSurgery("patient", "RemoveBodyPart", -2); err == nil {
		t.Fatal("out-of-range part accepted")
	}
	if whole, err := NewSurgery("patient", "InstallPegLeg", -1); err != nil || whole.Part() != -1 {
		t.Fatal("whole-body sentinel rejected", whole, err)
	}
}

func TestSurgeryPlanDoesNotRequireADraftPrerequisite(t *testing.T) {
	intent, _ := NewSurgery("patient", "RemoveBodyPart", 3)
	action, _ := NewSurgeryAction("surgery", intent)
	plan, err := NewPlan("plan", 1, []Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := NewProgress(plan, "surgery")
	if err != nil || progress.Action() != action || progress.View().Stage != Pending || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
	if _, known := progress.View().DraftCleanup.Value(); known {
		t.Fatal("surgery fabricated draft ownership")
	}
}

func TestSurgeryHandlerCoverageIsRequired(t *testing.T) {
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction}); err == nil {
		t.Fatal("missing surgery handler accepted")
	}
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, SurgeryAction, SurgeryAction}); err == nil {
		t.Fatal("duplicate surgery handler accepted")
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
}

func TestValidMedicalCare(t *testing.T) {
	for _, care := range []MedicalCare{MedicalCareNoCare, MedicalCareNoMedicine, MedicalCareHerbalOrWorse, MedicalCareNormalOrWorse, MedicalCareBest} {
		if !ValidMedicalCare(care) {
			t.Fatalf("expected %q valid", care)
		}
	}
	for _, care := range []MedicalCare{"", "best ", "BEST", "unknown"} {
		if ValidMedicalCare(care) {
			t.Fatalf("expected %q invalid", care)
		}
	}
}
