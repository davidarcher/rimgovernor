package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func penTestRace() AnimalRace {
	return AnimalRace{
		Def:             "Cow",
		AdultFeedPerDay: domain.Known(2.0),
		Products: []RaceProduct{
			{Kind: "milk", Def: "Milk", Amount: domain.Known(12.0), IntervalDays: domain.Known(1.0)},
			{Kind: "wool", Def: "WoolCow", Amount: domain.Known(30.0), IntervalDays: domain.Known(10.0)},
		},
		FeedItems: []RaceFeedItem{{Def: "Kibble", Nutrition: 0.05}, {Def: "Pellet", Nutrition: 0.2}},
	}
}

func penGraphInput(animals int) StationGraphInput {
	in := graphTestInput()
	in.Population.Herds = []StationHerd{{Race: "Cow", Animals: animals}}
	in.Races = AnimalRaceCatalog{Races: map[Resource]AnimalRace{"Cow": penTestRace()}}
	in.Stock = map[Resource]int64{"Steel": 90}
	return in
}

// A pen hauls what its herd yields: animals times Amount over IntervalDays,
// items a day, to the store that holds the product. The flow scales with the
// herd and with the product's frequency.
func TestPenVisitsScaleWithHerdAndProductInterval(t *testing.T) {
	g, err := BuildStationGraph(penGraphInput(3))
	if err != nil {
		t.Fatal(err)
	}
	pen, ok := g.Pen("pen/Cow")
	if !ok || len(g.Pens) != 1 || pen.Animals != 3 || pen.Reason != ReasonPenHerd {
		t.Fatalf("pens %+v", g.Pens)
	}
	if got := g.Weight("pen/Cow", "Shelf/store"); got != 3*12.0/1+3*30.0/10 {
		t.Errorf("milk and wool of three cows %v, want 45 items a day", got)
	}
	g6, _ := BuildStationGraph(penGraphInput(6))
	if g6.Weight("pen/Cow", "Shelf/store") != 2*g.Weight("pen/Cow", "Shelf/store") {
		t.Error("twice the herd is not twice the hauls")
	}
	in := penGraphInput(3)
	cow := penTestRace()
	cow.Products = []RaceProduct{{Kind: "milk", Def: "Milk", Amount: domain.Known(12.0), IntervalDays: domain.Known(0.5)}}
	in.Races.Races["Cow"] = cow
	fast, _ := BuildStationGraph(in)
	if got := fast.Weight("pen/Cow", "Shelf/store"); got != 72 {
		t.Errorf("a product every half day doubles the milk: %v", got)
	}
}

// A product with no job in the herd plan, or an unread amount or interval, is
// no flow; a race the catalog lacks gets no pen.
func TestPenFlowsSkipWhatIsUnknown(t *testing.T) {
	in := penGraphInput(2)
	cow := penTestRace()
	cow.Products = []RaceProduct{
		{Kind: "milk", Def: "Milk", Amount: domain.Unknown[float64](), IntervalDays: domain.Known(1.0)},
		{Kind: "wool", Def: "WoolCow", Amount: domain.Known(30.0), IntervalDays: domain.Unknown[float64]()},
		{Kind: "spawner", Def: "Gold", Amount: domain.Known(1.0), IntervalDays: domain.Known(1.0)},
	}
	in.Races.Races["Cow"] = cow
	in.Population.Herds = append(in.Population.Herds, StationHerd{Race: "Ghost", Animals: 4}, StationHerd{Race: "Cow", Animals: 0})
	g, err := BuildStationGraph(in)
	if err != nil {
		t.Fatal(err)
	}
	if len(g.Pens) != 1 || g.Weight("pen/Cow", "Shelf/store") != 0 {
		t.Errorf("pens %+v, edges %+v", g.Pens, g.Edges)
	}
}

// Milk rots: it goes to the cold store when the graph holds one.
func TestPenProductsGoToTheStoreThatKeepsThem(t *testing.T) {
	in := penGraphInput(2)
	shelf := in.Catalog.Stations["Shelf"]
	shelf.Capacity.Slots = 8 // the denser shelf is the plain store
	in.Catalog.Stations["Shelf"] = shelf
	cold := shelf
	cold.Def, cold.Capacity.Slots, cold.Needs = "Cooler", 4, []EnvNeed{{Kind: NeedCold, Max: 5}}
	in.Catalog.Stations["Cooler"] = cold
	in.Stock["Meat"], in.StackLimit["Meat"] = 10, 75
	in.Perishable = map[Resource]bool{"Meat": true, "Milk": true}
	g, err := BuildStationGraph(in)
	if err != nil {
		t.Fatal(err)
	}
	var cool string
	for _, n := range g.Nodes {
		if n.Purpose == PurposeStoreCold {
			cool = n.ID
		}
	}
	if cool == "" || g.Weight("pen/Cow", cool) != 24 || g.Weight("pen/Cow", "Shelf/store") != 6 {
		t.Errorf("milk to %q %v, wool to the shelf %v", cool, g.Weight("pen/Cow", cool), g.Weight("pen/Cow", "Shelf/store"))
	}
}

// The pen is sized to graze its herd; what its cells do not grow is hauled in the
// richest feed item. Ground that grows nothing hand-feeds the whole herd; an
// unread pasture sizes nothing.
func TestPenFeedingIsTheGrazingShortfall(t *testing.T) {
	in := penGraphInput(4) // eats 8 a day
	in.Pasture = domain.Known(PastureTable{"Soil": 0.5})
	g, _ := BuildStationGraph(in)
	if hand, known := g.Pens[0].HandFed.Value(); !known || hand != 0 {
		t.Errorf("sixteen cells of 0.5 cover 8 a day: %v %v", hand, known)
	}
	if hunger, _ := g.Pens[0].Hunger.Value(); hunger != 8 {
		t.Errorf("hunger %v", hunger)
	}
	feed := func(in StationGraphInput) float64 {
		g, err := BuildStationGraph(in)
		if err != nil {
			t.Fatal(err)
		}
		return g.Weight("pen/Cow", "Shelf/store") - 4*12.0 - 4*30.0/10
	}
	if feed(in) != 0 {
		t.Error("a covered pen is fed by hand")
	}
	in.Pasture = domain.Known(PastureTable{"Soil": 0.001}) // the cap grows 0.9 of 8
	if got := feed(in); got < 35.4 || got > 35.6 {
		t.Errorf("7.1 short at 0.2 nutrition an item is 35.5 pellets: %v", got)
	}
	in.Pasture = domain.Known(PastureTable{})
	if got := feed(in); got != 40 {
		t.Errorf("a pen that grows nothing is fed 8 a day: %v pellets", got)
	}
	in.Pasture = domain.Unknown[PastureTable]()
	g, _ = BuildStationGraph(in)
	if _, known := g.Pens[0].HandFed.Value(); known || feed(in) != 0 {
		t.Error("an unread pasture was guessed")
	}
	in.Pasture = domain.Known(PastureTable{})
	cow := penTestRace()
	cow.FeedItems = nil
	in.Races.Races["Cow"] = cow
	if got := feed(in); got != 0 {
		t.Errorf("a race with nothing to feed has no feed flow: %v", got)
	}
}

// A pen stands outdoors: clustering weighs the store group's trips to it as trips
// to the outside node.
func TestClusterCountsPenHaulsAsOutsideTrips(t *testing.T) {
	shelf := node("Shelf", PurposeStore, 1)
	g := StationGraph{Nodes: []StationNode{shelf}, Pens: []PenNode{{ID: "pen/Cow"}}, Edges: []StationEdge{{A: "Shelf/store", B: "pen/Cow", Weight: 45}}}
	if !g.outdoor("pen/Cow") || !g.outdoor(OutsideNode) || g.outdoor("Shelf/store") {
		t.Fatal("outdoor nodes")
	}
	c, err := ClusterStations(ClusterInput{Graph: g, Catalog: clusterCatalog(), RoofDistance: testRoof})
	if err != nil {
		t.Fatal(err)
	}
	total := 0.0
	for _, grp := range c.Groups {
		total += grp.Outside
	}
	if total != 45 {
		t.Errorf("outside trips %v, want the pen's 45 items", total)
	}
}

func TestPenItemsAndStationHerds(t *testing.T) {
	races := AnimalRaceCatalog{Races: map[Resource]AnimalRace{"Cow": penTestRace()}}
	got := PenItems([]StationHerd{{Race: "Cow", Animals: 2}, {Race: "Ghost", Animals: 1}, {Race: "Cow", Animals: 0}}, races)
	if len(got) != 4 || got[0] != "Kibble" || got[3] != "WoolCow" {
		t.Errorf("items %v", got)
	}
	plan := HerdPlan{Policy: HerdPolicy{PopulationMax: map[Resource]int64{"Cow": 14, "Alpaca": 8, "Rat": 0}}}
	herds := plan.StationHerds()
	if len(herds) != 2 || herds[0] != (StationHerd{Race: "Alpaca", Animals: 8}) || herds[1] != (StationHerd{Race: "Cow", Animals: 14}) {
		t.Errorf("herds %v", herds)
	}
}
