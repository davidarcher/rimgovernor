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

// learning marks an animal as still having a wanted trainable to learn.
func learning(a UpkeepAnimal) UpkeepAnimal {
	a.Training = []HusbandryTrainable{{Def: "Obedience", Available: domain.Known(true), Learned: domain.Known(false)}}
	return a
}

func TestHerdMasterChoiceWarAnimalGetsFrontLineHandler(t *testing.T) {
	got := masterChoice(workAnimal("w1", "Warg", "", false, false))
	want := HusbandryChoice{Animal: "w1", Method: domain.HusbandryMaster, Argument: "fighter"}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("got %+v, want %+v (the sniper handles better but shoots from the rear)", got, want)
	}
	// Once mastered it follows when drafted, and never writes fieldwork.
	if got := masterChoice(workAnimal("w1", "Warg", "fighter", false, true)); got.Method != domain.HusbandryFollowDrafted || got.Argument != "true" {
		t.Fatalf("war flag: %+v", got)
	}
	if got := masterChoice(workAnimal("w1", "Warg", "fighter", true, true)); got.Method != "" {
		t.Fatalf("settled war animal chose %+v", got)
	}
	// A rear master is replaced by the front line.
	if got := masterChoice(workAnimal("w1", "Warg", "sniper", true, false)); got.Argument != "fighter" {
		t.Fatalf("got %+v", got)
	}
}

func TestHerdMasterChoiceUnbondedHaulAnimalGetsNothing(t *testing.T) {
	if got := masterChoice(workAnimal("m1", "Muffalo", "", true, true)); got.Method != "" {
		t.Fatalf("haul animal chose %+v", got)
	}
}

func TestHerdMasterChoiceLearningAnimalGetsBestAnimalsHandler(t *testing.T) {
	// The sniper has the best Animals skill; follow flags are left alone and
	// one handler masters a whole pack.
	pack := []UpkeepAnimal{learning(workAnimal("m1", "Muffalo", "", true, true)), learning(workAnimal("w1", "Warg", "", false, false))}
	if got := masterChoice(pack...); got.Animal != "m1" || got.Method != domain.HusbandryMaster || got.Argument != "sniper" {
		t.Fatalf("got %+v", got)
	}
	pack[0].Master = domain.Known("sniper")
	if got := masterChoice(pack...); got.Animal != "w1" || got.Argument != "sniper" {
		t.Fatalf("pack not extended: %+v", got)
	}
	pack[1].Master = domain.Known("sniper")
	if got := masterChoice(pack...); got.Method != "" {
		t.Fatalf("settled pack chose %+v", got)
	}
}

func TestHerdMasterChoiceLearningIgnoresUnavailableAndLearned(t *testing.T) {
	a := workAnimal("m1", "Muffalo", "", false, false)
	a.Training = []HusbandryTrainable{
		{Def: "Obedience", Available: domain.Known(true), Learned: domain.Known(true)},
		{Def: "Haul", Available: domain.Known(false), Learned: domain.Known(false)},
	}
	if got := masterChoice(a); got.Method != "" {
		t.Fatalf("trained haul animal chose %+v", got)
	}
	a.Training[1].Available = domain.Unknown[bool]()
	if got := masterChoice(a); got.Method != "" {
		t.Fatalf("unread availability chose %+v", got)
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

func bondedAnimal(id string, def Resource, master string, bonded ...string) UpkeepAnimal {
	a := workAnimal(id, def, master, false, false)
	a.BondedPawns = bonded
	return a
}

func TestHerdMasterChoiceBondedAnimalGetsBondedColonist(t *testing.T) {
	// Bond partners off the roster (a prisoner, a pawn who left) are ignored;
	// of those on it the first by id masters, whatever the race's job.
	for _, def := range []Resource{"Husky", "Warg", "Muffalo"} {
		got := masterChoice(bondedAnimal("h1", def, "", "prisoner", "sniper", "hauler"))
		want := HusbandryChoice{Animal: "h1", Method: domain.HusbandryMaster, Argument: "hauler"}
		if !reflect.DeepEqual(got, want) {
			t.Fatalf("%s: got %+v, want %+v", def, got, want)
		}
	}
	// A master who is a bond partner on the roster stays; no flag is written.
	if got := masterChoice(bondedAnimal("w1", "Warg", "sniper", "hauler", "sniper")); got.Method != "" {
		t.Fatalf("settled bonded animal chose %+v", got)
	}
	// A master who is not a bond partner is replaced, even while learning.
	if got := masterChoice(learning(bondedAnimal("h1", "Husky", "fighter", "hauler"))); got.Argument != "hauler" {
		t.Fatalf("got %+v", got)
	}
}

func TestHerdMasterChoiceUnbondedCompanionWithoutTrainingStaysUnmastered(t *testing.T) {
	for _, bonded := range [][]string{nil, {"prisoner", "ghost"}} {
		if got := masterChoice(bondedAnimal("h1", "Husky", "", bonded...)); got.Method != "" {
			t.Fatalf("bonded %v chose %+v", bonded, got)
		}
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
