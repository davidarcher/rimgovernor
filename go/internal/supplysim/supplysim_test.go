package supplysim

import (
	"encoding/json"
	"flag"
	"math"
	"os"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

var update = flag.Bool("update", false, "rewrite testdata/golden.json")

func open(s Source) Source { s.Open = true; return s }

func near(t *testing.T, name string, got, want, tol float64) {
	t.Helper()
	if math.Abs(got-want) > tol {
		t.Errorf("%s = %v, want %v +- %v", name, got, want, tol)
	}
}

func firstDelivery(r Report, id string) int {
	for _, d := range r.Days {
		if d.Delivered[id] > 0 {
			return d.Day
		}
	}
	return -1
}

func bank(workers int, srcs ...Source) World {
	return World{Workers: workers, Stock: map[Good]float64{Nutrition: 1000}, Sources: srcs}
}

func TestRunIsDeterministicInSeed(t *testing.T) {
	forage := open(NewForage("forage", 50, 2, 1, Window{}, 3))
	forage.Jitter = 0.3
	w := World{Seed: 7, Workers: 3, Stock: map[Good]float64{Nutrition: 10},
		Consumers: []Consumer{{Good: Nutrition, PerDay: 4}}, Sources: []Source{forage}}
	a, b := Run(w, nil, 30), Run(w, nil, 30)
	if !reflect.DeepEqual(a, b) {
		t.Fatal("same seed produced different reports")
	}
	w.Seed = 8
	if reflect.DeepEqual(a, Run(w, nil, 30)) {
		t.Fatal("different seed produced the same report")
	}
	if w.Sources[0].Stock != 50 || w.Sources[0].delivered != 0 {
		t.Fatal("Run mutated its input world")
	}
}

func TestFishingRegrowsAndPausesAtFloor(t *testing.T) {
	fish := open(NewFishing("lake", 100, 100, 10, 1))
	rep := Run(bank(10, fish), nil, 200)
	floor := domain.FishingPopulationFloor * 100
	var paused, resumed bool
	for _, d := range rep.Days {
		if d.Delivered["lake"] == 0 {
			paused = true
		} else if paused {
			resumed = true
		}
	}
	if !paused || !resumed {
		t.Fatalf("fishing never paused and resumed (paused=%v resumed=%v)", paused, resumed)
	}
	// Total catch is the initial surplus above the floor plus regeneration.
	near(t, "catch", rep.Delivered["lake"], (100-floor)+FishingRegenFraction*100*200, 40)
	// Over a long run the catch rate approaches regeneration per day.
	var tail float64
	for _, d := range rep.Days[100:] {
		tail += d.Delivered["lake"]
	}
	near(t, "tail catch/day", tail/100, FishingRegenFraction*100, 0.3)
}

func TestFishingCapacityLimitedByFishers(t *testing.T) {
	rep := Run(bank(10, open(NewFishing("lake", 1000, 1000, 2, 1))), nil, 5)
	near(t, "catch/day", rep.Days[0].Delivered["lake"], 2*FishPerFisherDay, 1e-9)
}

func TestCropGrowthWindowAndShocks(t *testing.T) {
	season := Window{Period: 60, From: 0, To: 30}
	crop := func() Source { return open(NewCrop("rice", 20, 10, 5, season, 20)) }
	base := Run(bank(10, crop()), nil, 120)
	if got := firstDelivery(base, "rice"); got != 11 {
		t.Fatalf("first harvest day %d, want 11 (10 growing days)", got)
	}
	for _, d := range base.Days[30:61] {
		if d.Delivered["rice"] > 0 {
			t.Fatalf("harvest on day %d outside the growing window", d.Day)
		}
	}
	near(t, "burst", base.Days[11].Delivered["rice"], 20, 1e-9)

	frost := bank(10, crop())
	frost.Shocks = []Shock{{Day: 5, Kind: DestroyStock, Source: "rice", Factor: 1}}
	if got := firstDelivery(Run(frost, nil, 120), "rice"); got != 16 {
		t.Fatalf("first harvest after frost on day 5 = %d, want 16 (replanted)", got)
	}
	pause := bank(10, crop())
	pause.Shocks = []Shock{{Day: 5, Kind: PauseGrowth, Source: "rice", Days: 3}}
	if got := firstDelivery(Run(pause, nil, 120), "rice"); got != 14 {
		t.Fatalf("first harvest after a 3 day pause = %d, want 14", got)
	}
}

func TestHerdDepletes(t *testing.T) {
	rep := Run(bank(2, open(NewHerd("deer", 10, 5, 2, 0))), nil, 12)
	near(t, "delivered animals", rep.Delivered["deer"], 10, 1e-9)
	if rep.Days[5].Delivered["deer"] != 0 || rep.Days[4].Delivered["deer"] == 0 {
		t.Fatal("herd should be gone after day 4")
	}
	dangerous := Run(bank(2, open(NewHerd("boar", 10, 5, 2, 0.5))), nil, 6)
	near(t, "dangerous day", dangerous.Days[0].Delivered["boar"], 1, 1e-9)
}

func TestTradeWindowAndPrice(t *testing.T) {
	w := bank(5, open(NewTrade("trader", Window{Period: 10, From: 4, To: 6}, 20, 3, 2)))
	w.Stock = map[Good]float64{Nutrition: 0, Silver: 50}
	rep := Run(w, nil, 25)
	for _, d := range rep.Days {
		inWindow := d.Day%10 == 4
		if (d.Delivered["trader"] > 0) != (inWindow && d.Day < 20) {
			t.Fatalf("day %d delivered %v", d.Day, d.Delivered["trader"])
		}
	}
	// 20 units on day 4 cost 40 silver; 10 silver buys 5 units on day 14.
	near(t, "day 4", rep.Days[4].Delivered["trader"], 20, 1e-9)
	near(t, "day 14", rep.Days[14].Delivered["trader"], 5, 1e-9)
	near(t, "silver", rep.Days[24].Stock[Silver], 0, 1e-9)
}

func TestSpoilage(t *testing.T) {
	w := World{Stock: map[Good]float64{Nutrition: 100}, Spoilage: map[Good]float64{Nutrition: 0.1}}
	near(t, "stock", Run(w, nil, 10).Days[9].Stock[Nutrition], 100*math.Pow(0.9, 10), 1e-4)
}

func TestShocks(t *testing.T) {
	w := bank(10, open(NewProducts("milk", 10, 1, 1)), open(NewFishing("lake", 1000, 1000, 2, 1)))
	w.Shocks = []Shock{
		{Day: 2, Kind: ScaleCapacity, Source: "milk", Factor: 0.5},
		{Day: 3, Kind: RemoveSource, Source: "lake"},
		{Day: 4, Kind: DestroyStock, Good: Nutrition, Factor: 0.5},
	}
	rep := Run(w, nil, 6)
	near(t, "milk before", rep.Days[1].Delivered["milk"], 10, 1e-9)
	near(t, "milk scaled", rep.Days[2].Delivered["milk"], 5, 1e-9)
	if rep.Days[2].Delivered["lake"] == 0 || rep.Days[3].Delivered["lake"] != 0 {
		t.Fatal("lake should deliver through day 2 and vanish on day 3")
	}
	if before, after := rep.Days[3].Stock[Nutrition], rep.Days[4].Stock[Nutrition]; after > before*0.6+5 {
		t.Fatalf("world stock %v -> %v, want about half destroyed", before, after)
	}
}

func TestHerdStockShock(t *testing.T) {
	w := bank(2, open(NewHerd("deer", 10, 5, 1, 0)))
	w.Shocks = []Shock{{Day: 1, Kind: DestroyStock, Source: "deer", Factor: 1}}
	near(t, "delivered animals", Run(w, nil, 5).Delivered["deer"], 1, 1e-9)
}

func TestRunwayAndStarvedDay(t *testing.T) {
	w := World{Stock: map[Good]float64{Nutrition: 30}, Consumers: []Consumer{{Good: Nutrition, PerDay: 10}}}
	rep := Run(w, nil, 6)
	if rep.FirstStarved[Nutrition] != 3 || rep.StarvedDays[Nutrition] != 3 {
		t.Fatalf("first starved %d, starved days %d; want 3 and 3", rep.FirstStarved[Nutrition], rep.StarvedDays[Nutrition])
	}
	near(t, "runway day 0", rep.Days[0].Runway[Nutrition], 2, 1e-9)
	near(t, "min runway", rep.MinRunway[Nutrition], 0, 1e-9)
	near(t, "unmet", rep.Days[3].Unmet[Nutrition], 10, 1e-9)
	fed := Run(World{Stock: map[Good]float64{Nutrition: 100}, Consumers: []Consumer{{Good: Nutrition, PerDay: 10}}}, nil, 5)
	if fed.Starved(Nutrition) {
		t.Fatal("a fed colony reported starvation")
	}
}

func TestConsumerGrowth(t *testing.T) {
	c := Consumer{PerDay: 10, GrowthPerDay: 1}
	near(t, "demand day 5", c.Demand(5), 15, 1e-9)
}

func TestPlannerOpensWithLeadAndCloses(t *testing.T) {
	src := NewProducts("milk", 10, 1, 1)
	src.Lead = 3
	var leads []int
	p := PlannerFunc(func(v WorldView) []Command {
		leads = append(leads, v.Sources[0].LeadLeft)
		switch v.Day {
		case 2:
			return []Command{{Open, "milk"}}
		case 8:
			return []Command{{Close, "milk"}}
		}
		return nil
	})
	rep := Run(bank(10, src), p, 12)
	if got := firstDelivery(rep, "milk"); got != 5 {
		t.Fatalf("first delivery day %d, want 5 (opened day 2, lead 3)", got)
	}
	if rep.Days[8].Delivered["milk"] != 0 || rep.Days[7].Delivered["milk"] == 0 {
		t.Fatal("milk should stop when closed on day 8")
	}
	if leads[3] != 2 {
		t.Fatalf("lead left on day 3 = %d, want 2", leads[3])
	}
}

func TestLaborBudgetScalesDeliveries(t *testing.T) {
	a, b := open(NewProducts("a", 10, 1, 1)), open(NewProducts("b", 10, 1, 1))
	a.Labor, b.Labor = LaborPerWorkerDay, LaborPerWorkerDay
	rep := Run(bank(1, a, b), nil, 1)
	near(t, "a", rep.Days[0].Delivered["a"], 5, 1e-9)
	near(t, "b", rep.Days[0].Delivered["b"], 5, 1e-9)
}

func TestViewCarriesTick(t *testing.T) {
	var tick domain.Tick
	Run(bank(1), PlannerFunc(func(v WorldView) []Command {
		if v.Day == 2 {
			tick = v.Tick
		}
		return nil
	}), 3)
	if tick != 2*domain.TicksPerDay {
		t.Fatalf("tick %d", tick)
	}
}

func goldenWorld() (World, Planner) {
	forage := NewForage("forage", 40, 1.5, 2, Window{Period: 30, From: 0, To: 20}, 2)
	forage.Jitter = 0.2
	w := World{
		Seed: 42, Workers: 4,
		Stock:     map[Good]float64{Nutrition: 60, Silver: 80},
		Consumers: []Consumer{{Name: "colonists", Good: Nutrition, PerDay: 12, GrowthPerDay: 0.1}},
		Spoilage:  map[Good]float64{Nutrition: 0.01},
		Sources: []Source{
			NewFishing("lake", 80, 100, 3, 2),
			NewCrop("rice", 30, 8, 3, Window{Period: 40, From: 0, To: 25}, 15),
			NewHerd("deer", 12, 20, 2, 0.2),
			forage,
			NewProducts("milk", 4, 2, 2),
			NewTrade("trader", Window{Period: 20, From: 10, To: 12}, 15, 2, 3),
		},
		Shocks: []Shock{
			{Day: 12, Kind: DestroyStock, Source: "rice", Factor: 0.5},
			{Day: 20, Kind: ScaleCapacity, Source: "lake", Factor: 0.5},
			{Day: 30, Kind: RemoveSource, Source: "deer"},
		},
	}
	p := PlannerFunc(func(v WorldView) []Command {
		if v.Day != 3 {
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

func TestGoldenReport(t *testing.T) {
	w, p := goldenWorld()
	got, err := json.MarshalIndent(Run(w, p, 45), "", " ")
	if err != nil {
		t.Fatal(err)
	}
	const path = "testdata/golden.json"
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
