package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Milking and shearing are Handling work givers whose speed and yield stats
// scale with the Animals skill (#1650): a milk or wool job gives Handling an
// owner even when nobody is a natural specialist.
func TestRoutineWorkDemandHandlingFollowsMilkOrWoolJob(t *testing.T) {
	wool := AnimalRace{Def: "Sheep", BodySize: domain.Known(1.0), Products: []RaceProduct{{Kind: "wool", Def: "WoolSheep", Amount: domain.Known(30.0), IntervalDays: domain.Known(10.0)}}}
	facts := func(owned []UpkeepAnimal, races ...AnimalRace) RoutineFacts {
		var f RoutineFacts
		f.AnimalUpkeep.Animals = domain.Known(owned)
		f.AnimalUpkeep.WildAnimals = domain.Known([]UpkeepAnimal{})
		f.AnimalUpkeep.AnimalRaces = raceCatalog(races...)
		return f
	}
	cases := []struct {
		name string
		f    RoutineFacts
		want bool
	}{
		{"milk", facts(goatHerd(), milkRace("Goat", 2, 0.8)), true},
		{"wool", facts([]UpkeepAnimal{planAnimal("s1", "Sheep", "Female")}, wool), true},
		{"haul only", facts([]UpkeepAnimal{hauler(planAnimal("m1", "Muffalo", "Female"))}, haulRace("Muffalo", 100, 2)), false},
		{"no animals", facts(nil, milkRace("Goat", 2, 0.8)), false},
		{"unread animals", RoutineFacts{}, false},
	}
	for _, c := range cases {
		if got := RoutineWorkDemand(c.f, false).Handling; got != c.want {
			t.Errorf("%s: Handling demand %v, want %v", c.name, got, c.want)
		}
	}
}

func TestPlanWorkMilkJobGivesTopAnimalsPawnHandling(t *testing.T) {
	pawns := []WorkPawn{
		testWorkPawn("low", true, false, []WorkSkill{{Name: "Animals", Level: 3}}),
		testWorkPawn("top", true, false, []WorkSkill{{Name: "Animals", Level: 5}}),
	}
	plain, err := PlanWork(pawns, nil, WorkDemand{})
	if err != nil {
		t.Fatal(err)
	}
	if got := coverageOf(t, plain, WorkHandling).Owners; got != 0 {
		t.Fatalf("no herd job: Handling owners %d, want 0 (no specialist)", got)
	}
	decision, err := PlanWork(pawns, nil, WorkDemand{Handling: true})
	if err != nil {
		t.Fatal(err)
	}
	if got := coverageOf(t, decision, WorkHandling); got.Demand != 1 || got.Owners != 1 {
		t.Fatalf("coverage %+v", got)
	}
	if workValue(t, decision, "top", WorkHandling) != 1 || workValue(t, decision, "low", WorkHandling) == 1 {
		t.Fatalf("top handler owns Handling at 1: top=%d low=%d", workValue(t, decision, "top", WorkHandling), workValue(t, decision, "low", WorkHandling))
	}
}
