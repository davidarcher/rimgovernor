package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func handlerAt(level int) domain.Fact[[]PawnProfile] {
	return domain.Known([]PawnProfile{BuildProfile(WorkPawn{ID: "handler", Work: testAllWork(), Skills: domain.Known([]WorkSkill{{Name: "Animals", Level: level}})})})
}

func levelingInput(level int, wild ...UpkeepAnimal) HerdPlanInput {
	in := milkInput(goatHerd(), append(wild, wildOf("Cow"))...)
	alpaca := AnimalRace{Def: "Alpaca", MinimumHandlingSkill: domain.Known(0), BodySize: domain.Known(1.0)}
	in.Races = raceCatalog(milkRace("Cow", 12, 2.5), milkRace("Goat", 2, 0.8), alpaca)
	in.Handlers = handlerAt(level)
	return in
}

func TestHerdPlanLevelsOnAnEasyAnimalWhenNoHandlerClearsTheWantedRace(t *testing.T) {
	plan := PlanHerd(levelingInput(1, wildOf("Alpaca")))
	if plan.Jobs[HerdJobMilk].Target != "Cow" || plan.Leveling != "Alpaca" || plan.Policy.PopulationMin["Alpaca"] != 1 {
		t.Fatal("the easy race is tamed while no handler clears cows", plan.Leveling, plan.Policy)
	}
	cow := wildOf("Cow")
	cow.MinimumHandlingSkill = domain.Known(3)
	wild := domain.Known([]UpkeepAnimal{cow, wildOf("Alpaca")})
	choice := SelectHusbandryMethod(domain.Known(goatHerd()), wild, feedFine, plan.Policy, handlerAt(1))
	if choice.Method != domain.HusbandryTame || choice.Animal != "wild-Alpaca" {
		t.Fatal("the unreachable cow is skipped for the easy animal", choice)
	}
}

func TestHerdPlanLevelingStopsOnceAHandlerClearsTheWantedRace(t *testing.T) {
	plan := PlanHerd(levelingInput(3, wildOf("Alpaca")))
	if plan.Leveling != "" || plan.Policy.PopulationMin["Alpaca"] != 0 {
		t.Fatal("a skilled handler tames cows directly", plan.Leveling, plan.Policy)
	}
}

func TestHerdPlanLevelingNeedsAWildEasyAnimal(t *testing.T) {
	plan := PlanHerd(levelingInput(1))
	if plan.Leveling != "" || len(plan.Policy.PopulationMin) != 1 || plan.Roles["Alpaca"].Race != "" {
		t.Fatal("no wild easy animal leaves the plan unchanged", plan.Leveling, plan.Policy)
	}
}
