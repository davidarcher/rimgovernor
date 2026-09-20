package policy

import (
	"fmt"
	"math"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestBuildResourceDemand(t *testing.T) {
	steel := ResourceKey{Def: "Steel"}
	granite := ResourceKey{Def: "Wall", Stuff: "BlocksGranite"}
	limestone := ResourceKey{Def: "Wall", Stuff: "BlocksLimestone"}
	wall := ResourceKey{Def: "Wall"}
	for _, tc := range []struct {
		name         string
		targets, bom []ResourceDemand
		floors       map[string]int64
		stock        []ResourceQuantity
		want         []ResourceDemand
	}{
		{name: "shortage", targets: []ResourceDemand{{steel, 100, 2}}, stock: []ResourceQuantity{{steel, 40}}, want: []ResourceDemand{{steel, 60, 2}}},
		{name: "satisfied", targets: []ResourceDemand{{steel, 100, 2}}, stock: []ResourceQuantity{{steel, 100}}},
		{name: "floor and target overlap", targets: []ResourceDemand{{steel, 100, 2}, {steel, 80, 3}}, floors: map[string]int64{"Steel": 150}, stock: []ResourceQuantity{{steel, 40}}, want: []ResourceDemand{{steel, 110, 3}}},
		{name: "economic floor", floors: map[string]int64{"Steel": 100}, want: []ResourceDemand{{steel, 100, 1}}},
		{name: "construction anticipates shortage", targets: []ResourceDemand{{steel, 100, 2}}, bom: []ResourceDemand{{steel, 60, 3}, {steel, 20, 3}}, stock: []ResourceQuantity{{steel, 100}}, want: []ResourceDemand{{steel, 80, 3}}},
		{name: "planned exact stuff", bom: []ResourceDemand{{granite, 10, 3}}, stock: []ResourceQuantity{{limestone, 10}}, want: []ResourceDemand{{granite, 10, 3}}},
		{name: "stock counted once", floors: map[string]int64{"Wall": 10}, bom: []ResourceDemand{{granite, 5, 3}}, stock: []ResourceQuantity{{granite, 7}, {limestone, 3}}, want: []ResourceDemand{{wall, 5, 1}}},
		{name: "exact target is part of generic floor", targets: []ResourceDemand{{granite, 5, 2}}, floors: map[string]int64{"Wall": 10}, stock: []ResourceQuantity{{granite, 5}, {limestone, 5}}},
		{name: "wrong stuff cannot fill exact target", targets: []ResourceDemand{{granite, 5, 2}}, floors: map[string]int64{"Wall": 10}, stock: []ResourceQuantity{{limestone, 10}}, want: []ResourceDemand{{granite, 5, 2}}},
		{name: "empty"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			got, err := BuildResourceDemand(ResourceDemandInput{Targets: tc.targets, EconomicFloors: tc.floors, PlannedConstruction: tc.bom, Stock: domain.Known(tc.stock)})
			rows, known := got.Value()
			if err != nil || !known || !reflect.DeepEqual(rows, tc.want) {
				t.Fatalf("got %v known=%v err=%v want %v", rows, known, err, tc.want)
			}
		})
	}
}

func TestResourceDemandUnknownAndValidation(t *testing.T) {
	key := ResourceKey{Def: "Steel"}
	good := ResourceDemand{key, 10, 2}
	for _, tc := range []struct {
		name      string
		in        ResourceDemandInput
		wantError bool
	}{
		{"unknown stock", ResourceDemandInput{Targets: []ResourceDemand{good}}, false},
		{"negative floor", ResourceDemandInput{EconomicFloors: map[string]int64{"Steel": -1}}, true},
		{"invalid def", ResourceDemandInput{EconomicFloors: map[string]int64{"": 1}}, true},
		{"negative target", ResourceDemandInput{Targets: []ResourceDemand{{key, -1, 2}}}, true},
		{"priority", ResourceDemandInput{Targets: []ResourceDemand{{key, 1, 101}}}, true},
		{"combined bound", ResourceDemandInput{Targets: []ResourceDemand{{key, maxAcquisitionCount, 2}}, PlannedConstruction: []ResourceDemand{good}}, true},
		{"duplicate stock", ResourceDemandInput{Stock: domain.Known([]ResourceQuantity{{key, 1}, {key, 2}})}, true},
		{"negative stock", ResourceDemandInput{Stock: domain.Known([]ResourceQuantity{{key, -1}})}, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			got, err := BuildResourceDemand(tc.in)
			if (err != nil) != tc.wantError {
				t.Fatalf("err=%v", err)
			}
			if _, known := got.Value(); known {
				t.Fatal("invalid/unknown stock became known demand")
			}
		})
	}
}

func acquisitionFixture(id string, kind AcquisitionKind, def Resource, distance float64) AcquisitionCandidate {
	return AcquisitionCandidate{ID: id, Kind: kind, Yields: []AcquisitionYield{{ResourceQuantity: ResourceQuantity{ResourceKey{Def: def}, 50}, UnitValue: 1, Headroom: domain.Known(int64(100))}}, PathDistance: domain.Known(distance), Labor: domain.Known(0.0), NeedsHaul: true, UnitsPerTrip: 50}
}

func TestResourceCandidateRanking(t *testing.T) {
	steel := ResourceKey{Def: "Steel"}
	demand := domain.Known([]ResourceDemand{{steel, 100, 2}})
	for _, kind := range []AcquisitionKind{AcquisitionLoot, AcquisitionSalvage, AcquisitionMining} {
		t.Run(string(kind), func(t *testing.T) {
			near := acquisitionFixture("near", kind, "Steel", 10)
			far := acquisitionFixture("far", kind, "Steel", 100)
			gold := acquisitionFixture("gold", kind, "Gold", 100)
			gold.Yields[0].UnitValue = 10000
			rows, err := RankResourceCandidates(demand, []AcquisitionCandidate{gold, far, near}, AcquisitionCompetition{})
			if err != nil || len(rows) != 2 || rows[0].ID != "near" || rows[1].ID != "far" {
				t.Fatalf("%v %v", rows, err)
			}
			for _, empty := range []domain.Fact[[]ResourceDemand]{domain.Known([]ResourceDemand{}), domain.Unknown[[]ResourceDemand]()} {
				rows, err = RankResourceCandidates(empty, []AcquisitionCandidate{near, far, gold}, AcquisitionCompetition{})
				if err != nil || len(rows) != 0 {
					t.Fatalf("without demand: %v %v", rows, err)
				}
			}
		})
	}
}

func TestResourceCandidateCostsAndHolds(t *testing.T) {
	demand := domain.Known([]ResourceDemand{{ResourceKey{Def: "Steel"}, 50, 2}})
	base := acquisitionFixture("source", AcquisitionMining, "Steel", 10)
	baseline, err := ScoreResourceCandidate(demand, base, AcquisitionCompetition{})
	if err != nil {
		t.Fatal(err)
	}
	for _, tc := range []struct {
		name      string
		change    func(*AcquisitionCandidate)
		urgent    int
		hold      string
		lower     bool
		wantError bool
	}{
		{name: "labor", change: func(c *AcquisitionCandidate) { c.Labor = domain.Known(100.0) }, lower: true},
		{name: "hauling trips", change: func(c *AcquisitionCandidate) { c.UnitsPerTrip = 10 }, lower: true},
		{name: "limited storage", change: func(c *AcquisitionCandidate) { c.Yields[0].Headroom = domain.Known(int64(10)) }, lower: true},
		{name: "full storage", change: func(c *AcquisitionCandidate) { c.Yields[0].Headroom = domain.Known(int64(0)) }, hold: "no_storage_headroom"},
		{name: "unknown storage", change: func(c *AcquisitionCandidate) { c.Yields[0].Headroom = domain.Unknown[int64]() }, hold: "no_storage_headroom"},
		{name: "urgent competing work", urgent: 3, hold: "competing_urgent_work"},
		{name: "equal urgency", urgent: 2},
		{name: "lower urgency", urgent: 1},
		{name: "unknown route", change: func(c *AcquisitionCandidate) { c.PathDistance = domain.Unknown[float64]() }, hold: "unknown_demand_or_cost"},
		{name: "unknown labor", change: func(c *AcquisitionCandidate) { c.Labor = domain.Unknown[float64]() }, hold: "unknown_demand_or_cost"},
		{name: "negative distance", change: func(c *AcquisitionCandidate) { c.PathDistance = domain.Known(-1.0) }, wantError: true},
		{name: "nan", change: func(c *AcquisitionCandidate) { c.Yields[0].UnitValue = math.NaN() }, wantError: true},
		{name: "infinity", change: func(c *AcquisitionCandidate) { c.Labor = domain.Known(math.Inf(1)) }, wantError: true},
		{name: "no carry capacity", change: func(c *AcquisitionCandidate) { c.UnitsPerTrip = 0 }, wantError: true},
		{name: "invalid kind", change: func(c *AcquisitionCandidate) { c.Kind = "unknown" }, wantError: true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			c := base
			c.Yields = append([]AcquisitionYield(nil), base.Yields...)
			if tc.change != nil {
				tc.change(&c)
			}
			got, err := ScoreResourceCandidate(demand, c, AcquisitionCompetition{UrgentPriority: tc.urgent})
			if (err != nil) != tc.wantError {
				t.Fatalf("%v %v", got, err)
			}
			if tc.wantError {
				return
			}
			if got.Hold != tc.hold || tc.hold != "" && got.Score != 0 || tc.hold == "" && got.Score <= 0 {
				t.Fatalf("%+v", got)
			}
			if tc.lower && got.Score >= baseline.Score {
				t.Fatalf("cost did not reduce score: %+v baseline %+v", got, baseline)
			}
		})
	}
}

func TestResourceCandidateValueAndStuff(t *testing.T) {
	key := ResourceKey{Def: "Chair", Stuff: "WoodLog"}
	c := acquisitionFixture("salvage", AcquisitionSalvage, "Chair", 10)
	c.Yields[0].Key = key
	c.Yields[0].Count = 10
	c.UnitsPerTrip = 4
	demand := domain.Known([]ResourceDemand{{key, 3, 3}, {ResourceKey{Def: "Chair"}, 2, 1}})
	s, err := ScoreResourceCandidate(demand, c, AcquisitionCompetition{})
	if err != nil || s.Wanted != 5 || s.Trips != 2 || s.Value != 22 {
		t.Fatalf("%+v %v", s, err)
	}
	c.Yields[0].UnitValue = 3
	valuable, err := ScoreResourceCandidate(demand, c, AcquisitionCompetition{})
	if err != nil || valuable.Score <= s.Score {
		t.Fatalf("value: %+v %v", valuable, err)
	}
	c.Yields[0].Key.Stuff = "Steel"
	s, err = ScoreResourceCandidate(demand, c, AcquisitionCompetition{})
	if err != nil || s.Wanted != 2 {
		t.Fatalf("wrong stuff: %+v %v", s, err)
	}
	c.NeedsHaul = false
	c.Yields[0].Headroom = domain.Known(int64(0))
	s, err = ScoreResourceCandidate(demand, c, AcquisitionCompetition{})
	if err != nil || s.Wanted != 2 || s.Trips != 0 || s.Score <= 0 {
		t.Fatalf("onsite consumption: %+v %v", s, err)
	}
}

func TestResourceRankingTieBreakAndInputOrder(t *testing.T) {
	keys := []ResourceKey{{Def: "Steel"}, {Def: "Wall", Stuff: "WoodLog"}, {Def: "Wall"}}
	input := ResourceDemandInput{Targets: []ResourceDemand{{keys[0], 50, 2}, {keys[2], 20, 2}}, PlannedConstruction: []ResourceDemand{{keys[1], 10, 2}}, Stock: domain.Known([]ResourceQuantity{{keys[1], 5}, {keys[0], 10}})}
	wantDemand, err := BuildResourceDemand(input)
	if err != nil {
		t.Fatal(err)
	}
	candidates := []AcquisitionCandidate{
		acquisitionFixture("b", AcquisitionMining, "Steel", 10),
		acquisitionFixture("a", AcquisitionMining, "Steel", 10),
		acquisitionFixture("z", AcquisitionLoot, "Steel", 10),
	}
	want, err := RankResourceCandidates(wantDemand, candidates, AcquisitionCompetition{})
	if err != nil || len(want) != 3 || want[0].ID != "z" || want[1].ID != "a" || want[2].ID != "b" {
		t.Fatalf("%v %v", want, err)
	}
	for i := 0; i < 30; i++ {
		input.Targets[0], input.Targets[1] = input.Targets[1], input.Targets[0]
		candidates[0], candidates[1], candidates[2] = candidates[1], candidates[2], candidates[0]
		gotDemand, err := BuildResourceDemand(input)
		if err != nil || !reflect.DeepEqual(gotDemand, wantDemand) {
			t.Fatalf("demand order changed: %v %v", gotDemand, err)
		}
		rows, _ := gotDemand.Value()
		for a, b := 0, len(rows)-1; a < b; a, b = a+1, b-1 {
			rows[a], rows[b] = rows[b], rows[a]
		}
		got, err := RankResourceCandidates(domain.Known(rows), candidates, AcquisitionCompetition{})
		if err != nil || !reflect.DeepEqual(got, want) {
			t.Fatalf("ranking order changed: %v %v", got, err)
		}
	}
}

func TestResourceDemandCombinedRowBound(t *testing.T) {
	floors := make(map[string]int64, 4096)
	for i := 0; i < 4096; i++ {
		floors[fmt.Sprintf("Def%d", i)] = 1
	}
	_, err := BuildResourceDemand(ResourceDemandInput{EconomicFloors: floors, Targets: []ResourceDemand{{ResourceKey{Def: "Steel"}, 1, 2}}, Stock: domain.Known([]ResourceQuantity{})})
	if err == nil {
		t.Fatal("combined rows exceed the scorer input bound")
	}
}

func TestResourceCandidateYieldOrderAndDuplicateIdentity(t *testing.T) {
	c := acquisitionFixture("source", AcquisitionSalvage, "Steel", 10)
	c.Yields = append(c.Yields, AcquisitionYield{ResourceQuantity: ResourceQuantity{ResourceKey{Def: "Gold"}, 5}, UnitValue: 4, Headroom: domain.Known(int64(5))})
	demand := domain.Known([]ResourceDemand{{ResourceKey{Def: "Steel"}, 20, 2}, {ResourceKey{Def: "Gold"}, 5, 1}})
	want, err := ScoreResourceCandidate(demand, c, AcquisitionCompetition{})
	if err != nil {
		t.Fatal(err)
	}
	c.Yields[0], c.Yields[1] = c.Yields[1], c.Yields[0]
	got, err := ScoreResourceCandidate(demand, c, AcquisitionCompetition{})
	if err != nil || got != want {
		t.Fatalf("yield order changed score: %+v want %+v, %v", got, want, err)
	}
	if _, err := RankResourceCandidates(demand, []AcquisitionCandidate{c, c}, AcquisitionCompetition{}); err == nil {
		t.Fatal("duplicate identity accepted")
	}
	c.Yields = append(c.Yields, c.Yields[0])
	if _, err := ScoreResourceCandidate(demand, c, AcquisitionCompetition{}); err == nil {
		t.Fatal("duplicate yield accepted")
	}
}
