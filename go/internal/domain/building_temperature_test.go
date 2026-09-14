package domain

import "testing"

func TestBuildingTemperatureRange(t *testing.T) {
	valid := []float64{-273.15, 0, 21.5, 1000}
	for _, celsius := range valid {
		if _, err := NewBuildingTemperature("thing", celsius, "token"); err != nil {
			t.Fatalf("expected %v celsius to be valid: %v", celsius, err)
		}
	}
	invalid := []float64{-273.16, -274, 1000.01, 5000}
	for _, celsius := range invalid {
		if _, err := NewBuildingTemperature("thing", celsius, "token"); err == nil {
			t.Fatalf("expected %v celsius to be rejected", celsius)
		}
	}
}

func TestBuildingTemperatureIdentity(t *testing.T) {
	if _, err := NewBuildingTemperature("", 20, "token"); err == nil {
		t.Fatal("expected empty thing to be rejected")
	}
	if _, err := NewBuildingTemperature("thing", 20, ""); err == nil {
		t.Fatal("expected empty before token to be rejected")
	}
	bt, err := NewBuildingTemperature("thing", 20, "token")
	if err != nil {
		t.Fatal(err)
	}
	if bt.Thing() != "thing" || bt.Celsius() != 20 || bt.BeforeToken() != "token" {
		t.Fatal("incorrect building temperature accessors")
	}
}

func TestBuildingTemperatureAction(t *testing.T) {
	bt, err := NewBuildingTemperature("thing", 20, "token")
	if err != nil {
		t.Fatal(err)
	}
	action, err := NewBuildingTemperatureAction("a1", bt)
	if err != nil {
		t.Fatal(err)
	}
	if action.Kind() != BuildingTemperatureAction || action.ID() != "a1" {
		t.Fatal("incorrect building temperature action identity")
	}
	value, ok := action.BuildingTemperature()
	if !ok || value != bt {
		t.Fatal("incorrect building temperature action accessor")
	}
	if _, err := NewBuildingTemperatureAction("", bt); err == nil {
		t.Fatal("expected invalid action identity to be rejected")
	}
}

func TestBuildingTemperatureSupportedByPlan(t *testing.T) {
	bt, err := NewBuildingTemperature("thing", 20, "token")
	if err != nil {
		t.Fatal(err)
	}
	action, err := NewBuildingTemperatureAction("a1", bt)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := NewPlan("p1", 1, []Action{action}); err != nil {
		t.Fatal(err)
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
}
