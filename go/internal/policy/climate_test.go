package policy

import (
	"math"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// seaIce is a sea-ice map's twelfth means (C): a cold year whose summer stays
// under the full-rot temperature even with the diurnal swing added.
var seaIce = []float64{-38, -36, -30, -22, -14, -6, -4, -8, -16, -24, -32, -37}

// shortSummer peaks at 4 C: its mean alone is under rot, the +7 C afternoon
// swing on top is not.
var shortSummer = []float64{-20, -18, -12, -4, 0, 4, 4, 0, -4, -10, -16, -20}

var temperate = []float64{2, 4, 8, 12, 16, 19, 21, 20, 16, 11, 6, 3}

// testClimate is the recorded game's numbers (bridge's TestStationCatalogClimate
// ModelIsTheGamesNumbers pins them): TickRare 250 and vent rate 14, the door's
// 34 and 375 ticks at rate 1, a 21 heat/s heater at 175 W and cooler at 200 W.
func testClimate() ClimateModel {
	return ClimateModel{
		Vent: Exchange{250, 14}, DoorOpen: Exchange{34, 1}, DoorClosed: Exchange{375, 1},
		Heater: ClimatePlant{21, 175}, Cooler: ClimatePlant{21, 200},
		StepTicks: 250, TicksPerSecond: 60, SwingC: 7,
		Build: ClimateBuildValue{Vent: 57, Door: 18.75, Wall: 3.75},
	}
}

func outdoorsLink(o Opening) []ClimateLink {
	return []ClimateLink{{Opening: o, Side: ClimateSide{Outdoors: true}}}
}

func mustLoad(t *testing.T, curve []float64, room ClimateRoom, traffic float64) ClimateWatts {
	t.Helper()
	got, err := ClimateLoad(testClimate(), curve, room, traffic)
	if err != nil {
		t.Fatal(err)
	}
	return got
}

var living = Band{MinC: 16, MaxC: 26}

func TestClimateWattsRiseWithAnOpenDoorOnAColdMap(t *testing.T) {
	room := func(o Opening) ClimateRoom { return ClimateRoom{Cells: 30, Band: living, Links: outdoorsLink(o)} }
	closed := mustLoad(t, seaIce, room(OpeningDoorClosed), 0)
	open := mustLoad(t, seaIce, room(OpeningDoorOpen), 0)
	if closed.MeanW <= 0 || open.MeanW <= closed.MeanW*10 {
		t.Errorf("closed %.1f W, open %.1f W: an open door should cost far more", closed.MeanW, open.MeanW)
	}
	if open.CoolingW != 0 || open.HeatingW != open.MeanW {
		t.Errorf("a cold map heats: %+v", open)
	}
	if open.PeakW < open.MeanW {
		t.Errorf("peak %.1f under mean %.1f", open.PeakW, open.MeanW)
	}
	// On a temperate map the open door costs a good deal less than on the ice.
	if mild := mustLoad(t, temperate, room(OpeningDoorOpen), 0); mild.MeanW >= open.MeanW {
		t.Errorf("temperate %.1f W is not under sea ice %.1f W", mild.MeanW, open.MeanW)
	}
}

func TestClimateWattsFallWhenAVentLeadsToAWarmRoomNotTheOutdoors(t *testing.T) {
	room := func(side ClimateSide) ClimateRoom {
		return ClimateRoom{Cells: 30, Band: living, Links: []ClimateLink{{Opening: OpeningVent, Side: side}}}
	}
	cold := mustLoad(t, seaIce, room(ClimateSide{Outdoors: true}), 0)
	warm := mustLoad(t, seaIce, room(ClimateSide{HeldC: 21, Cells: 30}), 0)
	if warm.MeanW >= cold.MeanW {
		t.Errorf("vent to a warm room %.1f W, to the outdoors %.1f W", warm.MeanW, cold.MeanW)
	}
	// The cold room is the other side's gain: the warm room holding 21 C pays
	// more, the sum moves with the heat the vent carries.
	freezer := mustLoad(t, seaIce, ClimateRoom{Cells: 30, Band: Band{MinC: math.Inf(-1), MaxC: 10}, Links: []ClimateLink{{Opening: OpeningVent, Side: ClimateSide{HeldC: 21, Cells: 30}}}}, 0)
	if freezer.CoolingW <= 0 || freezer.HeatingW != 0 {
		t.Errorf("a freezer vented to a 21 C room cools: %+v", freezer)
	}
}

func TestClimateWattsAreSymmetricForHotAndColdMaps(t *testing.T) {
	m := testClimate()
	m.Cooler = m.Heater
	mirror := func(curve []float64, about float64) []float64 {
		out := make([]float64, len(curve))
		for i, v := range curve {
			out[i] = 2*about - v
		}
		return out
	}
	// The band is 16-26; the year mirrored about 21 swaps heating for cooling.
	// The diurnal swing is a cosine, so the mirror holds a half day later; a
	// whole-year mean keeps the two within a fraction of a percent.
	room := ClimateRoom{Cells: 30, Band: living, Links: outdoorsLink(OpeningVent)}
	cold, err := ClimateLoad(m, seaIce, room, 0)
	if err != nil {
		t.Fatal(err)
	}
	hot, err := ClimateLoad(m, mirror(seaIce, 21), room, 0)
	if err != nil {
		t.Fatal(err)
	}
	if cold.HeatingW == 0 || hot.CoolingW == 0 || cold.CoolingW != 0 || hot.HeatingW != 0 {
		t.Fatalf("cold %+v, hot %+v", cold, hot)
	}
	if math.Abs(cold.MeanW-hot.MeanW) > 0.005*cold.MeanW {
		t.Errorf("cold year %.2f W, mirrored hot year %.2f W", cold.MeanW, hot.MeanW)
	}
}

func TestClimateFreezerSnapshotsCarryOverFromTheExposureModel(t *testing.T) {
	cold := Band{MinC: math.Inf(-1), MaxC: 10}
	vent := func(curve []float64, cells int) ClimateWatts {
		return mustLoad(t, curve, ClimateRoom{Cells: cells, Band: cold, Links: outdoorsLink(OpeningVent)}, 0)
	}
	// The sea-ice summer, swing included, stays under rot: no cooler runs.
	if w := vent(seaIce, 12); w.MeanW != 0 || w.PeakW != 0 {
		t.Errorf("sea ice small room %+v", w)
	}
	if w := vent(seaIce, 120); w.MeanW != 0 {
		t.Errorf("sea ice big room %+v", w)
	}
	// The swing alone breaks rot in a short summer, so the cooler runs a little;
	// a temperate year runs it much more.
	swing, mild := vent(shortSummer, 12), vent(temperate, 12)
	if swing.MeanW <= 0 || swing.CoolingW != swing.MeanW || mild.MeanW <= swing.MeanW*10 {
		t.Errorf("short summer %+v, temperate %+v", swing, mild)
	}
}

func TestClimateBiggerRoomsMoveMoreHeatUpToTheVentRate(t *testing.T) {
	small := mustLoad(t, seaIce, ClimateRoom{Cells: 4, Band: living, Links: outdoorsLink(OpeningVent)}, 0)
	mid := mustLoad(t, seaIce, ClimateRoom{Cells: 14, Band: living, Links: outdoorsLink(OpeningVent)}, 0)
	big := mustLoad(t, seaIce, ClimateRoom{Cells: 200, Band: living, Links: outdoorsLink(OpeningVent)}, 0)
	if !(small.MeanW < mid.MeanW) || mid.MeanW != big.MeanW {
		t.Errorf("4 cells %.1f W, 14 cells %.1f W, 200 cells %.1f W: the exchange moves min(rate, cells)", small.MeanW, mid.MeanW, big.MeanW)
	}
}

func TestClimateAirlockLeaksLessThanOneClosedDoorAndMoreWithTraffic(t *testing.T) {
	room := ClimateRoom{Cells: 30, Band: living, Links: outdoorsLink(OpeningAirlock)}
	closed := mustLoad(t, seaIce, ClimateRoom{Cells: 30, Band: living, Links: outdoorsLink(OpeningDoorClosed)}, 0)
	still := mustLoad(t, seaIce, room, 0)
	busy := mustLoad(t, seaIce, room, 0.05)
	if !(still.MeanW < closed.MeanW && still.MeanW < busy.MeanW) {
		t.Errorf("airlock still %.2f, busy %.2f, one closed door %.2f", still.MeanW, busy.MeanW, closed.MeanW)
	}
	// With the same traffic a lone door that opens stands wide to the cold; an
	// airlock never does.
	if ab := mustLoad(t, seaIce, room, 0.05); ab.MeanW >= mustLoad(t, seaIce, ClimateRoom{Cells: 30, Band: living, Links: outdoorsLink(OpeningDoorOpen)}, 0).MeanW {
		t.Errorf("a busy airlock %.2f leaks like a door held open", ab.MeanW)
	}
}

func TestClimateOptionsPriceTheBuildBesideTheOperatingWatts(t *testing.T) {
	w := DefaultSiteTypeWeights()
	options, err := ClimateOptions(testClimate(), w, 1.9, seaIce, ClimateRoom{Cells: 30, Band: living}, ClimateSide{Outdoors: true}, 0.02, OpeningOrder)
	if err != nil {
		t.Fatal(err)
	}
	if len(options) != 4 {
		t.Fatalf("%d options", len(options))
	}
	for i, o := range options {
		if o.Opening != OpeningOrder[i] || o.TotalW != o.Watts.MeanW+o.BuildW {
			t.Errorf("option %d: %+v", i, o)
		}
	}
	// A vent's 57 silver is 30 steel; at 2.6 per 100 steel a day against 4 per
	// kilowatt that is 30/100*2.6/4*1000 = 195 W.
	if got := options[0].BuildW; math.Abs(got-195) > 1e-6 {
		t.Errorf("vent build %.3f W, want 195", got)
	}
	if options[1].BuildW != 0 || options[2].BuildW != 0 || options[3].BuildW <= 0 {
		t.Errorf("builds %v %v %v %v", options[0].BuildW, options[1].BuildW, options[2].BuildW, options[3].BuildW)
	}
	best, ok := BestClimateOption(options)
	if !ok || best.Opening != OpeningDoorClosed {
		t.Errorf("best %+v on a heated living room of a sea-ice map", best)
	}
	// An opening the caller leaves out (an unresearched vent) is absent.
	fewer, err := ClimateOptions(testClimate(), w, 1.9, seaIce, ClimateRoom{Cells: 30, Band: living}, ClimateSide{Outdoors: true}, 0, []Opening{OpeningDoorOpen})
	if err != nil || len(fewer) != 1 || fewer[0].Opening != OpeningDoorOpen {
		t.Errorf("without a vent: %+v %v", fewer, err)
	}
	if _, ok := BestClimateOption(nil); ok {
		t.Error("a best of nothing")
	}
}

func TestClimateIsDeterministic(t *testing.T) {
	room := ClimateRoom{Cells: 30, Band: living, Links: outdoorsLink(OpeningVent)}
	a, b := mustLoad(t, seaIce, room, 0), mustLoad(t, seaIce, room, 0)
	if a != b {
		t.Errorf("%+v then %+v", a, b)
	}
}

func TestClimateRefusesWhatItCannotPrice(t *testing.T) {
	room := ClimateRoom{Cells: 12, Band: living, Links: outdoorsLink(OpeningVent)}
	bad := testClimate()
	bad.Vent.IntervalTicks = 0
	for name, run := range map[string]func() (ClimateWatts, error){
		"short curve": func() (ClimateWatts, error) { return ClimateLoad(testClimate(), seaIce[:11], room, 0) },
		"nan curve": func() (ClimateWatts, error) {
			return ClimateLoad(testClimate(), append(slices.Clone(seaIce[:11]), math.NaN()), room, 0)
		},
		"no cells": func() (ClimateWatts, error) { return ClimateLoad(testClimate(), seaIce, ClimateRoom{Band: living}, 0) },
		"inverted band": func() (ClimateWatts, error) {
			return ClimateLoad(testClimate(), seaIce, ClimateRoom{Cells: 3, Band: Band{MinC: 5, MaxC: 1}}, 0)
		},
		"unknown model": func() (ClimateWatts, error) { return ClimateLoad(bad, seaIce, room, 0) },
		"zero value":    func() (ClimateWatts, error) { return ClimateLoad(ClimateModel{}, seaIce, room, 0) },
	} {
		if _, err := run(); err == nil {
			t.Errorf("%s priced", name)
		}
	}
	// A room with no opening exchanges nothing.
	if w := mustLoad(t, seaIce, ClimateRoom{Cells: 12, Band: living}, 0); w != (ClimateWatts{}) {
		t.Errorf("sealed room %+v", w)
	}
}

func TestClimateTableNamesTheDefsItPrices(t *testing.T) {
	table := ClimateDefs()
	if table.VentDef == "" || table.DoorDef != ShellDoorDefinition || table.WallDef != ShellWallDefinition || table.SunPhase != 0.32 {
		t.Errorf("climate table %+v", table)
	}
	found := false
	for _, entry := range DefTables() {
		found = found || entry.Table == "climateTable" && slices.Equal(entry.Names, []string{table.VentDef, table.DoorDef, table.WallDef})
	}
	if !found {
		t.Error("climateTable is not a DefTables entry")
	}
}

func climateFacts() StationFacts {
	f := stationFactsFixture()
	f.Catalog.Climate = testClimate()
	f.Items = ItemFacts{Market: map[Resource]float64{"Steel": 1.9}}
	f.OutdoorCurve, f.FullRotRateC = domain.Known(slices.Clone(seaIce)), 10
	return f
}

func TestGroupClimateIsBesideColdGroupsOnly(t *testing.T) {
	f := climateFacts()
	cold := RoomGroup{Requires: []EnvRequirement{EnvCold}, Area: 12}
	got := groupClimate(f, cold)
	if got == nil || got.Cells != 12 || len(got.Options) != len(OpeningOrder) || got.Band.MaxC != 10 || !math.IsInf(got.Band.MinC, -1) {
		t.Fatalf("cold group: %+v", got)
	}
	if best, _ := BestClimateOption(got.Options); got.Best != best.Opening {
		t.Errorf("best %s, options say %s", got.Best, best.Opening)
	}
	if groupClimate(f, RoomGroup{Area: 12}) != nil {
		t.Error("a group that does not require cold got a climate answer")
	}
	for name, mutate := range map[string]func(*StationFacts){
		"no curve":       func(f *StationFacts) { f.OutdoorCurve = domain.Unknown[[]float64]() },
		"no rot":         func(f *StationFacts) { f.FullRotRateC = 0 },
		"no model":       func(f *StationFacts) { f.Catalog.Climate = ClimateModel{} },
		"no steel value": func(f *StationFacts) { f.Items = ItemFacts{} },
	} {
		g := climateFacts()
		mutate(&g)
		if groupClimate(g, cold) != nil {
			t.Errorf("climate answer with %s", name)
		}
	}
}

func TestAdviseStationsAttachesClimateToColdGroups(t *testing.T) {
	f := climateFacts()
	advice, err := AdviseStations(f)
	if err != nil {
		t.Fatal(err)
	}
	for _, g := range advice.Groups {
		if cold := slices.Contains(g.Group.Requires, EnvCold); cold != (g.Climate != nil) {
			t.Errorf("group %d: requires cold %v, climate %v", g.Group.ID, cold, g.Climate)
		}
	}
	f.OutdoorCurve = domain.Unknown[[]float64]()
	if advice, err = AdviseStations(f); err != nil {
		t.Fatal(err)
	}
	for _, g := range advice.Groups {
		if g.Climate != nil {
			t.Errorf("climate without a curve: %+v", g)
		}
	}
}
