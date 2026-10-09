package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A race is a herd with a unit of its own at a fertile pair or herdMinAnimals
// animals when it has a ceiling to size a unit from; fewer unpaired
// animals stay with the misc unit, which is sized from the other ceilings.
func TestHerdRacesAreAPairOrFiveAnimals(t *testing.T) {
	pair := []UpkeepAnimal{planAnimal("c1", "Cow", "Male"), planAnimal("c2", "Cow", "Female")}
	plan := PlanHerd(milkInput(pair))
	if len(plan.Herds) != 1 || plan.Herds[0] != "Cow" || len(plan.HerdUnits()) != 1 || plan.HerdUnits()[0] != (HerdCeiling{"Cow", int(plan.Policy.PopulationMax["Cow"])}) {
		t.Fatal("a pair is a herd sized from its ceiling", plan.Herds, plan.HerdUnits())
	}
	if got := plan.PenAnimals(); got != penAnimalsFloor {
		t.Fatal("the herd's ceiling is no misc animal", got)
	}
	// Four unpaired animals (a founder has no ceiling) stay misc.
	females := []UpkeepAnimal{planAnimal("c1", "Cow", "Female"), planAnimal("c2", "Cow", "Female"), planAnimal("c3", "Cow", "Female"), planAnimal("c4", "Cow", "Female")}
	if plan = PlanHerd(milkInput(females)); len(plan.Herds) != 0 || len(plan.HerdUnits()) != 0 {
		t.Fatal("unpaired animals are no herd", plan.Herds)
	}
	// Five animals of a race with a ceiling and no pair are a herd, four are not.
	in := func(n int) HerdPlanInput {
		var owned []UpkeepAnimal
		for i := 0; i < n; i++ {
			owned = append(owned, planAnimal(string(rune('a'+i)), "Chicken", "Female"))
		}
		return HerdPlanInput{Animals: domain.Known(owned), Wild: domain.Known([]UpkeepAnimal(nil)), Races: raceCatalog(milkRace("Cow", 12, 2.5), AnimalRace{Def: "Chicken"}),
			Budget: domain.Known(-1.0), Wealth: domain.Known(WealthFacts{Total: 100000}), Pens: domain.Unknown[[]PenGrazing](), Food: domain.Unknown[FoodPlan]()}
	}
	if plan = PlanHerd(in(5)); len(plan.Herds) != 1 || plan.Herds[0] != "Chicken" {
		t.Fatal("five animals are a herd", plan.Herds, plan.Policy.PopulationMax)
	}
	if plan = PlanHerd(in(4)); len(plan.Herds) != 0 {
		t.Fatal("four unpaired animals are not", plan.Herds)
	}
}
