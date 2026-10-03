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

// The mech arms (#1685) take a mech pawn and a def or a non-negative group,
// and stay distinct values.
func TestMechSettingsValidate(t *testing.T) {
	if _, err := NewMechWorkModeSetting("", "Work"); err == nil {
		t.Fatal("empty mech accepted")
	}
	if _, err := NewMechWorkModeSetting("Mech1", ""); err == nil {
		t.Fatal("empty mode accepted")
	}
	if _, err := NewMechControlGroupSetting("Mech1", -1); err == nil {
		t.Fatal("negative group accepted")
	}
	mode, err := NewMechWorkModeSetting("Mech1", "Work")
	if err != nil {
		t.Fatal(err)
	}
	group, err := NewMechControlGroupSetting("Mech1", 0)
	if err != nil {
		t.Fatal(err)
	}
	if m, ok := mode.MechWorkMode(); !ok || m != "Work" || mode == group {
		t.Fatal(mode, group)
	}
	if g, ok := group.MechControlGroup(); !ok || g != 0 {
		t.Fatal(group)
	}
	for _, v := range []PawnSettings{mode, group} {
		if _, err := NewPawnSettingsAction("s1", v); err != nil {
			t.Fatal(err)
		}
	}
}
