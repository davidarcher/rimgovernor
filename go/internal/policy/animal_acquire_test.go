package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const acquireDay = domain.TicksPerDay

func acquireCow() AnimalRace {
	return AnimalRace{Def: "Cow", BodySize: domain.Known(2.0), TameChanceFactor: domain.Known(0.5), ManhunterOnTameFail: domain.Known(0.0),
		AdultMinAgeTicks: domain.Known(int64(10 * acquireDay)), MilkableMinAgeTicks: domain.Known(int64(6 * acquireDay)),
		AdultFeedPerDay: domain.Known(2.0), MeatDef: "Meat_Cow", MeatAmount: domain.Known(100.0), MeatNutritionPerUnit: domain.Known(0.05),
		Products: []RaceProduct{{Kind: "milk", Def: "Milk", Amount: domain.Known(10.0), IntervalDays: domain.Known(1.0), NutritionPerUnit: domain.Known(0.5)}}}
}

func acquireInput(races ...AnimalRace) AnimalAcquisition {
	in := AnimalAcquisition{Races: raceCatalog(races...), Handlers: handlerAt(5)}
	in.Races.Interaction = AnimalInteraction{TalkTicks: domain.Known(270), FeedTicks: domain.Known(270), Feeds: domain.Known(2)}
	return in
}

func wildYoung(def Resource, gender string, ageYears float64) UpkeepAnimal {
	return UpkeepAnimal{ID: "wild-" + PawnID(def), Definition: def, Gender: gender, Tameable: domain.Known(true), Tame: domain.Known(false),
		MinimumHandlingSkill: domain.Known(2), Herd: HerdFacts{AgeYears: domain.Known(ageYears)}}
}

func TestTameFoodChannelNetsFeedAndLeadsUntilMilkable(t *testing.T) {
	t.Parallel()
	in := acquireInput(acquireCow())
	in.Wild = domain.Known([]UpkeepAnimal{wildYoung("Cow", "Female", 1.0/DaysPerYearF)})
	got := TameFoodChannels(in)
	if len(got) != 1 || got[0].Kind != CandidateTame || got[0].ID != "tame:wild-Cow" {
		t.Fatalf("channels %v", got)
	}
	c := got[0]
	if n, _ := c.Nutrition().PerDay.Value(); n != 3 {
		t.Errorf("net rate %v, want 5 milk - 2 feed", n)
	}
	if lead, _ := c.LeadDays.Value(); lead != 5 {
		t.Errorf("lead %v, want 6 milkable days - 1 day old", lead)
	}
	// 1/0.5 attempts of three 270-tick talks and two 270-tick feeds.
	if up, _ := c.UpfrontCost.LaborTicks.Value(); up != 2*(3*270+2*270) {
		t.Errorf("upfront %v", up)
	}
}

func TestTameFoodChannelFallsBackToMeatForAMale(t *testing.T) {
	t.Parallel()
	cow := acquireCow()
	cow.AdultFeedPerDay = domain.Known(0.2)
	in := acquireInput(cow)
	in.Wild = domain.Known([]UpkeepAnimal{wildYoung("Cow", "Male", 0)})
	got := TameFoodChannels(in)
	if len(got) != 1 {
		t.Fatalf("channels %v", got)
	}
	// 100 meat x 0.05 = 5 nutrition less 10 days of 0.2 feed until adult.
	if stock, _ := got[0].Nutrition().StockCap.Value(); stock != 3 {
		t.Errorf("meat stock %v", stock)
	}
	if _, ok := got[0].Nutrition().PerDay.Value(); ok {
		t.Errorf("a rate for a one-shot meat stock: %v", got[0].Nutrition().PerDay)
	}
	if lead, _ := got[0].LeadDays.Value(); lead != 10 {
		t.Errorf("lead %v, want until adult", lead)
	}
}

func TestTameFoodChannelRefusals(t *testing.T) {
	t.Parallel()
	base := func() AnimalAcquisition {
		in := acquireInput(acquireCow())
		in.Wild = domain.Known([]UpkeepAnimal{wildYoung("Cow", "Female", 0.5)})
		return in
	}
	if len(TameFoodChannels(base())) != 1 {
		t.Fatal("baseline offers nothing")
	}
	for name, mutate := range map[string]func(*AnimalAcquisition){
		"no handler": func(in *AnimalAcquisition) { in.Handlers = handlerAt(0) },
		"feed short": func(in *AnimalAcquisition) { in.Herd.FeedShort = true },
		"retired":    func(in *AnimalAcquisition) { in.Herd.Retired = map[Resource]bool{"Cow": true} },
		"at the ceiling": func(in *AnimalAcquisition) {
			in.Herd.PopulationMax = map[Resource]int64{"Cow": 1}
			in.Owned = domain.Known([]UpkeepAnimal{{ID: "own-Cow", Definition: "Cow", Release: domain.Known(false)}})
		},
		"unread race feed": func(in *AnimalAcquisition) {
			cow := acquireCow()
			cow.AdultFeedPerDay = domain.Unknown[float64]()
			in.Races = raceCatalog(cow)
			in.Races.Interaction = AnimalInteraction{TalkTicks: domain.Known(270), FeedTicks: domain.Known(270), Feeds: domain.Known(2)}
		},
		"unread talk time": func(in *AnimalAcquisition) { in.Races.Interaction.TalkTicks = domain.Unknown[int]() },
		"unread census":    func(in *AnimalAcquisition) { in.Wild = domain.Unknown[[]UpkeepAnimal]() },
	} {
		in := base()
		mutate(&in)
		if got := TameFoodChannels(in); len(got) != 0 {
			t.Errorf("%s: %v", name, got)
		}
	}
}

// TestAcquisitionPricesFeedWithNoOwnedAnimal (#2240): the race's own feed
// prices a first tame and a first purchase for a colony that owns none.
func TestAcquisitionPricesFeedWithNoOwnedAnimal(t *testing.T) {
	t.Parallel()
	in := acquireInput(acquireCow())
	in.Owned = domain.Known([]UpkeepAnimal{})
	in.Wild = domain.Known([]UpkeepAnimal{wildYoung("Cow", "Female", 0.5)})
	tame := TameFoodChannels(in)
	if len(tame) != 1 || tame[0].Kind != CandidateTame {
		t.Fatalf("tame channels %v", tame)
	}
	if n, _ := tame[0].Nutrition().PerDay.Value(); n != 3 {
		t.Errorf("net rate %v, want 5 milk - 2 feed", n)
	}
	buy := AnimalPurchaseFoodChannels([]TradeOffers{{Trader: "caravan", Rows: []TradeOffer{{Def: "Cow", Count: 1, Price: 50, Pawn: true, Gender: "Female"}}}}, in, 1000, 200)
	if len(buy) != 1 || buy[0].Kind != CandidateAnimalBuy {
		t.Fatalf("buy channels %v", buy)
	}
}

func TestAnimalPurchaseFoodChannelPricesTheCheapestUseful(t *testing.T) {
	t.Parallel()
	in := acquireInput(acquireCow())
	offers := []TradeOffers{{Trader: "caravan", Rows: []TradeOffer{
		{Def: "Cow", Count: 1, Price: 300, Pawn: true, Gender: "Female"},
		{Def: "Cow", Count: 1, Price: 100, Pawn: true, Gender: "Female"},
		{Def: "Cow", Count: 1, Price: 50, Pawn: true, Gender: "Male"},
		{Def: "Silver", Count: 1, Price: 1},
	}}}
	got := AnimalPurchaseFoodChannels(offers, in, 1000, 200)
	if len(got) != 2 || got[0].Kind != CandidateAnimalBuy {
		t.Fatalf("channels %v", got)
	}
	if got[0].ID != "caravan/Cow/Female" {
		got[0], got[1] = got[1], got[0]
	}
	if got[0].ID != "caravan/Cow/Female" || got[1].ID != "caravan/Cow/Male" {
		t.Fatalf("channels %v", got)
	}
	if stock, _ := got[1].Nutrition().StockCap.Value(); stock != 5 {
		t.Errorf("a bought adult male is five nutrition of meat, got %v", stock)
	}
	if n, _ := got[0].Nutrition().PerDay.Value(); n != 3 {
		t.Errorf("net rate %v", n)
	}
	if lead, _ := got[0].LeadDays.Value(); lead != 0 {
		t.Errorf("an adult trader's animal leads %v", lead)
	}
	if up, _ := got[0].UpfrontCost.LaborTicks.Value(); up != 100*tradeLaborPerSilver {
		t.Errorf("upfront %v", up)
	}
	if got := AnimalPurchaseFoodChannels(offers, in, 250, 200); len(got) != 1 || got[0].ID != "caravan/Cow/Male" {
		t.Errorf("unaffordable above the reserve: %v", got)
	}
}

func TestOpenedAcquisitionsReachTheirExecutors(t *testing.T) {
	t.Parallel()
	in := acquireInput(acquireCow())
	wild := []UpkeepAnimal{wildYoung("Cow", "Female", 0)}
	in.Wild = domain.Known(wild)
	rows := TameFoodChannels(in)
	rows = append(rows, AnimalPurchaseFoodChannels([]TradeOffers{{Trader: "caravan", Rows: []TradeOffer{{Def: "Cow", Count: 1, Price: 50, Pawn: true, Gender: "Female"}}}}, in, 1000, 200)...)
	plan, err := SupplyFoodPlan(foodPlanRequest(rows...))
	if err != nil {
		t.Fatal(err)
	}
	fact := domain.Known(plan)
	choice := FoodTameChoice(fact, in.Wild, domain.Known(false), in.Handlers)
	buy := PlannedAnimalPurchases(fact, "caravan")
	if choice.Method != domain.HusbandryTame && len(buy) == 0 {
		t.Fatalf("the plan opened neither acquisition: %s", plan.Explain())
	}
	if choice.Method == domain.HusbandryTame && choice.Animal != "wild-Cow" {
		t.Errorf("tame choice %v", choice)
	}
	for _, w := range buy {
		if w.Race != "Cow" || !w.Female || w.Male {
			t.Errorf("want %v", w)
		}
	}
	if FoodTameChoice(fact, in.Wild, domain.Known(true), in.Handlers).Reason != HusbandryNoDeficit {
		t.Error("a short feed forecast still tames")
	}
	if got := PlannedAnimalPurchases(fact, "other"); len(got) != 0 {
		t.Errorf("another trader's wants %v", got)
	}
}

// DaysPerYearF is the game's days per year for an age given in days.
const DaysPerYearF = float64(domain.DaysPerYear)
