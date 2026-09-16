package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ControlledEnvironment is the native census of indoor growing capacity inside
// the planning region: sun lamps with the exact cells the game grows under
// them, plant growers (hydroponics) with their fertility and sow tag, roofed
// rooms with temperature and light, and every power network's headroom split
// by source. Site-type selection reads it to price greenhouse reuse, new
// lamps, basins and dark rooms against outdoor soil; nothing here is planned
// or written natively.
type ControlledEnvironment struct {
	Lights              []GrowLight
	Growers             []PlantGrower
	Rooms               []GrowRoom
	Networks            []PowerHeadroom
	OutdoorTemperatureC domain.Fact[float64]
	Daylight            domain.Fact[bool]
}

type GrowLight struct {
	ID, Definition string
	Cell           domain.Cell
	Room, Network  domain.Fact[string]
	Powered        domain.Fact[bool]
	PowerW         domain.Fact[float64]
	// LitNow is powered and inside the lamp's native schedule.
	LitNow domain.Fact[bool]
	// GrowthCells are the native growth radius, not the wider glow radius.
	GrowthCells []domain.Cell
}

type PlantGrower struct {
	ID, Definition string
	Cell           domain.Cell
	Room, Network  domain.Fact[string]
	Powered        domain.Fact[bool]
	PowerW         domain.Fact[float64]
	Fertility      domain.Fact[float64]
	SowTag, Crop   domain.Fact[string]
	CanSow         domain.Fact[bool]
	Cells          []domain.Cell
}

type GrowRoom struct {
	ID                   string
	TemperatureC         domain.Fact[float64]
	Cells, OpenRoof, Lit domain.Fact[int]
	Proper, Outdoors     domain.Fact[bool]
}

type PowerHeadroom struct {
	ID                                                                         string
	GenerationW, SolarW, WindW, ConsumptionW, StoredWattDays, CapacityWattDays domain.Fact[float64]
	ActiveSource                                                               domain.Fact[bool]
}

// NightHeadroomW is the network's surplus once solar output stops. Unknown
// when any term is unknown.
func (p PowerHeadroom) NightHeadroomW() domain.Fact[float64] {
	generation, gk := p.GenerationW.Value()
	solar, sk := p.SolarW.Value()
	consumption, ck := p.ConsumptionW.Value()
	if !gk || !sk || !ck || !foodNumber(generation) || !foodNumber(solar) || !foodNumber(consumption) {
		return domain.Unknown[float64]()
	}
	return domain.Known(generation - solar - consumption)
}

// CalmNightHeadroomW is the surplus with neither sun nor wind: the margin a
// lamp or basin must fit in to keep growing through a still night.
func (p PowerHeadroom) CalmNightHeadroomW() domain.Fact[float64] {
	night, nk := p.NightHeadroomW().Value()
	wind, wk := p.WindW.Value()
	if !nk || !wk || !foodNumber(wind) {
		return domain.Unknown[float64]()
	}
	return domain.Known(night - wind)
}

// Network returns the headroom row for a native network id.
func (e ControlledEnvironment) Network(id string) (PowerHeadroom, bool) {
	for _, n := range e.Networks {
		if n.ID == id {
			return n, true
		}
	}
	return PowerHeadroom{}, false
}

// LitCells maps every cell a currently lit lamp grows to that lamp's id; a
// cell under two lamps keeps the lexically smaller id so output is stable.
func (e ControlledEnvironment) LitCells() map[domain.Cell]string {
	out := map[domain.Cell]string{}
	for _, l := range e.Lights {
		if !positive(l.LitNow) {
			continue
		}
		for _, c := range l.GrowthCells {
			if prev, ok := out[c]; !ok || l.ID < prev {
				out[c] = l.ID
			}
		}
	}
	return out
}

// GrowerAccepts reports whether a crop's native sow tags include the grower's
// sow tag; unknown tags never match.
func GrowerAccepts(grower PlantGrower, crop CropChoice) bool {
	tag, tk := grower.SowTag.Value()
	tags, ck := crop.SowTags.Value()
	return tk && ck && tag != "" && slices.Contains(tags, tag)
}

// GrowsInDark reports a crop whose native minimum glow is zero, such as
// cave fungus, so an unlit roofed room is eligible for it.
func GrowsInDark(crop CropChoice) bool {
	glow, ok := crop.MinGlow.Value()
	return ok && foodNumber(glow) && glow <= 0
}
