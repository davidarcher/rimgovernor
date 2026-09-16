package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestControlledEnvironmentHelpers(t *testing.T) {
	cell := domain.Cell{X: 5, Z: 5}
	e := ControlledEnvironment{Lights: []GrowLight{
		{ID: "lamp-b", LitNow: domain.Known(true), GrowthCells: []domain.Cell{cell}},
		{ID: "lamp-a", LitNow: domain.Known(true), GrowthCells: []domain.Cell{cell}},
		{ID: "lamp-off", LitNow: domain.Known(false), GrowthCells: []domain.Cell{{X: 9, Z: 9}}},
		{ID: "lamp-unknown", GrowthCells: []domain.Cell{{X: 8, Z: 8}}},
	}}
	lit := e.LitCells()
	if len(lit) != 1 || lit[cell] != "lamp-a" {
		t.Fatal("unlit or unknown lamps grew cells", lit)
	}
	net := PowerHeadroom{GenerationW: domain.Known(3000.0), SolarW: domain.Known(1700.0), WindW: domain.Known(300.0), ConsumptionW: domain.Known(600.0)}
	if net.NightHeadroomW() != domain.Known(700.0) || net.CalmNightHeadroomW() != domain.Known(400.0) {
		t.Fatal(net)
	}
	net.WindW = domain.Unknown[float64]()
	if _, known := net.CalmNightHeadroomW().Value(); known || net.NightHeadroomW() != domain.Known(700.0) {
		t.Fatal("unknown wind did not stay unknown")
	}
	net.SolarW = domain.Unknown[float64]()
	if _, known := net.NightHeadroomW().Value(); known {
		t.Fatal("unknown solar did not stay unknown")
	}
	basin := PlantGrower{SowTag: domain.Known("Hydroponic")}
	rice := CropChoice{SowTags: domain.Known([]string{"Ground", "Hydroponic"}), MinGlow: domain.Known(0.3)}
	corn := CropChoice{SowTags: domain.Known([]string{"Ground"}), MinGlow: domain.Known(0.3)}
	fungus := CropChoice{SowTags: domain.Known([]string{"Ground"}), MinGlow: domain.Known(0.0)}
	if !GrowerAccepts(basin, rice) || GrowerAccepts(basin, corn) || GrowerAccepts(PlantGrower{}, rice) || GrowerAccepts(basin, CropChoice{}) {
		t.Fatal("sow tag compatibility")
	}
	if GrowsInDark(rice) || !GrowsInDark(fungus) || GrowsInDark(CropChoice{}) {
		t.Fatal("dark eligibility")
	}
}
