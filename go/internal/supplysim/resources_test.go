package supplysim

import (
	"encoding/json"
	"os"
	"testing"
)

func resWorld(workers int, srcs ...Source) World {
	return World{Workers: workers, Stock: map[Good]float64{}, Sources: srcs}
}

func TestTreesRegrowWhileClosedAndDeplete(t *testing.T) {
	rep := Run(resWorld(5, NewTrees("grove", 100, 400, 20)), nil, 10)
	near(t, "closed grove", rep.Days[9].Delivered["grove"], 0, 1e-9)
	rep = Run(resWorld(5, open(NewTrees("grove", 100, 400, 20))), nil, 40)
	// Chopping outpaces regrowth, so the grove empties and settles on the
	// regrowth rate.
	if rep.Days[3].Delivered["grove"] != 20 {
		t.Fatalf("early chop %v", rep.Days[3].Delivered["grove"])
	}
	near(t, "steady chop", rep.Days[39].Delivered["grove"], TreeRegrowFraction*400, 0.01)
}

func TestTreesRegrowTowardMax(t *testing.T) {
	w := resWorld(5, NewTrees("grove", 100, 400, 20))
	var seen float64
	Run(w, PlannerFunc(func(v WorldView) []Command {
		if v.Day == 10 {
			seen = v.Sources[0].Stock
		}
		return nil
	}), 11)
	near(t, "regrown", seen, 100+10*TreeRegrowFraction*400, 1e-9)
}

func TestWinterDemandMultiplier(t *testing.T) {
	c := Consumer{Good: Wood, PerDay: 10, Window: Window{Period: 60, From: 40, To: 60}, WindowFactor: 2}
	near(t, "autumn", c.Demand(10), 10, 1e-9)
	near(t, "winter", c.Demand(45), 20, 1e-9)
	near(t, "plain", Consumer{PerDay: 10, WindowFactor: 2}.Demand(3), 10, 1e-9)
}

func TestStonecutterTurnsChunksToBlocks(t *testing.T) {
	w := resWorld(5, open(NewQuarry("quarry", 30, 10)), open(NewStonecutter("cutter", 4)))
	rep := Run(w, nil, 6)
	near(t, "day 1 cuts", rep.Days[1].Delivered["cutter"], 4, 1e-9)
	near(t, "blocks", rep.Days[5].Stock[StoneBlocks], StoneBlocksPerChunk*rep.Delivered["cutter"], 1e-6)
	near(t, "chunk conservation", rep.Delivered["quarry"]-rep.Delivered["cutter"], rep.Days[5].Stock[StoneChunks], 1e-6)
	if NewStonecutter("c", 1).Labor <= NewQuarry("q", 1, 1).Labor {
		t.Fatal("a cut chunk costs more labor than a mined one")
	}
}

func TestBuriedVeinLead(t *testing.T) {
	p := PlannerFunc(func(v WorldView) []Command {
		if v.Day == 1 {
			return []Command{{Open, "surface"}, {Open, "buried"}}
		}
		return nil
	})
	rep := Run(resWorld(5, NewVein("surface", Steel, 50, 10, 0), NewVein("buried", Steel, 50, 10, 4)), p, 12)
	if a, b := firstDelivery(rep, "surface"), firstDelivery(rep, "buried"); a != 1 || b != 5 {
		t.Fatalf("first deliveries %d, %d; want 1 and 5 (4 day tunnel)", a, b)
	}
	near(t, "vein depleted", rep.Delivered["buried"], 50, 1e-9)
}

func TestDeepDrillDepletesAndStopsOnPowerLoss(t *testing.T) {
	w := resWorld(5, open(NewDeepDrill("drill", Plasteel, 40, 5, 200)))
	w.Power = 300
	w.Shocks = []Shock{{Day: 2, Kind: PowerLoss, Factor: 0, Days: 3}}
	rep := Run(w, nil, 14)
	for d := 0; d < 14; d++ {
		got := rep.Days[d].Delivered["drill"]
		switch {
		case d >= 2 && d < 5:
			if got != 0 {
				t.Fatalf("drill ran on day %d without power", d)
			}
		case d < 10 && got != 5:
			t.Fatalf("drill day %d delivered %v, want 5", d, got)
		}
	}
	near(t, "lump", rep.Delivered["drill"], 40, 1e-9)
	noGrid := resWorld(5, open(NewDeepDrill("drill", Plasteel, 40, 5, 200)))
	if Run(noGrid, nil, 5).Delivered["drill"] != 0 {
		t.Fatal("an unpowered drill delivered")
	}
}

func TestCaravanWindowAndAbsence(t *testing.T) {
	visit := Window{Period: 15, From: 5, To: 7}
	mk := func() World {
		w := resWorld(5, open(NewTradeOf("caravan", Steel, visit, 30, 1, 2)))
		w.Stock = map[Good]float64{Silver: 100}
		return w
	}
	rep := Run(mk(), nil, 35)
	for _, d := range []int{5, 20} {
		if rep.Days[d].Delivered["caravan"] == 0 {
			t.Fatalf("no purchase on visit day %d", d)
		}
	}
	for _, d := range []int{0, 4, 6, 12, 19} {
		if rep.Days[d].Delivered["caravan"] != 0 {
			t.Fatalf("purchase outside the window on day %d", d)
		}
	}
	near(t, "silver spent", 100-rep.Days[34].Stock[Silver], 2*rep.Delivered["caravan"], 1e-6)
	absent := mk()
	absent.Shocks = []Shock{{Day: 5, Kind: StopWork, Source: "caravan", Days: 5}}
	if a := Run(absent, nil, 35); a.Days[5].Delivered["caravan"] != 0 || a.Days[20].Delivered["caravan"] == 0 {
		t.Fatal("an absent caravan should miss only the first visit")
	}
}

func TestWindfallWaitsOutThreat(t *testing.T) {
	w := resWorld(5, open(NewLoot("wreck", Steel, 30, 10)), open(NewSalvage("hulk", Components, 6, 2)))
	w.Shocks = []Shock{{Day: 1, Kind: Threat, Days: 4}}
	var sawThreat bool
	rep := Run(w, PlannerFunc(func(v WorldView) []Command {
		if v.Day == 2 {
			sawThreat = v.Threat
		}
		return nil
	}), 12)
	if !sawThreat {
		t.Fatal("view hid the threat")
	}
	near(t, "before", rep.Days[0].Delivered["wreck"], 10, 1e-9)
	for d := 1; d < 5; d++ {
		if rep.Days[d].Delivered["wreck"] != 0 || rep.Days[d].Delivered["hulk"] != 0 {
			t.Fatalf("collected under threat on day %d", d)
		}
	}
	near(t, "one shot", rep.Delivered["wreck"], 30, 1e-9)
	near(t, "salvage", rep.Delivered["hulk"], 6, 1e-9)
	if NewSalvage("s", Steel, 1, 1).Labor <= NewLoot("l", Steel, 1, 1).Labor {
		t.Fatal("salvage is slower than loot")
	}
}

func TestLeatherCoYieldAndCotton(t *testing.T) {
	hunt := open(NewHerd("deer", 10, 5, 2, 0).WithYield(Leather, 2))
	rep := Run(resWorld(5, hunt), nil, 10)
	near(t, "leather", rep.Days[9].Stock[Leather], 2*rep.Delivered["deer"], 1e-9)
	near(t, "meat", rep.Days[9].Stock[Nutrition], 5*rep.Delivered["deer"], 1e-9)
	season := Window{Period: 60, From: 0, To: 30}
	rep = Run(resWorld(5, open(NewCropOf("cotton", Cotton, 12, 6, 3, season, 12))), nil, 20)
	if d := firstDelivery(rep, "cotton"); d != 7 {
		t.Fatalf("cotton first harvest %d, want 7", d)
	}
	near(t, "cotton", rep.Days[7].Stock[Cotton], 36, 1e-9)
}

func TestFloorLatchesAndAsks(t *testing.T) {
	w := World{Workers: 5, Stock: map[Good]float64{Wood: 130},
		Consumers: []Consumer{{Good: Wood, PerDay: 5}},
		Floors:    []Floor{{Wood, WoodMin, WoodTarget, WoodMax}},
		Sources:   []Source{NewTrees("grove", 1000, 1000, 100)}}
	var views []WorldView
	Run(w, PlannerFunc(func(v WorldView) []Command {
		views = append(views, v)
		if v.Floors[0].Latched && !v.Sources[0].Open {
			return []Command{{Open, "grove"}}
		}
		return nil
	}), 12)
	if views[0].Floors[0].Latched || views[1].Floors[0].Latched {
		t.Fatal("latched above the minimum")
	}
	// 130 - 5/day is 115 at the start of day 3, under WoodMin.
	if !views[3].Floors[0].Latched {
		t.Fatalf("not latched at %v wood: %+v", views[3].Stock[Wood], views[3].Floors[0])
	}
	near(t, "ask", views[3].Floors[0].Ask, WoodTarget-views[3].Stock[Wood], 1e-9)
	last := views[len(views)-1]
	if last.Floors[0].Latched || last.Stock[Wood] <= WoodTarget {
		t.Fatalf("latch should release above target: %+v (wood %v)", last.Floors[0], last.Stock[Wood])
	}
}

func TestBuildShortfallEdge(t *testing.T) {
	w := World{Workers: 5, Stock: map[Good]float64{Steel: 20},
		Builds:  []Build{{Name: "turret", Day: 2, Costs: []Yield{{Steel, 50}}}},
		Sources: []Source{NewVein("vein", Steel, 100, 10, 0)}}
	var open2, open9 int
	rep := Run(w, PlannerFunc(func(v WorldView) []Command {
		switch v.Day {
		case 2:
			open2 = len(v.Builds)
			return []Command{{Open, "vein"}}
		case 9:
			open9 = len(v.Builds)
		}
		return nil
	}), 10)
	if open2 != 1 || open9 != 0 {
		t.Fatalf("open builds on day 2 / 9 = %d / %d", open2, open9)
	}
	// The build draws 20 on day 2; the vein delivers 10 a day on day 2 on, so day 4.
	if got := rep.BuildDone["turret"]; got != 4 {
		t.Fatalf("turret paid on day %d, want 4", got)
	}
	if rep.Starved(Steel) {
		t.Fatal("a build shortfall is not starvation")
	}
}

func TestForecastRunwayDeficit(t *testing.T) {
	w := World{Workers: 5, Stock: map[Good]float64{Steel: 300},
		Consumers: []Consumer{{Good: Steel, PerDay: 20}},
		Floors:    []Floor{{Steel, SteelFloor, SteelFloor, 1000}},
		Sources:   []Source{NewVein("vein", Steel, 400, 10, 0)}}
	var fcs []map[Good]Forecast
	Run(w, PlannerFunc(func(v WorldView) []Command {
		fcs = append(fcs, v.Forecast)
		return nil
	}), 8)
	if fcs[0] != nil {
		t.Fatal("forecast without a day of history")
	}
	f := fcs[1][Steel]
	near(t, "rate", f.ConsumptionPerDay, 20, 1e-9)
	// 280 stock - 200 reserve + 400 unopened ore, at 20 a day.
	near(t, "days left", f.DaysLeft, (280-200+400)/20.0, 1e-9)
	if f.Deficit {
		t.Fatal("deficit with ore in reach")
	}
	idle := World{Workers: 5, Stock: map[Good]float64{Steel: 300}, Consumers: w.Consumers, Floors: w.Floors}
	var last Forecast
	Run(idle, PlannerFunc(func(v WorldView) []Command { last = v.Forecast[Steel]; return nil }), 8)
	if !last.Deficit {
		t.Fatalf("no ore and %.1f days left should be a deficit", last.DaysLeft)
	}
}

func TestResourceShocks(t *testing.T) {
	base := func() World {
		return World{Workers: 20, Stock: map[Good]float64{Wood: 200, Steel: 100},
			Consumers: []Consumer{{Good: Wood, PerDay: 10}},
			Sources:   []Source{open(NewTrees("grove", 1000, 1000, 40)), open(NewVein("vein", Steel, 200, 20, 0))}}
	}
	run := func(sh ...Shock) Report { w := base(); w.Shocks = sh; return Run(w, nil, 10) }
	ref := run()

	if r := run(Shock{Day: 3, Kind: DestroyStock, Source: "grove", Factor: 1}); r.Delivered["grove"] >= ref.Delivered["grove"] {
		t.Fatal("no trees: grove delivered as before")
	}
	if r := run(Shock{Day: 3, Kind: RemoveSource, Source: "vein"}); r.Delivered["vein"] >= ref.Delivered["vein"] || r.Days[9].Delivered["vein"] != 0 {
		t.Fatal("no ore: vein kept delivering")
	}
	if r := run(Shock{Day: 3, Kind: DestroyStock, Source: "vein", Factor: 1}); r.Days[3].Delivered["vein"] != 0 {
		t.Fatal("depleted vein delivered")
	}
	raid := run(Shock{Day: 3, Kind: DestroyStock, Good: Steel, Factor: 0.8})
	if raid.Days[3].Stock[Steel] >= ref.Days[3].Stock[Steel]*0.5 {
		t.Fatal("raid left the steel stock")
	}
	surge := run(Shock{Day: 3, Kind: SurgeDemand, Good: Wood, Factor: 6, Days: 4})
	if surge.Days[4].Stock[Wood] >= ref.Days[4].Stock[Wood] || surge.Days[9].Stock[Wood] < surge.Days[8].Stock[Wood] {
		t.Fatal("winter surge should draw wood down only while it lasts")
	}
	weapons := run(Shock{Day: 2, Kind: Requisition, Good: Steel, Factor: 150})
	if weapons.Days[2].Stock[Steel] >= ref.Days[2].Stock[Steel]-100 {
		t.Fatal("steel requisition did not draw")
	}
	full := run(Shock{Day: 3, Kind: StorageFull, Good: Steel, Factor: 100, Days: 3})
	for d := 3; d < 6; d++ {
		if full.Days[d].Stock[Steel] > full.Days[2].Stock[Steel]+1e-9 {
			t.Fatalf("stock %v grew past a full store on day %d", full.Days[d].Stock[Steel], d)
		}
	}
	if full.Delivered["vein"] >= ref.Delivered["vein"] {
		t.Fatal("storage full did not stop deliveries")
	}
	if full.Days[9].Delivered["vein"] == 0 {
		t.Fatal("deliveries should resume once storage frees")
	}
}

// resourceGoldenWorld copies the policy floors and labor priors into a colony
// with every resource dynamic and shock (see resources.go for sources).
func resourceGoldenWorld() (World, Planner) {
	w := World{
		Seed: 11, Workers: 6, Power: 400,
		Stock: map[Good]float64{Wood: 260, Steel: 150, Silver: 120, Components: 4},
		Consumers: []Consumer{
			{Name: "build", Good: Wood, PerDay: 8, Window: Window{Period: 60, From: 30, To: 50}, WindowFactor: 2.5},
			{Name: "repair", Good: Steel, PerDay: 6},
			{Name: "workshop", Good: Components, PerDay: 0.5},
		},
		Floors: []Floor{{Wood, WoodMin, WoodTarget, WoodMax}, {Steel, SteelFloor, SteelFloor, 800}, {Components, ComponentFloor, ComponentFloor, 40}},
		Builds: []Build{{Name: "wall", Day: 10, Costs: []Yield{{StoneBlocks, 80}, {Steel, 30}}}},
		Sources: []Source{
			NewTrees("grove", 150, 400, 25),
			NewQuarry("quarry", 40, 8),
			NewStonecutter("cutter", 4),
			NewVein("vein", Steel, 120, 12, 3),
			NewDeepDrill("drill", Components, 20, 1, 250),
			NewLoot("wreck", Steel, 60, 20),
			NewSalvage("hulk", Components, 5, 1),
			NewTradeOf("caravan", Steel, Window{Period: 20, From: 12, To: 14}, 50, 1, 3),
			NewHerd("deer", 12, 4, 2, 0.1).WithYield(Leather, 2),
			NewCropOf("cotton", Cotton, 20, 8, 3, Window{Period: 60, From: 0, To: 30}, 20),
		},
		Shocks: []Shock{
			{Day: 8, Kind: Threat, Days: 6},
			{Day: 15, Kind: PowerLoss, Factor: 0, Days: 4},
			{Day: 18, Kind: DestroyStock, Good: Steel, Factor: 0.5},
			{Day: 31, Kind: SurgeDemand, Good: Wood, Factor: 1.5, Days: 5},
			{Day: 32, Kind: StopWork, Source: "caravan", Days: 3},
		},
	}
	p := PlannerFunc(func(v WorldView) []Command {
		if v.Day != 2 {
			return nil
		}
		var cmds []Command
		for _, s := range v.Sources {
			cmds = append(cmds, Command{Open, s.ID})
		}
		return cmds
	})
	return w, p
}

func TestResourceGoldenReport(t *testing.T) {
	w, p := resourceGoldenWorld()
	got, err := json.MarshalIndent(Run(w, p, 45), "", " ")
	if err != nil {
		t.Fatal(err)
	}
	const path = "testdata/resources_golden.json"
	if *update {
		if err := os.WriteFile(path, append(got, '\n'), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	want, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if string(want) != string(got)+"\n" {
		t.Fatalf("report differs from %s; rerun with -update if the change is intended", path)
	}
}
