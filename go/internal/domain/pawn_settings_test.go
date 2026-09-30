package domain

import "testing"

func TestHostilitySettingRequiresPawnAndMode(t *testing.T) {
	if _, err := NewHostilitySetting("", HostilityFlee); err == nil {
		t.Fatal("empty pawn accepted")
	}
	if _, err := NewHostilitySetting("Human1", "Charge"); err == nil {
		t.Fatal("unknown mode accepted")
	}
	v, err := NewHostilitySetting("Human1", HostilityIgnore)
	if err != nil {
		t.Fatal(err)
	}
	a, err := NewPawnSettingsAction("s1", v)
	if got, ok := a.PawnSettings(); err != nil || !ok || got != v || a.Kind() != PawnSettingsAction {
		t.Fatal(a, err)
	}
	if _, err := NewPawnSettingsAction("s1", PawnSettings{}); err == nil {
		t.Fatal("zero settings accepted")
	}
	if _, err := NewMedicineCarrySetting("Human1", 4); err == nil {
		t.Fatal("carry above 3 accepted")
	}
	carry, err := NewMedicineCarrySetting("Human1", 0)
	if err != nil {
		t.Fatal(err)
	}
	if n, ok := carry.MedicineCarry(); !ok || n != 0 || carry == v {
		t.Fatal(carry)
	}
	if _, err := NewPlan("p", 1, []Action{a}); err != nil {
		t.Fatal(err)
	}
}

func TestMedicalCareSettingTiers(t *testing.T) {
	if _, err := NewMedicalCareSetting("Human1", "Glitter"); err == nil {
		t.Fatal("unknown tier accepted")
	}
	for _, c := range MedicalCares {
		v, err := NewMedicalCareSetting("Human1", c)
		if err != nil || v.MedicalCare() != c || v.Hostility() != "" {
			t.Fatal(c, err)
		}
		if _, err := NewPawnSettingsAction("s1", v); err != nil {
			t.Fatal(err)
		}
	}
	if CareHerbal.Raised() != CareNormal || CareBest.Raised() != CareBest {
		t.Fatal("raise")
	}
}
