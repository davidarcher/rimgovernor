package policy

import "testing"

func haulGraphInput(colonists int, haul HaulModel, stock map[Resource]int64) StationGraphInput {
	in := graphTestInput()
	in.Population.Colonists = colonists
	in.Catalog.Haul = haul
	in.Stock = stock
	in.StackLimit = map[Resource]int64{"Steel": 75, "Silver": 100, "WoodLog": 75, "Bioferrite": 75}
	return in
}

// haulLoads is the store's loads a day to the outside node.
func haulLoads(g StationGraph) float64 { return g.Weight("Shelf/store", OutsideNode) }

// A miner's day is CookTicksPerDay of work: 20000 ticks over a steel tile's
// 1900 ticks (19 hits) is 10.5 tiles of 40 steel, in stacks of 75.
func TestMinedGoodsAreHauledInStacksFromTheOutside(t *testing.T) {
	steel := HaulSource{Good: "Steel", Units: 40, Work: 1900}
	g, err := BuildStationGraph(haulGraphInput(2, HaulModel{Mined: []HaulSource{steel}}, map[Resource]int64{"Steel": 90}))
	if err != nil {
		t.Fatal(err)
	}
	if want := CookTicksPerDay / 1900 * 40 / 75; !near(haulLoads(g), want) {
		t.Errorf("loads %v, want %v", haulLoads(g), want)
	}
	for _, e := range g.Edges {
		if e.A == "Shelf/store" && e.B == OutsideNode && e.Reason != ReasonFlowHaulMined {
			t.Errorf("edge %+v", e)
		}
	}
	// The miners are the table's, not the colony's size.
	g8, _ := BuildStationGraph(haulGraphInput(8, HaulModel{Mined: []HaulSource{steel}}, map[Resource]int64{"Steel": 90}))
	if !near(haulLoads(g8), haulLoads(g)) {
		t.Errorf("eight colonists haul %v, two haul %v", haulLoads(g8), haulLoads(g))
	}
}

// No stock demand for the good, an unread source or no colonist adds no flow;
// two wanted goods share the owner's ticks.
func TestHaulsOnlyHaulWhatIsWantedAndRead(t *testing.T) {
	steel := HaulSource{Good: "Steel", Units: 40, Work: 1900}
	silver := HaulSource{Good: "Silver", Units: 40, Work: 1900}
	for name, in := range map[string]StationGraphInput{
		"no demand":    haulGraphInput(2, HaulModel{Mined: []HaulSource{steel}}, map[Resource]int64{"Silver": 5}),
		"no work read": haulGraphInput(2, HaulModel{Mined: []HaulSource{{Good: "Steel", Units: 40}}}, map[Resource]int64{"Steel": 90}),
		"no yield":     haulGraphInput(2, HaulModel{Mined: []HaulSource{{Good: "Steel", Work: 1900}}}, map[Resource]int64{"Steel": 90}),
		"no model":     haulGraphInput(2, HaulModel{}, map[Resource]int64{"Steel": 90}),
		"no colonists": haulGraphInput(0, HaulModel{Mined: []HaulSource{steel}}, map[Resource]int64{"Steel": 90}),
	} {
		g, err := BuildStationGraph(in)
		if err != nil {
			t.Fatal(err)
		}
		if haulLoads(g) != 0 {
			t.Errorf("%s: loads %v", name, haulLoads(g))
		}
	}
	both, err := BuildStationGraph(haulGraphInput(2, HaulModel{Mined: []HaulSource{steel, silver}}, map[Resource]int64{"Steel": 90, "Silver": 5}))
	if err != nil {
		t.Fatal(err)
	}
	// Silver's stack is 100: half the ticks to each, a stack a load.
	if want := CookTicksPerDay/2/1900*40/75 + CookTicksPerDay/2/1900*40/100; !near(haulLoads(both), want) {
		t.Errorf("loads %v, want %v", haulLoads(both), want)
	}
}

// The woodcutters are the PlantCutting owners the work assignment wants.
func TestWoodIsHauledByThePlantCuttingOwners(t *testing.T) {
	wood := HaulSource{Good: "WoodLog", Units: 27, Work: 800}
	g, err := BuildStationGraph(haulGraphInput(5, HaulModel{Chopped: []HaulSource{wood}}, map[Resource]int64{"WoodLog": 200}))
	if err != nil {
		t.Fatal(err)
	}
	owners := float64(baselineDemand(WorkPlantCutting, 5, WorkDemand{}))
	if owners < 1 {
		t.Fatalf("the baseline holds no plant cutter: %v", owners)
	}
	if want := owners * CookTicksPerDay / 800 * 27 / 75; !near(haulLoads(g), want) {
		t.Errorf("loads %v, want %v", haulLoads(g), want)
	}
}

// A kill is a load to the store that holds a corpse, and only a colony whose
// orders butcher hunts; four colonists field two hunters (baselineDemand).
func TestHuntKillsAreLoadsWhereTheOrdersButcher(t *testing.T) {
	build := func(colonists int, butcher bool) StationGraph {
		in := haulGraphInput(colonists, HaulModel{}, map[Resource]int64{"Steel": 90})
		if butcher {
			in.Recipes["Butcher"] = RecipeFlow{Ingredients: [][]Amount{{{Resource: "Corpse", Count: 1}}}, Butchery: true}
			in.Orders = []OrderSpec{{Recipe: "Butcher", BenchKind: "Bench", Target: 1}}
			in.Catalog.Stations["Bench"] = Station{Def: "Bench", Capacity: StationCapacity{Kind: CapacityWorkers, Slots: 1, Recipes: []RecipeWorkAmount{{"MakeThing", 30000}, {"Butcher", 6000}}}}
		}
		g, err := BuildStationGraph(in)
		if err != nil {
			t.Fatal(err)
		}
		return g
	}
	if got := haulLoads(build(4, false)); got != 0 {
		t.Errorf("a colony that butchers nothing hauls %v kills", got)
	}
	three, four := haulLoads(build(3, true)), haulLoads(build(4, true))
	if want := float64(baselineDemand(WorkHunting, 3, WorkDemand{})) * HuntKillsPerHunterDay; !near(three, want) || three != 1 {
		t.Errorf("three colonists haul %v kills, want %v", three, want)
	}
	if four != 2 {
		t.Errorf("four colonists haul %v kills, want two hunters' 2", four)
	}
}
