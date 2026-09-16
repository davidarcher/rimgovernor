package domain

import "testing"

func TestExcavationActionAndClosedVariants(t *testing.T) {
	excavation, err := NewExcavation(Cell{X: 3, Z: 4}, "Granite")
	if err != nil {
		t.Fatal(err)
	}
	action, err := NewExcavationAction("dig-1", excavation)
	if err != nil || action.ID() != "dig-1" || action.Kind() != ExcavationAction {
		t.Fatal(action, err)
	}
	if got, ok := action.Excavation(); !ok || got != excavation || got.Cell() != (Cell{X: 3, Z: 4}) || got.Definition() != "Granite" {
		t.Fatal(got, ok)
	}
	// Excavation is keyed by cell plus rock definition, never by ThingID,
	// and must stay distinct from resource mining's MineAcquisitionAction.
	if _, ok := action.MineAcquisition(); ok {
		t.Fatal("excavation exposed mine acquisition accessor")
	}
	if _, ok := action.WallRemoval(); ok {
		t.Fatal("excavation exposed wall removal accessor")
	}
	if _, err := NewExcavationAction("dig-1", Excavation{}); err == nil {
		t.Fatal("zero intent accepted")
	}
	for _, invalid := range []string{"", " ", "x\x00y"} {
		if _, err := NewExcavation(Cell{X: 1, Z: 1}, invalid); err == nil {
			t.Fatal("invalid definition accepted")
		}
		if _, err := NewExcavationAction(ActionID(invalid), excavation); err == nil {
			t.Fatal("invalid action id accepted")
		}
	}
	if _, err := NewExcavation(Cell{X: -1, Z: 1}, "Granite"); err == nil {
		t.Fatal("negative cell accepted")
	}
}

func TestExcavationPlanCanonicalisesAndNeedsNoPrerequisite(t *testing.T) {
	excavation, _ := NewExcavation(Cell{X: 3, Z: 4}, "Granite")
	action, _ := NewExcavationAction("dig-1", excavation)
	plan, err := NewPlan("plan", 1, []Action{action})
	if err != nil || plan.Actions()[0] != action {
		t.Fatal(plan, err)
	}
	progress, err := NewProgress(plan, "dig-1")
	if err != nil || progress.Action() != action || progress.View().Stage != Pending || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
	kinds := []ActionKind{}
	for _, kind := range SupportedActionKinds() {
		if kind != ExcavationAction {
			kinds = append(kinds, kind)
		}
	}
	if err := ValidateHandlerCoverage(kinds); err == nil {
		t.Fatal("missing excavation handler accepted")
	}
}
