package policy

import (
	"math"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func fishCells(reachable bool, xz ...int32) []FishingCell {
	var out []FishingCell
	for i := 0; i < len(xz); i += 2 {
		out = append(out, FishingCell{Cell: domain.Cell{X: xz[i], Z: xz[i+1]}, Reachable: reachable, DistanceSquared: float64(xz[i]*xz[i] + xz[i+1]*xz[i+1])})
	}
	return out
}

func TestFishingFootprint(t *testing.T) {
	line := fishCells(true, 1, 0, 2, 0, 3, 0, 4, 0, 5, 0)
	diagonal := fishCells(true, 1, 1, 2, 2)
	frozenGap := append(fishCells(true, 1, 0, 2, 0), fishCells(false, 3, 0)...)
	frozenGap = append(frozenGap, fishCells(true, 4, 0)...)
	for _, tc := range []struct {
		name    string
		cells   []FishingCell
		fishers int
		want    []domain.Cell
	}{
		{"nearest first, capped at fishers", line, 2, []domain.Cell{{X: 1}, {X: 2}}},
		{"cap above area", line[:2], 5, []domain.Cell{{X: 1}, {X: 2}}},
		{"no fishers", line, 0, nil},
		{"no cells", nil, 3, nil},
		{"all unreachable", fishCells(false, 1, 0, 2, 0), 2, nil},
		{"connected only", diagonal, 2, []domain.Cell{{X: 1, Z: 1}}},
		{"unreachable or frozen cell breaks the chain", frozenGap, 3, []domain.Cell{{X: 1}, {X: 2}}},
		{"cardinal order north east south west", fishCells(true, 0, 0, 0, 1, 1, 0, 0, -1, -1, 0), 5,
			[]domain.Cell{{X: 0, Z: 0}, {X: 0, Z: 1}, {X: 1, Z: 0}, {X: 0, Z: -1}, {X: -1, Z: 0}}},
		{"distance tie breaks by x then z", []FishingCell{
			{Cell: domain.Cell{X: 3, Z: 1}, Reachable: true, DistanceSquared: 4},
			{Cell: domain.Cell{X: 2, Z: 2}, Reachable: true, DistanceSquared: 4},
			{Cell: domain.Cell{X: 2, Z: 1}, Reachable: true, DistanceSquared: 4}}, 1, []domain.Cell{{X: 2, Z: 1}}},
		{"unreachable nearest cell is skipped", append(fishCells(false, 1, 0), fishCells(true, 2, 0, 3, 0)...), 2, []domain.Cell{{X: 2}, {X: 3}}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			if got := FishingFootprint(tc.cells, tc.fishers); !reflect.DeepEqual(got, tc.want) {
				t.Fatalf("got %v want %v", got, tc.want)
			}
		})
	}
}

func TestFishingReachable(t *testing.T) {
	yes, no, unknown := domain.Known(true), domain.Known(false), domain.Unknown[bool]()
	for _, tc := range []struct {
		name               string
		reaches, zoned     domain.Fact[bool]
		footprint, fishers int
		want               domain.Fact[bool]
	}{
		{"unzoned short footprint forces false", yes, no, 1, 2, no},
		{"unzoned full footprint keeps reach", yes, no, 2, 2, yes},
		{"zoned keeps reach", yes, yes, 0, 2, yes},
		{"unzoned without fishers keeps reach", no, no, 0, 0, no},
		{"zoned unknown keeps reach", yes, unknown, 0, 2, yes},
		{"unknown reach, short footprint", unknown, no, 0, 1, no},
	} {
		t.Run(tc.name, func(t *testing.T) {
			if got := FishingReachable(tc.reaches, tc.zoned, tc.footprint, tc.fishers); got != tc.want {
				t.Fatalf("got %v want %v", got, tc.want)
			}
		})
	}
}

func TestFishingDelivering(t *testing.T) {
	open := FishingZoneState{Allowed: true, DoForever: true, HasFishableCells: true}
	for name, zones := range map[string][]FishingZoneState{
		"none": nil, "disallowed": {{DoForever: true, HasFishableCells: true}}, "target count": {{Allowed: true, HasFishableCells: true}},
		"no cells": {{Allowed: true, DoForever: true}},
	} {
		if FishingDelivering(zones) {
			t.Fatalf("%s delivers", name)
		}
	}
	if !FishingDelivering([]FishingZoneState{{}, open}) {
		t.Fatal("open zone not delivering")
	}
}

func TestFishingResearchLeadDays(t *testing.T) {
	k := domain.Known[float64]
	in := FishingResearchInputs{Researched: domain.Known(false), BaseCost: k(400), Progress: k(100), CostFactor: k(1.5),
		ResearcherSpeeds: []float64{0.5, 1}, PointsPerWorkTick: k(0.00825), Difficulty: k(1)}
	want := 300 * 1.5 / (1 * 0.00825 * 20000 * 1)
	if got, known := FishingResearchLeadDays(in).Value(); !known || math.Abs(got-want) > 1e-12 {
		t.Fatalf("lead %v %v want %v", got, known, want)
	}
	in.Progress = k(500)
	if got, _ := FishingResearchLeadDays(in).Value(); got != 0 {
		t.Fatalf("overdone project lead %v", got)
	}
	in.ResearcherSpeeds = []float64{0}
	if _, known := FishingResearchLeadDays(in).Value(); known {
		t.Fatal("no researcher speed has a lead")
	}
	in.Researched = domain.Known(true)
	if got, known := FishingResearchLeadDays(in).Value(); !known || got != 0 {
		t.Fatalf("researched lead %v %v", got, known)
	}
	in.Researched = domain.Unknown[bool]()
	if _, known := FishingResearchLeadDays(in).Value(); known {
		t.Fatal("unknown research has a lead")
	}
}

func TestFishingWorkCapacity(t *testing.T) {
	k := domain.Known[float64]
	fishers := []Fisher{{Yield: 1, Speed: 1}, {Yield: 0.5, Speed: 2}}
	// Yield 0.5*6=3 fish; 1*6=6 fish; banker's rounding at 2.5 gives 2.
	want := 20000.0 * 0.25 / 7500 * (6*1 + 3*2)
	if got, known := FishingWorkCapacity(fishers, k(6), k(0.25), k(7500)).Value(); !known || math.Abs(got-want) > 1e-9 {
		t.Fatalf("capacity %v %v want %v", got, known, want)
	}
	if got, _ := FishingWorkCapacity([]Fisher{{Yield: 0.5, Speed: 1}}, k(5), k(1), k(20000)).Value(); got != 2 {
		t.Fatalf("round half to even: %v", got)
	}
	if got, _ := FishingWorkCapacity([]Fisher{{Yield: 0.01, Speed: 1}}, k(1), k(1), k(20000)).Value(); got != 1 {
		t.Fatalf("batch floor of one fish: %v", got)
	}
	if got, known := FishingWorkCapacity(nil, k(6), k(0.25), k(7500)).Value(); !known || got != 0 {
		t.Fatalf("no fishers: %v %v", got, known)
	}
	for _, un := range [][3]domain.Fact[float64]{
		{domain.Unknown[float64](), k(1), k(1)}, {k(1), domain.Unknown[float64](), k(1)}, {k(1), k(1), domain.Unknown[float64]()}, {k(1), k(1), k(0)},
	} {
		if _, known := FishingWorkCapacity(fishers, un[0], un[1], un[2]).Value(); known {
			t.Fatal("unread input produced capacity")
		}
	}
}
