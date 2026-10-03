package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func milkRace(def Resource, perDay, body float64) AnimalRace {
	return AnimalRace{Def: def, BodySize: domain.Known(body), MinimumHandlingSkill: domain.Known(3),
		Products: []RaceProduct{{Kind: "milk", Def: "Milk", Amount: domain.Known(perDay), IntervalDays: domain.Known(1.0)}}}
}

func haulRace(def Resource, capacity, body float64) AnimalRace {
	return AnimalRace{Def: def, BodySize: domain.Known(body), CarryingCapacity: domain.Known(capacity), Trainables: []string{"Haul", "Obedience"}}
}

func raceCatalog(races ...AnimalRace) AnimalRaceCatalog {
	c := AnimalRaceCatalog{Races: map[Resource]AnimalRace{}}
	for _, r := range races {
		c.Races[r.Def] = r
	}
	return c
}

func planAnimal(id string, def Resource, gender string) UpkeepAnimal {
	return UpkeepAnimal{ID: PawnID(id), Definition: def, Gender: gender, Release: domain.Known(false), Bonded: domain.Known(false), Slaughter: domain.Known(false),
		SafeToSlaughter: domain.Known(true), SafeToRelease: domain.Known(false), Herd: HerdFacts{SlaughterBarred: domain.Known(false), EatingBarred: domain.Known(false)}}
}

func juvenile(a UpkeepAnimal) UpkeepAnimal {
	a.Herd.Adult = domain.Known(false)
	return a
}

func hauler(a UpkeepAnimal) UpkeepAnimal {
	a.Training = []HusbandryTrainable{trainable("Haul", true, true)}
	return a
}

func wildOf(def Resource) UpkeepAnimal {
	return UpkeepAnimal{ID: PawnID("wild-" + string(def)), Definition: def, Tameable: domain.Known(true), Tame: domain.Known(false)}
}

func goatHerd() []UpkeepAnimal {
	return []UpkeepAnimal{planAnimal("g1", "Goat", "Male"), planAnimal("g2", "Goat", "Female"), planAnimal("g3", "Goat", "Female")}
}

func milkInput(owned []UpkeepAnimal, wild ...UpkeepAnimal) HerdPlanInput {
	return HerdPlanInput{Animals: domain.Known(owned), Wild: domain.Known(wild),
		Races:  raceCatalog(milkRace("Cow", 12, 2.5), milkRace("Goat", 2, 0.8)),
		Budget: domain.Unknown[float64](), Wealth: domain.Unknown[WealthFacts](), Pens: domain.Unknown[[]PenGrazing](), Food: domain.Unknown[FoodPlan]()}
}

func TestHerdPlanMilkPicksCowsWhenObtainableElseGoats(t *testing.T) {
	plan := PlanHerd(milkInput(goatHerd(), wildOf("Cow")))
	if got := plan.Jobs[HerdJobMilk]; got.Target != "Cow" || len(got.Ranked) != 2 || got.Ranked[1] != "Goat" {
		t.Fatal(got)
	}
	if plan.Policy.PopulationMin["Cow"] != herdPairSize || plan.Policy.PopulationMax["Cow"] != herdPairSize+herdSpare {
		t.Fatal("cows are the wanted herd", plan.Policy)
	}
	if r := plan.Roles["Goat"]; r.Retiring || r.Job != HerdJobMilk {
		t.Fatal("goats keep working until cows are adult", r)
	}
	if _, ok := plan.Policy.PopulationMin["Goat"]; ok {
		t.Fatal("only the preferred race has a floor", plan.Policy.PopulationMin)
	}
	// No cow on the map, a trader or in a reward: goats are the best obtainable.
	plan = PlanHerd(milkInput(goatHerd()))
	if got := plan.Jobs[HerdJobMilk]; got.Target != "Goat" || plan.Policy.PopulationMin["Goat"] != herdPairSize {
		t.Fatal(got, plan.Policy)
	}
	// A trader's cows make them obtainable again.
	in := milkInput(goatHerd())
	in.Offers = []Resource{"Cow"}
	if got := PlanHerd(in).Jobs[HerdJobMilk]; got.Target != "Cow" {
		t.Fatal(got)
	}
	// A dangerous or untameable wild cow is not obtainable.
	wild := wildOf("Cow")
	wild.Tameable = domain.Known(false)
	if got := PlanHerd(milkInput(goatHerd(), wild)).Jobs[HerdJobMilk]; got.Target != "Goat" {
		t.Fatal(got)
	}
}

func TestHerdPlanMilkTieGoesToCows(t *testing.T) {
	in := milkInput(goatHerd(), wildOf("Cow"))
	in.Races = raceCatalog(milkRace("Cow", 2, 0.8), milkRace("Goat", 2, 0.8))
	if got := PlanHerd(in).Jobs[HerdJobMilk].Target; got != "Cow" {
		t.Fatal(got)
	}
}

func TestHerdPlanLoneCowIsAFounder(t *testing.T) {
	owned := append(goatHerd(), planAnimal("c1", "Cow", "Female"))
	plan := PlanHerd(milkInput(owned, wildOf("Cow")))
	cow := plan.Roles["Cow"]
	if !cow.Founder || !cow.WantMale || cow.WantFemale || !cow.Preferred {
		t.Fatal(cow)
	}
	if _, capped := plan.Policy.PopulationMax["Cow"]; capped {
		t.Fatal("a founder race has no cap", plan.Policy)
	}
	if plan.Roles["Goat"].Retiring {
		t.Fatal("goats stay until the cows are a pair")
	}
	rows := append(owned, planAnimal("c2", "Cow", "Female"))
	if got, _ := herdSurplusCandidates(rows, map[Resource]int64{"Cow": 0}, false, plan.Policy.Retired); len(got) != 0 {
		t.Fatal("the pair floor keeps founders", got)
	}
}

func TestHerdPlanGoatsRetireOnlyOnceCowsAreAdultPaired(t *testing.T) {
	retiring := func(cows ...UpkeepAnimal) HerdPlan {
		return PlanHerd(milkInput(append(goatHerd(), cows...), wildOf("Cow")))
	}
	if retiring(juvenile(planAnimal("c1", "Cow", "Male")), juvenile(planAnimal("c2", "Cow", "Female"))).Roles["Goat"].Retiring {
		t.Fatal("calves do not cover the job")
	}
	if retiring(planAnimal("c1", "Cow", "Female"), planAnimal("c2", "Cow", "Female")).Roles["Goat"].Retiring {
		t.Fatal("two cows without a bull are no pair")
	}
	plan := retiring(planAnimal("c1", "Cow", "Male"), planAnimal("c2", "Cow", "Female"))
	goat := plan.Roles["Goat"]
	if !goat.Retiring || !plan.Policy.Retired["Goat"] || plan.Policy.PopulationMax["Goat"] != 0 {
		t.Fatal(goat, plan.Policy)
	}
	if cow := plan.Roles["Cow"]; cow.Founder || cow.Job != HerdJobMilk {
		t.Fatal(cow)
	}
	// Retired goats go entirely, the breeding pair included; the cows stay.
	rows := append(goatHerd(), planAnimal("c1", "Cow", "Male"), planAnimal("c2", "Cow", "Female"))
	got, unknown := herdSurplusCandidates(rows, plan.Policy.PopulationMax, false, plan.Policy.Retired)
	if unknown || len(got) != 3 || got[0].animal.Definition != "Goat" || got[2].animal.Definition != "Goat" {
		t.Fatal(got, unknown)
	}
	if got := ReconcileHerdRemoval(domain.Known(rows), plan.Policy, domain.Unknown[FoodPlan]()); got.Method != "" {
		t.Fatal("no standing removal to cancel", got)
	}
	// A goat with another job is not surplus: this one also hauls.
	in := milkInput(append(append(goatHerd(), planAnimal("c1", "Cow", "Male"), planAnimal("c2", "Cow", "Female")), hauler(planAnimal("g4", "Goat", "Male"))), wildOf("Cow"))
	in.Races = raceCatalog(milkRace("Cow", 12, 2.5), func() AnimalRace {
		r := milkRace("Goat", 2, 0.8)
		r.Trainables = []string{"Haul"}
		r.CarryingCapacity = domain.Known(30.0)
		return r
	}())
	if r := PlanHerd(in).Roles["Goat"]; r.Retiring {
		t.Fatal("goats still haul", r)
	}
}

func TestHerdPlanSmallHaulersRetireAfterLargerLearnHaul(t *testing.T) {
	catalog := raceCatalog(haulRace("Muffalo", 150, 2.4), haulRace("Husky", 40, 0.9))
	plan := func(muffalo ...UpkeepAnimal) HerdPlan {
		owned := append([]UpkeepAnimal{hauler(planAnimal("h1", "Husky", "Male")), hauler(planAnimal("h2", "Husky", "Female"))}, muffalo...)
		return PlanHerd(HerdPlanInput{Animals: domain.Known(owned), Wild: domain.Known([]UpkeepAnimal{}), Races: catalog})
	}
	untrained := plan(planAnimal("m1", "Muffalo", "Male"), planAnimal("m2", "Muffalo", "Female"))
	if untrained.Jobs[HerdJobHaul].Target != "Muffalo" || untrained.Roles["Husky"].Retiring {
		t.Fatal("untrained muffalo do not replace trained huskies", untrained.Roles)
	}
	half := plan(hauler(planAnimal("m1", "Muffalo", "Male")), planAnimal("m2", "Muffalo", "Female"))
	if !half.Roles["Husky"].Retiring {
		t.Fatal("one trained muffalo carries more than both huskies", half.Roles)
	}
	small := plan(hauler(juvenile(planAnimal("m1", "Muffalo", "Male"))), hauler(juvenile(planAnimal("m2", "Muffalo", "Female"))))
	if small.Roles["Husky"].Retiring {
		t.Fatal("juvenile haulers carry nothing", small.Roles)
	}
	bigger := PlanHerd(HerdPlanInput{Animals: domain.Known([]UpkeepAnimal{hauler(planAnimal("m1", "Muffalo", "Male")), hauler(planAnimal("m2", "Muffalo", "Female")),
		hauler(planAnimal("h1", "Husky", "Male"))}), Wild: domain.Known([]UpkeepAnimal{}), Races: catalog})
	if !bigger.Roles["Husky"].Retiring || bigger.Jobs[HerdJobHaul].Heads != herdPairSize {
		t.Fatal(bigger)
	}
}

func TestHerdPlanCeilingsStillBind(t *testing.T) {
	in := milkInput(append(goatHerd(), planAnimal("x1", "Muffalo", "Male")), wildOf("Cow"))
	wealth := domain.Known(WealthFacts{Total: 100000})
	in.Wealth = wealth
	in.Budget = WealthBudget(domain.Known(1000.0), domain.Known(500.0), wealth)
	plan := PlanHerd(in)
	// Half the defense: the cow band (3 + 3) halves, a jobless race with one
	// animal drops to the unplanned floor.
	if plan.Policy.PopulationMax["Cow"] != 3 || plan.Policy.PopulationMin["Cow"] != 3 || plan.Policy.PopulationMax["Muffalo"] != herdUnplannedFloor {
		t.Fatal(plan.Policy)
	}
	in.Budget = WealthBudget(domain.Known(1000.0), domain.Known(1000.0), wealth)
	if plan := PlanHerd(in); len(plan.Policy.PopulationMax) != 1 || plan.Policy.PopulationMax["Cow"] != herdPairSize+herdSpare {
		t.Fatal("headroom leaves only the plan's own band", plan.Policy)
	}
	// Pasture: 3 penned muffalo of demand 4 against supply 3 keep 2.
	pen := []UpkeepAnimal{planAnimal("p1", "Muffalo", "Male"), planAnimal("p2", "Muffalo", "Female"), planAnimal("p3", "Muffalo", "Female")}
	for i := range pen {
		pen[i].RequiresPen = domain.Known(true)
	}
	in.Animals = domain.Known(pen)
	in.Pens = domain.Known([]PenGrazing{{ID: "pen", DemandPerDay: domain.Known(4.0), PasturePerDay: domain.Known(2.0), StoredNutrition: domain.Known(15.0)}})
	plan = PlanHerd(in)
	if plan.Policy.PopulationMax["Muffalo"] != 2 || !plan.Policy.FeedShort {
		t.Fatal(plan.Policy)
	}
}

func TestHerdPlanUnknownFactsLeavePlansUnchanged(t *testing.T) {
	// A goat with an unread yield cannot hold the milk job: it is neither
	// ranked nor retired.
	in := milkInput(append(goatHerd(), planAnimal("c1", "Cow", "Male"), planAnimal("c2", "Cow", "Female")), wildOf("Cow"))
	goat := milkRace("Goat", 2, 0.8)
	goat.Products[0].Amount = domain.Unknown[float64]()
	in.Races = raceCatalog(milkRace("Cow", 12, 2.5), goat)
	plan := PlanHerd(in)
	if plan.Roles["Goat"].Retiring || plan.Roles["Goat"].Job != HerdJobNone || plan.Jobs[HerdJobMilk].Target != "Cow" {
		t.Fatal(plan)
	}
	if got := PlanHerd(HerdPlanInput{Animals: domain.Unknown[[]UpkeepAnimal](), Races: in.Races}); len(got.Roles) != 0 || len(got.Policy.PopulationMax) != 0 {
		t.Fatal(got)
	}
	// An unread wild census offers no race beyond the owned ones.
	in = milkInput(goatHerd(), wildOf("Cow"))
	in.Wild = domain.Unknown[[]UpkeepAnimal]()
	if got := PlanHerd(in).Jobs[HerdJobMilk].Target; got != "Goat" {
		t.Fatal(got)
	}
}

func TestHerdPlanCompanionIsNeverCulled(t *testing.T) {
	dog := planAnimal("d1", "Husky", "Male")
	dog.Bonded = domain.Known(true)
	in := milkInput([]UpkeepAnimal{dog}, wildOf("Cow"))
	wealth := domain.Known(WealthFacts{Total: 100000})
	in.Wealth, in.Budget = wealth, WealthBudget(domain.Known(1000.0), domain.Known(100.0), wealth)
	plan := PlanHerd(in)
	if plan.Roles["Husky"].Job != HerdJobCompanion {
		t.Fatal(plan.Roles)
	}
	if _, capped := plan.Policy.PopulationMax["Husky"]; capped {
		t.Fatal("a companion race has no cap", plan.Policy)
	}
}

func TestHerdPlanFoodPlanTermsSizeTheJob(t *testing.T) {
	owned := append(goatHerd(), planAnimal("c1", "Cow", "Male"), planAnimal("c2", "Cow", "Female"))
	in := milkInput(owned)
	in.Food = domain.Known(FoodPlan{Portfolio: []FoodPlanEntry{{
		Channel:         FoodChannel{Kind: FoodAnimalProduct, ID: "Cow", NutritionPerDay: domain.Known(20.0), WorkPerDay: domain.Known(1.0), Terms: []FoodPlanTerm{{Name: "productive_animals", Value: 7}}},
		Decision:        FoodPlanOpen,
		DeliveredPerDay: 10,
	}}})
	// Seven cows' milk (84 a day) needs seven cows.
	if got := PlanHerd(in).Policy.PopulationMin["Cow"]; got != 7 {
		t.Fatal(got)
	}
}
