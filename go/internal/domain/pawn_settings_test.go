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
	if _, err := NewPlan("p", 1, []Action{a}); err != nil {
		t.Fatal(err)
	}
}
