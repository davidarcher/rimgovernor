package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"math"
	"testing"
)

func TestShrineHeatNeedsMeasuredHeatAndSafeEnclosure(t *testing.T) {
	f := ShrineHeatFacts{Temperature: 61, OutdoorTemperature: 20, Cells: 15, BoundaryCells: 16, Enclosed: true, FiringCells: []domain.Cell{{X: 10, Z: 9}}, RetreatCells: []domain.Cell{{X: 10, Z: 8}}}
	n := ShrineHeaterCount(f)
	for range n {
		f.Heaters = append(f.Heaters, ShrineHeater{ID: "heater"})
	}
	caskets := []ShrineCasket{{EntityID: "casket", Cell: domain.Cell{X: 10, Z: 12}, HitPoints: 250, MaxHitPoints: 250, HasContents: true}}
	squad := []ShrineDefenderFacts{shrineDefender("rifle", true, 25)}
	for _, tc := range []struct {
		name   string
		change func(*ShrineHeatFacts)
		want   string
	}{
		{"ready", func(*ShrineHeatFacts) {}, "heat_open"},
		{"threshold", func(f *ShrineHeatFacts) { f.Temperature = 60 }, "heat_warming"},
		{"cold", func(f *ShrineHeatFacts) { f.Temperature = 20 }, "heat_warming"},
		{"unknown", func(f *ShrineHeatFacts) { f.Temperature = math.NaN() }, "heat_unknown"},
		{"worker", func(f *ShrineHeatFacts) { f.ColonistsInside = true }, "heat_colonists_inside"},
		{"open roof", func(f *ShrineHeatFacts) { f.Enclosed = false }, "heat_enclosure"},
		{"breach", func(f *ShrineHeatFacts) { f.Enclosed = false; f.DoorSites = []domain.Cell{{X: 10, Z: 9}} }, "heat_door"},
		{"no doorway", func(f *ShrineHeatFacts) { f.FiringCells = nil }, "heat_no_doorway"},
		{"install", func(f *ShrineHeatFacts) { f.Heaters = nil; f.HeaterSites = []domain.Cell{{X: 9, Z: 10}} }, "heat_heater"},
		{"no space", func(f *ShrineHeatFacts) { f.Heaters = nil }, "heat_no_space"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			changed := f
			tc.change(&changed)
			s := AncientShrine{InHome: true, GuardsKnown: true, Heat: domain.Known(changed)}
			if got := SelectShrineHeat(s, caskets, squad); got.Phase != tc.want {
				t.Fatalf("%+v", got)
			}
		})
	}
	s := AncientShrine{InHome: true, GuardsKnown: true, Heat: domain.Known(f)}
	if got := SelectShrineHeat(s, caskets, nil); got.Phase != "heat_no_shooter" {
		t.Fatal(got)
	}
	caskets[0].HitPoints = 49
	if got := SelectShrineHeat(s, caskets, squad); got.Phase != "heat_no_shooter" {
		t.Fatal(got)
	}
}

func TestShrineHeaterSizingIncludesLeakAndRoomWarmup(t *testing.T) {
	f := ShrineHeatFacts{Cells: 15, BoundaryCells: 16, OutdoorTemperature: 20}
	// 30.6 energy lost plus 67.5 warmup energy; 48.125 per heater.
	if got := ShrineHeaterCount(f); got != 3 {
		t.Fatal(got)
	}
	f.Cells = 30
	if got := ShrineHeaterCount(f); got != 4 {
		t.Fatal(got)
	}
	f.OutdoorTemperature = -40
	if got := ShrineHeaterCount(f); got != 5 {
		t.Fatal(got)
	}
	f.Cells = 0
	if got := ShrineHeaterCount(f); got != 0 {
		t.Fatal(got)
	}
}
