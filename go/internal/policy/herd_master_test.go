package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func masterRoster() domain.Fact[[]PawnProfile] {
	skills := func(animals, melee, shooting int) map[string]ProfileSkill {
		return map[string]ProfileSkill{"Animals": {Level: animals}, "Melee": {Level: melee}, "Shooting": {Level: shooting}}
	}
	return domain.Known([]PawnProfile{
		roleProfile("fighter", false, skills(4, 12, 3)),
		roleProfile("sniper", true, skills(9, 2, 12)),
		roleProfile("hauler", false, skills(6, 1, 5)),
	})
}

func masterHerd() HerdPolicy {
	return HerdPolicy{Roles: map[Resource]HerdRole{
		"Warg":    {Race: "Warg", Job: HerdJobWar},
		"Muffalo": {Race: "Muffalo", Job: HerdJobHaul},
		"Husky":   {Race: "Husky", Job: HerdJobCompanion},
	}}
}

func workAnimal(id string, def Resource, master string, drafted, fieldwork bool) UpkeepAnimal {
	a := planAnimal(id, def, "Male")
	a.Obedient, a.Master = domain.Known(true), domain.Known(master)
	a.FollowDrafted, a.FollowFieldwork = domain.Known(drafted), domain.Known(fieldwork)
	return a
}

func masterChoice(rows ...UpkeepAnimal) HusbandryChoice {
	return HerdMasterChoice(domain.Known(rows), masterHerd(), masterRoster())
}

func TestHerdMasterChoiceWarAnimalGetsFrontLineHandler(t *testing.T) {
	got := masterChoice(workAnimal("w1", "Warg", "", false, false))
	want := HusbandryChoice{Animal: "w1", Method: domain.HusbandryMaster, Argument: "fighter"}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("got %+v, want %+v (the sniper handles better but shoots from the rear)", got, want)
	}
}

func TestHerdMasterChoiceHaulAnimalGetsBestHauler(t *testing.T) {
	got := masterChoice(workAnimal("m1", "Muffalo", "", false, false))
	want := HusbandryChoice{Animal: "m1", Method: domain.HusbandryMaster, Argument: "sniper"}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("got %+v, want %+v", got, want)
	}
}

func TestHerdMasterChoiceFlagsFollowJobOneWritePerCall(t *testing.T) {
	war := workAnimal("w1", "Warg", "fighter", false, true)
	if got := masterChoice(war); got.Method != domain.HusbandryFollowDrafted || got.Argument != "true" {
		t.Fatalf("war flags: %+v", got)
	}
	war.FollowDrafted = domain.Known(true)
	if got := masterChoice(war); got.Method != domain.HusbandryFollowFieldwork || got.Argument != "false" {
		t.Fatalf("war fieldwork flag: %+v", got)
	}
	haul := workAnimal("m1", "Muffalo", "hauler", true, false)
	if got := masterChoice(haul); got.Method != domain.HusbandryFollowDrafted || got.Argument != "false" {
		t.Fatalf("haul flags: %+v", got)
	}
}

func TestHerdMasterChoiceMatchingAnimalSelectsNothing(t *testing.T) {
	// A fitting master stays even when another colonist would rank higher.
	got := masterChoice(workAnimal("w1", "Warg", "fighter", true, false), workAnimal("m1", "Muffalo", "hauler", false, true))
	if got.Reason != HusbandryNoDeficit || got.Method != "" {
		t.Fatalf("matching animals chose %+v", got)
	}
}

func TestHerdMasterChoiceReassignsOnlyMismatchedMaster(t *testing.T) {
	// The sniper shoots from the rear, so a war animal reassigns to the fighter;
	// a master the roster no longer holds fits no job.
	got := masterChoice(workAnimal("w1", "Warg", "sniper", true, false))
	if got.Method != domain.HusbandryMaster || got.Argument != "fighter" {
		t.Fatalf("got %+v", got)
	}
	if got := masterChoice(workAnimal("m1", "Muffalo", "ghost", false, true)); got.Method != domain.HusbandryMaster {
		t.Fatalf("departed master kept: %+v", got)
	}
}

func TestHerdMasterChoiceSkipsIneligibleAnimals(t *testing.T) {
	notObedient := workAnimal("a1", "Warg", "", false, false)
	notObedient.Obedient = domain.Known(false)
	slaughter := workAnimal("a2", "Warg", "", false, false)
	slaughter.Slaughter = domain.Known(true)
	release := workAnimal("a3", "Muffalo", "", false, false)
	release.Release = domain.Known(true)
	companion := workAnimal("a4", "Husky", "", false, false)
	unknownMaster := workAnimal("a5", "Warg", "", false, false)
	unknownMaster.Master = domain.Unknown[string]()
	unknownObedient := workAnimal("a6", "Warg", "", false, false)
	unknownObedient.Obedient = domain.Unknown[bool]()
	unplanned := workAnimal("a7", "Goat", "", false, false)
	got := masterChoice(notObedient, slaughter, release, companion, unknownMaster, unknownObedient, unplanned)
	if got.Reason != HusbandryNoDeficit || got.Method != "" {
		t.Fatalf("ineligible animal chosen: %+v", got)
	}
	// An ineligible animal does not block an eligible one after it.
	if got := masterChoice(notObedient, workAnimal("b1", "Warg", "", false, false)); got.Animal != "b1" {
		t.Fatalf("got %+v", got)
	}
}

func TestHerdMasterChoiceRetiringRaceAndUnknownRosterSelectNothing(t *testing.T) {
	herd := masterHerd()
	herd.Roles["Warg"] = HerdRole{Race: "Warg", Job: HerdJobWar, Retiring: true}
	animals := domain.Known([]UpkeepAnimal{workAnimal("w1", "Warg", "", false, false)})
	if got := HerdMasterChoice(animals, herd, masterRoster()); got.Method != "" {
		t.Fatalf("retiring race mastered: %+v", got)
	}
	if got := HerdMasterChoice(animals, masterHerd(), domain.Unknown[[]PawnProfile]()); got.Method != "" {
		t.Fatalf("unknown roster mastered: %+v", got)
	}
	if got := HerdMasterChoice(domain.Unknown[[]UpkeepAnimal](), masterHerd(), masterRoster()); got.Method != "" {
		t.Fatalf("unread census mastered: %+v", got)
	}
}

func TestHerdSaleAnimalsUnknownMasterSellsNothing(t *testing.T) {
	goats := []UpkeepAnimal{bondedAs(planAnimal("g1", "Goat", "Male"), false)}
	rows, plan := retiredGoatsPlan(goats)
	rows[0].Master = domain.Unknown[string]()
	if got := HerdSaleAnimals(domain.Known(rows), plan.Policy); got["g1"] {
		t.Fatalf("unknown master sold: %v", got)
	}
}
