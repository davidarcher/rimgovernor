package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func warRace(def Resource, power, body float64, skill int) AnimalRace {
	return AnimalRace{Def: def, BodySize: domain.Known(body), CombatPower: domain.Known(power), MinimumHandlingSkill: domain.Known(skill),
		Trainables: []string{"Obedience", "Release", "Rescue"}}
}

func animalsHandler(level int) domain.Fact[[]PawnProfile] {
	return domain.Known([]PawnProfile{{ID: "h", Skills: map[string]ProfileSkill{"Animals": {Name: "Animals", Level: level}}, Incapable: map[WorkType]bool{}}})
}

func warInput(headroom domain.Fact[float64], handler int, wild ...UpkeepAnimal) HerdPlanInput {
	return HerdPlanInput{Animals: domain.Known([]UpkeepAnimal{}), Wild: domain.Known(wild),
		Races:  raceCatalog(warRace("Warg", 40, 1.5, 6), warRace("Bear_Grizzly", 60, 2, 8), warRace("Thrumbo", 300, 4, 14), warRace("Husky", 10, 0.5, 2)),
		Budget: headroom, Wealth: domain.Known(WealthFacts{Total: 100000}), Pens: domain.Unknown[[]PenGrazing](), Food: domain.Unknown[FoodPlan](),
		Handlers: animalsHandler(handler)}
}

func TestHerdPlanWantsWarAnimalWhenHandlerAndBudgetAllow(t *testing.T) {
	in := warInput(domain.Known(0.0), 10, wildOf("Warg"), wildOf("Bear_Grizzly"), wildOf("Thrumbo"))
	plan := PlanHerd(in)
	// The strongest race the handler clears: bears, not the thrumbo above her skill.
	if got := plan.Jobs[HerdJobWar]; got.Target != "Bear_Grizzly" || got.Heads != herdPairSize || len(got.Ranked) != 2 {
		t.Fatal(got)
	}
	role := plan.Roles["Bear_Grizzly"]
	if role.Job != HerdJobWar || !role.Preferred || plan.Policy.PopulationMin["Bear_Grizzly"] != herdPairSize {
		t.Fatal(role, plan.Policy)
	}
	// A handler who clears the thrumbo's catalog minimum picks it.
	in.Handlers = animalsHandler(14)
	if got := PlanHerd(in).Jobs[HerdJobWar].Target; got != "Thrumbo" {
		t.Fatal(got)
	}
	// An untameable wild thrumbo is not obtainable.
	thrumbo := wildOf("Thrumbo")
	thrumbo.Tameable = domain.Known(false)
	in.Wild = domain.Known([]UpkeepAnimal{wildOf("Warg"), thrumbo})
	if got := PlanHerd(in).Jobs[HerdJobWar].Target; got != "Warg" {
		t.Fatal(got)
	}
}

func TestHerdPlanWantsNoWarAnimalWithoutHandlerOrBudget(t *testing.T) {
	wild := []UpkeepAnimal{wildOf("Warg"), wildOf("Bear_Grizzly")}
	unread := warInput(domain.Known(0.0), 10, wild...)
	unread.Handlers = domain.Unknown[[]PawnProfile]()
	for name, in := range map[string]HerdPlanInput{
		"negative headroom": warInput(domain.Known(-1.0), 10, wild...),
		"unknown budget":    warInput(domain.Unknown[float64](), 10, wild...),
		"skill too low":     warInput(domain.Known(0.0), 3, wild...),
		"unread roster":     unread,
	} {
		if plan := PlanHerd(in); len(plan.Jobs) != 0 || len(plan.Policy.PopulationMin) != 0 {
			t.Fatal(name, plan.Jobs, plan.Policy)
		}
	}
}

func TestHerdPlanKeepsTrainedWarAnimalsWhateverTheBudget(t *testing.T) {
	warg := func(id, gender string) UpkeepAnimal {
		a := planAnimal(id, "Warg", gender)
		a.Training = []HusbandryTrainable{trainable("Release", true, true)}
		return a
	}
	in := warInput(domain.Known(-1.0), 3)
	in.Animals = domain.Known([]UpkeepAnimal{warg("w1", "Male"), warg("w2", "Female")})
	plan := PlanHerd(in)
	if r := plan.Roles["Warg"]; r.Job != HerdJobWar || r.Retiring {
		t.Fatal(r)
	}
}
