package bridge

import (
	"slices"
	"testing"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// racesReply is a catalog with a wolf (predator), an alphabeaver (a tree
// eater), a lancer (a mechanoid) and a human, their game-computed race facts
// and the stat rows an animal race reads.
func racesReply(t testing.TB) *o.DefinitionCatalog {
	v := catalogReply(authorityTestContext(7)).GetObserved()
	pawn := d.ThingCategory_THING_CATEGORY_PAWN
	v.ThingDefs = []*d.ThingDef{
		{DefName: "Wolf", Category: pawn, Race: &d.RaceProperties{Predator: true, BaseBodySize: 0.85, LifeExpectancy: 14, Trainability: "Intermediate", ManhunterOnDamageChance: 0.5}},
		{DefName: "Alphabeaver", Category: pawn, Race: &d.RaceProperties{BaseBodySize: 0.6, FoodType: d.FoodTypeFlags_FOOD_TYPE_FLAGS_TREE}},
		{DefName: "Mech_Lancer", Category: pawn, Race: &d.RaceProperties{BaseBodySize: 1, FleshType: "Mechanoid"}},
		{DefName: "Human", Category: pawn, Race: &d.RaceProperties{BaseBodySize: 1, Intelligence: d.Intelligence_INTELLIGENCE_HUMANLIKE}},
		// A corpse def shares its pawn's race properties and is no race.
		{DefName: "Corpse_Wolf", Race: &d.RaceProperties{BaseBodySize: 0.85}},
	}
	v.Defs = &d.DefSets{
		FleshTypeDefs:    []*d.FleshTypeDef{{DefName: "Normal", IsOrganic: true}, {DefName: "Mechanoid"}},
		TrainabilityDefs: []*d.TrainabilityDef{{DefName: "Intermediate", IntelligenceOrder: 2}, {DefName: "Advanced", IntelligenceOrder: 3}},
		TrainableDefs: []*d.TrainableDef{{DefName: "Obedience", RequiredTrainability: "Intermediate"}, {DefName: "Release", RequiredTrainability: "Advanced"},
			{DefName: "Haul", RequiredTrainability: "Intermediate", MinBodySize: 2}},
	}
	setStats(v.ThingDefs[0], StatCarryingCapacity, 40, StatWildness, 0.8, StatMarketValue, 250, StatMinimumHandlingSkill, 5)
	withStatSupport(t, v)
	v.ThingFacts = []*o.ThingDefFacts{
		{DefName: "Wolf", Race: &o.RaceFacts{}}, {DefName: "Alphabeaver", Race: &o.RaceFacts{}}, {DefName: "Mech_Lancer", Race: &o.RaceFacts{}},
		{DefName: "Human", Race: &o.RaceFacts{}}, {DefName: "Corpse_Wolf", Race: &o.RaceFacts{}},
	}
	return v
}

// TestCatalogAnimalRaces: the animal races derive from the catalog's
// race rows, the stat table and the game's race facts; only animals get a
// race, a pest is a tree eater, and the flag lookup reads the facts.
func TestCatalogAnimalRaces(t *testing.T) {
	catalog, err := DecodeDefinitionCatalog(racesReply(t), pbIdentity())
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
	if v, ok := wolf.MinimumHandlingSkill.Value(); !ok || v != 8 {
		t.Fatalf("wolf minimum handling skill %v %v", v, ok)
	}
	if v, ok := wolf.CarryingCapacity.Value(); !ok || v != 34 {
		t.Fatalf("wolf carrying capacity %v %v", v, ok)
	}
	if !slices.Equal(wolf.Trainables, []string{"Obedience"}) {
		t.Fatalf("wolf trainables %v, want Obedience only (Release needs a higher rank, Haul a larger body)", wolf.Trainables)
	}
	if beaver, ok := races.Race("Alphabeaver"); !ok || !beaver.Pest {
		t.Fatalf("alphabeaver %+v", beaver)
	}
	if _, ok := races.Race("Mech_Lancer"); ok {
		t.Fatal("a mechanoid is no animal race")
	}
	if mech, insect := catalog.RaceFlags("Mech_Lancer"); !mech || insect {
		t.Fatal("lancer flags", mech, insect)
	}
	if mech, _ := catalog.RaceFlags("Wolf"); mech {
		t.Fatal("wolf flags", mech)
	}
	var none *DefinitionCatalog
	if empty, err := none.AnimalRaces(); err != nil || len(empty.Races) != 0 {
		t.Fatal("a nil catalog has no races", empty, err)
	}
}

// TestCatalogAnimalRacesFailLoudly: race facts on a def without race
// properties and an animal without a stat table are errors, never an empty
// or defaulted race.
func TestCatalogAnimalRacesFailLoudly(t *testing.T) {
	noRow := racesReply(t)
	noRow.ThingDefs[0].Race = nil
	catalog, err := DecodeDefinitionCatalog(noRow, pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := catalog.AnimalRaces(); err == nil {
		t.Fatal("race facts without race properties accepted")
	}
	noStats := racesReply(t)
	noStats.StatEnv = nil
	if catalog, err := DecodeDefinitionCatalog(noStats, pbIdentity()); err == nil {
		if _, err := catalog.AnimalRaces(); err == nil {
			t.Fatal("an animal without a stat table accepted")
		}
	}
}

// TestCatalogAnimalRaceHusbandryFacts: the game-computed husbandry
// facts of a race and the animal interaction constants reach the policy race
// catalog; an absent life stage and a race with no meat stay unknown.
func TestCatalogAnimalRaceHusbandryFacts(t *testing.T) {
	reply := racesReply(t)
	animal, train := reply.GameConstants.JobDriver_InteractAnimal, reply.GameConstants.TrainableUtility
	animal.TalkDuration, animal.FeedDuration, animal.FeedCount = 270, 270, 2
	animal.NutritionPercentagePerFeed, animal.MaxMinNutritionPerFeed, train.MinTrainInterval = 0.15, 0.3, 15000
	wolf := reply.ThingFacts[0].Race
	reproductive, milkable := int64(2700000), int64(3600000)
	wolf.TamenessCanDecay, wolf.TamenessDecayPeriodTicks, wolf.TameChanceFactor = true, 450000, 0.25
	stage := func(name string, minAge float32) *d.Opt_LifeStageAge {
		return &d.Opt_LifeStageAge{Value: &d.LifeStageAge{Def: name, MinAge: minAge}}
	}
	reply.ThingDefs[0].Race.LifeStageAges = []*d.Opt_LifeStageAge{stage("Baby", 0), stage("Juvenile", 0.75), stage("Mid", 1), stage("Adult", 2)}
	reply.Defs.LifeStageDefs = []*d.LifeStageDef{{DefName: "Baby"}, {DefName: "Juvenile", Reproductive: true}, {DefName: "Mid", Reproductive: true, Milkable: true}, {DefName: "Adult"}}
	feed := 1.25
	wolf.AdultFeedPerDay = &feed
	reply.ThingDefs = append(reply.ThingDefs, &d.ThingDef{DefName: "Meat_Wolf"})
	setStats(reply.ThingDefs[0], StatCarryingCapacity, 40, StatWildness, 0.8, StatMarketValue, 250, StatMinimumHandlingSkill, 5, StatMeatAmount, 70)
	setStats(reply.ThingDefs[len(reply.ThingDefs)-1], StatNutrition, 0.05)
	reply.ThingDefs[0].Race.HasMeat, reply.ThingDefs[0].Race.HasCorpse = true, true
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
	if v, ok := race.MeatAmount.Value(); !ok || v != 59.5 || race.MeatDef != "Meat_Wolf" {
		t.Fatalf("meat %q %v %v", race.MeatDef, v, ok)
	}
	if v, ok := race.MeatNutritionPerUnit.Value(); !ok || float32(v) != 0.05 {
		t.Fatalf("meat nutrition %v %v", v, ok)
	}
	if v, ok := race.AdultFeedPerDay.Value(); !ok || v != 1.25 {
		t.Fatalf("adult feed %v %v", v, ok)
	}
	beaver, _ := races.Race("Alphabeaver")
	if _, ok := beaver.AdultFeedPerDay.Value(); ok {
		t.Fatal("a race with no feed fact has none")
	}
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

// TestCatalogAnimalRaceHerdGrowth: a race carries its life stages with
// the tick each begins and its hunger rate factor, and the mean litter of its
// litterSizeCurve as Rand.ByCurveAverage reads it.
func TestCatalogAnimalRaceHerdGrowth(t *testing.T) {
	reply := racesReply(t)
	point := func(x, y float32) *d.CurvePoint { return &d.CurvePoint{Loc: &d.Vector2{X: x, Y: y}} }
	wolf := reply.ThingDefs[0].Race
	wolf.LifeStageAges = []*d.Opt_LifeStageAge{{Value: &d.LifeStageAge{Def: "WolfBaby", MinAge: 0}}, {Value: &d.LifeStageAge{Def: "WolfAdult", MinAge: 0.5}}}
	wolf.LitterSizeCurve = &d.SimpleCurve{Points: []*d.CurvePoint{point(1, 0), point(2, 1), point(3, 0)}}
	reply.Defs.LifeStageDefs = []*d.LifeStageDef{{DefName: "WolfBaby", HungerRateFactor: 0.25}, {DefName: "WolfAdult", HungerRateFactor: 1}}
	catalog, err := DecodeDefinitionCatalog(reply, pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	races, err := catalog.AnimalRaces()
	if err != nil {
		t.Fatal(err)
	}
	race, _ := races.Race("Wolf")
	if len(race.LifeStages) != 2 || race.LifeStages[1].MinAgeTicks != 1800000 || race.LifeStages[0].HungerRateFactor != 0.25 {
		t.Fatalf("stages %+v", race.LifeStages)
	}
	if v, ok := race.LitterSize.Value(); !ok || v != 2 {
		t.Fatalf("litter %v %v", v, ok)
	}
	if beaver, _ := races.Race("Alphabeaver"); beaver.LifeStages != nil {
		t.Fatalf("stages without defs %+v", beaver.LifeStages)
	} else if v, ok := beaver.LitterSize.Value(); !ok || v != 1 {
		t.Fatalf("no curve is one child, %v %v", v, ok)
	}
	for name, curve := range map[string]*d.SimpleCurve{
		"two points":   {Points: []*d.CurvePoint{point(1, 0), point(2, 0)}},
		"open end":     {Points: []*d.CurvePoint{point(1, 0), point(2, 1), point(3, 1)}},
		"no area":      {Points: []*d.CurvePoint{point(1, 0), point(1, 0), point(1, 0)}},
		"negative row": {Points: []*d.CurvePoint{point(1, 0), point(2, -1), point(3, 0)}},
	} {
		if _, ok := litterSizeMean(curve).Value(); ok {
			t.Fatal(name, "rolled")
		}
	}
	if v, _ := litterSizeMean(&d.SimpleCurve{Points: []*d.CurvePoint{point(0, 0), point(0.5, 1), point(1, 0)}}).Value(); v != 1 {
		t.Fatalf("a birth gives a child, %v", v)
	}
}
