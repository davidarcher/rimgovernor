package domain

import "testing"

func TestMineAcquisitionActionAndClosedVariants(t *testing.T) {
	acquisition, err := NewAcquisition("Rock1", "Steel", Cell{X: 3, Z: 4})
	if err != nil {
		t.Fatal(err)
	}
	action, err := NewMineAcquisitionAction("mine-1", acquisition)
	if err != nil || action.ID() != "mine-1" || action.Kind() != MineAcquisitionAction {
		t.Fatal(action, err)
	}
	if got, ok := action.MineAcquisition(); !ok || got != acquisition {
		t.Fatal(got, ok)
	}
	// A MineAcquisitionAction must never satisfy the generic AcquisitionAction
	// accessor -- the whole point of the second vertical is that the two
	// remain distinct, independently admitted kinds (see docs/BACKLOG.md
	// 05.5).
	if _, ok := action.Acquisition(); ok {
		t.Fatal("mine acquisition exposed generic acquisition accessor")
	}
	if _, ok := action.GearReplace(); ok {
		t.Fatal("mine acquisition exposed gear replace")
	}

	sameShape, err := NewAcquisitionAction("acq-1", acquisition)
	if err != nil || sameShape.Kind() != AcquisitionAction {
		t.Fatal(sameShape, err)
	}
	if _, ok := sameShape.MineAcquisition(); ok {
		t.Fatal("generic acquisition exposed mine acquisition accessor")
	}

	if _, err := NewMineAcquisitionAction("mine-1", Acquisition{}); err == nil {
		t.Fatal("zero intent accepted")
	}
	for _, invalid := range []string{"", " ", "x\x00y"} {
		if _, err := NewMineAcquisitionAction(ActionID(invalid), acquisition); err == nil {
			t.Fatal("invalid action id accepted")
		}
	}
}

func TestMineAcquisitionPlanDoesNotRequireADraftPrerequisite(t *testing.T) {
	acquisition, _ := NewAcquisition("Rock1", "Steel", Cell{X: 3, Z: 4})
	action, _ := NewMineAcquisitionAction("mine-1", acquisition)
	plan, err := NewPlan("plan", 1, []Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := NewProgress(plan, "mine-1")
	if err != nil || progress.Action() != action || progress.View().Stage != Pending || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
}

func TestMineAcquisitionHandlerCoverageIsRequired(t *testing.T) {
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction}); err == nil {
		t.Fatal("missing mine acquisition handler accepted")
	}
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, MineAcquisitionAction, MineAcquisitionAction}); err == nil {
		t.Fatal("duplicate mine acquisition handler accepted")
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
}
