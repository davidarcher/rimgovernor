package bridge

import (
	"testing"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// racesReply is a catalog with a wolf (predator), an alphabeaver (a tree
// eater), a lancer (a mechanoid) and a human, their game-computed race facts
// and the stat rows an animal race reads.
func racesReply() *o.DefinitionCatalog {
	v := catalogReply(authorityTestContext(7)).GetObserved()
	v.ThingDefs = []*d.ThingDef{
		{DefName: "Wolf", Race: &d.RaceProperties{Predator: true, BaseBodySize: 0.85, LifeExpectancy: 14, Trainability: "Intermediate", ManhunterOnDamageChance: 0.5}},
		{DefName: "Alphabeaver", Race: &d.RaceProperties{BaseBodySize: 0.6, FoodType: d.FoodTypeFlags_FOOD_TYPE_FLAGS_TREE}},
		{DefName: "Mech_Lancer", Race: &d.RaceProperties{BaseBodySize: 1}},
		{DefName: "Human", Race: &d.RaceProperties{BaseBodySize: 1}},
	}
	v.StatValues = &o.DefStatTable{
		Stats: []string{StatCarryingCapacity, StatWildness, StatMarketValue, StatMinimumHandlingSkill},
		Rows: []*o.DefStatRow{
			{DefName: "Wolf", Stat: []int32{0, 1, 2, 3}, Value: []float32{40, 0.8, 250, 5}},
			{DefName: "Alphabeaver"}, {DefName: "Mech_Lancer"}, {DefName: "Human"},
		},
	}
	v.ThingFacts = []*o.ThingDefFacts{
		{DefName: "Wolf", Race: &o.RaceFacts{Animal: true, Trainables: []string{"Obedience"}}},
		{DefName: "Alphabeaver", Race: &o.RaceFacts{Animal: true}},
		{DefName: "Mech_Lancer", Race: &o.RaceFacts{Mechanoid: true}},
		{DefName: "Human", Race: &o.RaceFacts{}},
	}
	return v
}

// TestCatalogAnimalRaces (#1722): the animal races derive from the catalog's
// race rows, the stat table and the game's race facts; only animals get a
// race, a pest is a tree eater, and the flag lookup reads the facts.
func TestCatalogAnimalRaces(t *testing.T) {
	catalog, err := DecodeDefinitionCatalog(racesReply(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	races, err := catalog.AnimalRaces()
	if err != nil {
		t.Fatal(err)
	}
	if len(races.Races) != 2 {
		t.Fatalf("races %v, want the wolf and the alphabeaver only", races.Races)
	}
	wolf, ok := races.Race("Wolf")
	if !ok || !wolf.Predator || wolf.Pest {
		t.Fatalf("wolf %+v", wolf)
	}
	if v, ok := wolf.BodySize.Value(); !ok || v != float64(float32(0.85)) {
		t.Fatalf("wolf body size %v %v", v, ok)
	}
	if v, ok := wolf.MinimumHandlingSkill.Value(); !ok || v != 5 {
		t.Fatalf("wolf minimum handling skill %v %v", v, ok)
	}
	if v, ok := wolf.CarryingCapacity.Value(); !ok || v != 40 {
		t.Fatalf("wolf carrying capacity %v %v", v, ok)
	}
	if beaver, ok := races.Race("Alphabeaver"); !ok || !beaver.Pest {
		t.Fatalf("alphabeaver %+v", beaver)
	}
	if _, ok := races.Race("Mech_Lancer"); ok {
		t.Fatal("a mechanoid is no animal race")
	}
	if animal, mech, insect := catalog.RaceFlags("Mech_Lancer"); animal || !mech || insect {
		t.Fatal("lancer flags", animal, mech, insect)
	}
	if animal, mech, _ := catalog.RaceFlags("Wolf"); !animal || mech {
		t.Fatal("wolf flags", animal, mech)
	}
	var none *DefinitionCatalog
	if empty, err := none.AnimalRaces(); err != nil || len(empty.Races) != 0 {
		t.Fatal("a nil catalog has no races", empty, err)
	}
}

// TestCatalogAnimalRacesFailLoudly (#1722): race facts on a def without race
// properties and an animal without a stat table are errors, never an empty
// or defaulted race.
func TestCatalogAnimalRacesFailLoudly(t *testing.T) {
	noRow := racesReply()
	noRow.ThingDefs[0].Race = nil
	catalog, err := DecodeDefinitionCatalog(noRow, pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := catalog.AnimalRaces(); err == nil {
		t.Fatal("race facts without race properties accepted")
	}
	noStats := racesReply()
	noStats.StatValues = nil
	if catalog, err := DecodeDefinitionCatalog(noStats, pbIdentity()); err == nil {
		if _, err := catalog.AnimalRaces(); err == nil {
			t.Fatal("an animal without a stat table accepted")
		}
	}
}

// TestCatalogAnimalRaceHusbandryFacts (#2238): the game-computed husbandry
// facts of a race and the animal interaction constants reach the policy race
// catalog; an absent life stage and a race with no meat stay unknown.
func TestCatalogAnimalRaceHusbandryFacts(t *testing.T) {
	reply := racesReply()
	reply.Constants.AnimalInteractTalkTicks, reply.Constants.AnimalInteractFeedTicks, reply.Constants.AnimalInteractFeeds = 270, 270, 2
	reply.Constants.AnimalFeedNutritionFraction, reply.Constants.AnimalFeedNutritionCap, reply.Constants.MinTrainIntervalTicks = 0.15, 0.3, 15000
	wolf := reply.ThingFacts[0].Race
	reproductive, milkable := int64(2700000), int64(3600000)
	wolf.AdultMinAgeTicks, wolf.ReproductiveMinAgeTicks, wolf.MilkableMinAgeTicks = 7200000, &reproductive, &milkable
	wolf.TamenessCanDecay, wolf.TamenessDecayPeriodTicks, wolf.TameChanceFactor = true, 450000, 0.25
	wolf.MeatDef, wolf.MeatAmount = "Meat_Wolf", 70
	catalog, err := DecodeDefinitionCatalog(reply, pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	races, err := catalog.AnimalRaces()
	if err != nil {
		t.Fatal(err)
	}
	race, _ := races.Race("Wolf")
	if v, ok := race.AdultMinAgeTicks.Value(); !ok || v != 7200000 {
		t.Fatalf("adult age %v %v", v, ok)
	}
	if v, ok := race.ReproductiveMinAgeTicks.Value(); !ok || v != reproductive {
		t.Fatalf("reproductive age %v %v", v, ok)
	}
	if v, ok := race.MilkableMinAgeTicks.Value(); !ok || v != milkable {
		t.Fatalf("milkable age %v %v", v, ok)
	}
	if _, ok := race.ShearableMinAgeTicks.Value(); ok {
		t.Fatal("a race with no shearable stage has no shearable age")
	}
	if v, ok := race.TamenessCanDecay.Value(); !ok || !v {
		t.Fatalf("tameness decay %v %v", v, ok)
	}
	if v, ok := race.TamenessDecayPeriodTicks.Value(); !ok || v != 450000 {
		t.Fatalf("decay period %v %v", v, ok)
	}
	if v, ok := race.TameChanceFactor.Value(); !ok || v != 0.25 {
		t.Fatalf("tame chance factor %v %v", v, ok)
	}
	if v, ok := race.MeatAmount.Value(); !ok || v != 70 || race.MeatDef != "Meat_Wolf" {
		t.Fatalf("meat %q %v %v", race.MeatDef, v, ok)
	}
	beaver, _ := races.Race("Alphabeaver")
	if _, ok := beaver.MeatAmount.Value(); ok || beaver.MeatDef != "" {
		t.Fatalf("a race with no meat has none: %+v", beaver)
	}
	in := races.Interaction
	if v, _ := in.TalkTicks.Value(); v != 270 {
		t.Fatalf("talk ticks %v", v)
	}
	if v, _ := in.Feeds.Value(); v != 2 {
		t.Fatalf("feeds %v", v)
	}
	if v, ok := in.FeedNutritionCap.Value(); !ok || v != float64(float32(0.3)) {
		t.Fatalf("feed cap %v %v", v, ok)
	}
	if v, _ := in.MinTrainIntervalTicks.Value(); v != 15000 {
		t.Fatalf("train interval %v", v)
	}
}
