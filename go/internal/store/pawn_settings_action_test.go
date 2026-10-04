package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A pawn settings action (#1299) persists its pawn and hostility response.
func TestPawnSettingsActionRoundTrips(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	value, err := domain.NewHostilitySetting("Human7", domain.HostilityIgnore)
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewPawnSettingsAction("hostility", value)
	if err != nil {
		t.Fatal(err)
	}
	tend, err := domain.NewSelfTendSetting("Human7", false)
	if err != nil {
		t.Fatal(err)
	}
	b, err := domain.NewPawnSettingsAction("self-tend", tend)
	if err != nil {
		t.Fatal(err)
	}
	rename, err := domain.NewNicknameSetting("Human8", "Bob")
	if err != nil {
		t.Fatal(err)
	}
	n, err := domain.NewPawnSettingsAction("nickname", rename)
	if err != nil {
		t.Fatal(err)
	}
	care, err := domain.NewMedicalCareSetting("Human9", domain.CareBest)
	if err != nil {
		t.Fatal(err)
	}
	c, err := domain.NewPawnSettingsAction("care", care)
	if err != nil {
		t.Fatal(err)
	}
	carryValue, err := domain.NewMedicineCarrySetting("Human7", 0)
	if err != nil {
		t.Fatal(err)
	}
	carry, err := domain.NewPawnSettingsAction("carry", carryValue)
	if err != nil {
		t.Fatal(err)
	}
	readingValue, err := domain.NewReadingPolicySetting("Human7", "Bob")
	if err != nil {
		t.Fatal(err)
	}
	reading, err := domain.NewPawnSettingsAction("reading", readingValue)
	if err != nil {
		t.Fatal(err)
	}
	contents, err := domain.NewReadingPolicy("Bob", []string{"Novel", "Book_Schematic"})
	if err != nil {
		t.Fatal(err)
	}
	write, err := domain.NewReadingPolicyAction("reading-write", contents)
	if err != nil {
		t.Fatal(err)
	}
	empty, err := domain.NewReadingPolicy("Tim", nil)
	if err != nil {
		t.Fatal(err)
	}
	none, err := domain.NewReadingPolicyAction("reading-none", empty)
	if err != nil {
		t.Fatal(err)
	}
	drugValue, err := domain.NewDrugPolicySetting("Human7", "Bob")
	if err != nil {
		t.Fatal(err)
	}
	drug, err := domain.NewPawnSettingsAction("drug", drugValue)
	if err != nil {
		t.Fatal(err)
	}
	drugContents, err := domain.NewDrugPolicy("Bob", []domain.DrugPolicyEntry{
		{Drug: "SmokeleafJoint", Joy: true, DaysFrequency: 1, OnlyIfMoodBelow: 1, OnlyIfJoyBelow: 1},
		{Drug: "Beer", Joy: true, Scheduled: true, DaysFrequency: 2.5, OnlyIfMoodBelow: 0.3, OnlyIfJoyBelow: 1, TakeToInventory: 2},
	})
	if err != nil {
		t.Fatal(err)
	}
	drugWrite, err := domain.NewDrugPolicyAction("drug-write", drugContents)
	if err != nil {
		t.Fatal(err)
	}
	drugEmpty, err := domain.NewDrugPolicy("Tim", nil)
	if err != nil {
		t.Fatal(err)
	}
	drugNone, err := domain.NewDrugPolicyAction("drug-none", drugEmpty)
	if err != nil {
		t.Fatal(err)
	}
	dietValue, err := domain.NewFoodPolicySetting("Human7", "Bob")
	if err != nil {
		t.Fatal(err)
	}
	diet, err := domain.NewPawnSettingsAction("diet", dietValue)
	if err != nil {
		t.Fatal(err)
	}
	foods, err := domain.NewFoodPolicy("Bob", []string{"MealSimple", "Meat_Human"})
	if err != nil {
		t.Fatal(err)
	}
	foodWrite, err := domain.NewFoodPolicyAction("diet-write", foods)
	if err != nil {
		t.Fatal(err)
	}
	mechMode, err := domain.NewMechWorkModeSetting("Mech1", "Work")
	if err != nil {
		t.Fatal(err)
	}
	mechModeAction, err := domain.NewPawnSettingsAction("mech-mode", mechMode)
	if err != nil {
		t.Fatal(err)
	}
	mechGroup, err := domain.NewMechControlGroupSetting("Mech1", 1)
	if err != nil {
		t.Fatal(err)
	}
	mechGroupAction, err := domain.NewPawnSettingsAction("mech-group", mechGroup)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("settings-plan", 1, []domain.Action{a, b, n, c, carry, write, none, reading, drugWrite, drugNone, drug, foodWrite, diet, mechModeAction, mechGroupAction})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "restore", p); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, "settings-plan")
	if err != nil {
		t.Fatal(err)
	}
	got := loaded.Spec.Actions()
	if len(got) != 15 {
		t.Fatal(got)
	}
	if v, ok := got[0].PawnSettings(); !ok || v != value {
		t.Fatal(v, value)
	}
	if v, ok := got[1].PawnSettings(); !ok || v != tend {
		t.Fatal(v, tend)
	}
	if v, ok := got[4].PawnSettings(); !ok || v != carryValue {
		t.Fatal(v, carryValue)
	}
	if v, ok := got[2].PawnSettings(); !ok || v != rename {
		t.Fatal(v, rename)
	}
	if v, ok := got[3].PawnSettings(); !ok || v != care {
		t.Fatal(v, care)
	}
	if v, ok := got[5].ReadingPolicy(); !ok || v != contents {
		t.Fatal(v, contents)
	}
	if v, ok := got[6].ReadingPolicy(); !ok || v != empty {
		t.Fatal(v, empty)
	}
	if v, ok := got[7].PawnSettings(); !ok || v != readingValue {
		t.Fatal(v, readingValue)
	}
	if v, ok := got[8].DrugPolicy(); !ok || v != drugContents || v.Entries()[0].Drug != "Beer" {
		t.Fatal(v, drugContents)
	}
	if v, ok := got[9].DrugPolicy(); !ok || v != drugEmpty {
		t.Fatal(v, drugEmpty)
	}
	if v, ok := got[10].PawnSettings(); !ok || v != drugValue {
		t.Fatal(v, drugValue)
	}
	if v, ok := got[11].FoodPolicy(); !ok || v != foods {
		t.Fatal(v, foods)
	}
	if v, ok := got[12].PawnSettings(); !ok || v != dietValue {
		t.Fatal(v, dietValue)
	}
	if v, ok := got[13].PawnSettings(); !ok || v != mechMode {
		t.Fatal(v, mechMode)
	}
	if v, ok := got[14].PawnSettings(); !ok || v != mechGroup {
		t.Fatal(v, mechGroup)
	}
}
