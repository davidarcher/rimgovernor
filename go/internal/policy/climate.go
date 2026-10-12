package policy

import (
	"errors"
	"fmt"
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Climate model (#2940, replacing #2880's vent count): the sustained extra
// heater or cooler power, in watts, that holds a room in its temperature band
// given its openings (vent, door held open, door closed, airlock), the
// outdoor curve and the heat the openings move. Every number is the game's:
// the equalize interval and rate of a vent and of a door come from the defs
// and the game's constants (ClimateModel, read by the bridge), the heater's
// and cooler's heat per second and power draw from the catalog, and the one
// judgment (the sun cycle's phase, which no def carries) from climateTable, a
// DefTables entry. The model is advice beside the old planner, which stays
// authoritative until #2931.
//
// Left out, because every option pays it alike: heat leaking through walls and
// roofs, heat given off inside the room, weather offsets and drift inside a
// twelfth of the year. The room has no thermal inertia: at each step of a day
// resolved year it is held where the openings would float it, clamped into the
// band, and the power is the heat that holding it costs.

// ClimateTable is the model's one judgment table, registered in DefTables so a
// renamed def fails the dangling-reference gate. The defs are the buildings the
// options are priced in; SunPhase is the phase of GenTemperature.OffsetFromSunCycle,
// cos(2pi(dayPct+SunPhase)) times TemperatureTuning.DailyTempVariationAmplitude:
// a literal in the game's code that no def or constant states.
type ClimateTable struct {
	VentDef, DoorDef, WallDef string
	SunPhase                  float64
}

var climateTable = ClimateTable{VentDef: "Vent", DoorDef: ShellDoorDefinition, WallDef: ShellWallDefinition, SunPhase: 0.32}

// ClimateDefs are the building defs the climate options are priced in.
func ClimateDefs() ClimateTable { return climateTable }

// airlockVestibuleCells is the airlock's one floor cell (PlannedAirlock).
const airlockVestibuleCells = 1

// Exchange is how a building equalizes the rooms either side of it:
// GenTemperature.EqualizeTemperaturesThroughBuilding every IntervalTicks, each
// room moving Rate/cells of the way to the pair's mean, the cells capped at
// Rate (the game's num3).
type Exchange struct {
	IntervalTicks int
	Rate          float64
}

// conductance is the heat (cell-degrees C) one tick moves per degree of
// difference between the sides, when the smaller room has cells floor cells: an
// event moves (difference/2)*min(Rate, cells).
func (e Exchange) conductance(cells int) float64 {
	return min(e.Rate, float64(cells)) / 2 / float64(e.IntervalTicks)
}

func (e Exchange) valid() bool { return e.IntervalTicks > 0 && finitePositive(e.Rate) }

// ClimatePlant is a heater or cooler: the heat it moves per second
// (CompProperties_TempControl.energyPerSecond, as a positive number) at the
// power it draws.
type ClimatePlant struct {
	HeatPerSecond, PowerW float64
}

func (p ClimatePlant) valid() bool {
	return finitePositive(p.HeatPerSecond) && finitePositive(p.PowerW)
}

// ClimateBuildValue is the market value of the cheapest cost list of the
// buildings the options build: a vent, a door and a wall.
type ClimateBuildValue struct{ Vent, Door, Wall float64 }

// ClimateModel is everything the game states that the model reads. The zero
// value is unknown (Valid is false): a hand-built catalog gets no advice.
type ClimateModel struct {
	// Vent, DoorOpen and DoorClosed are the exchanges of a vent (TickRare,
	// TemperatureTuning.Vent_TempEqualizeRate) and of a door (its
	// BuildingProperties interval and rate, open and closed).
	Vent, DoorOpen, DoorClosed Exchange
	// Heater and Cooler are the catalog's cheapest powered heater and its
	// most efficient cooler.
	Heater, Cooler ClimatePlant
	// StepTicks is the model's time step (GenTicks.TickRareInterval, the
	// vent's) and TicksPerSecond the game's (GenTicks.TicksPerRealSecond), the
	// unit the plants' heat per second is in.
	StepTicks      int
	TicksPerSecond float64
	// SwingC is the sun cycle's amplitude, TemperatureTuning.DailyTempVariationAmplitude.
	SwingC float64
	Build  ClimateBuildValue
}

// Valid reports a model with every number present.
func (m ClimateModel) Valid() bool {
	return m.Vent.valid() && m.DoorOpen.valid() && m.DoorClosed.valid() && m.Heater.valid() && m.Cooler.valid() &&
		m.StepTicks > 0 && finitePositive(m.TicksPerSecond) && !math.IsNaN(m.SwingC) && !math.IsInf(m.SwingC, 0)
}

// Opening is one way a room is open to its neighbour.
type Opening string

const (
	// OpeningVent is a vent in the wall (the door beside it stays closed).
	OpeningVent Opening = "vent"
	// OpeningDoorOpen is a door held open and OpeningDoorClosed one held shut.
	OpeningDoorOpen   Opening = "door_open"
	OpeningDoorClosed Opening = "door_closed"
	// OpeningAirlock is two doors around a one-cell vestibule, never open at
	// once: the traffic opens one at a time.
	OpeningAirlock Opening = "airlock"
)

// OpeningOrder is the order options are listed and ties are broken in.
var OpeningOrder = []Opening{OpeningVent, OpeningDoorOpen, OpeningDoorClosed, OpeningAirlock}

// Band is the temperature range a room is held in; an open end is infinite.
type Band struct{ MinC, MaxC float64 }

// ClimateSide is what is beyond an opening: the outdoors, following the curve,
// or a room held at HeldC with Cells floor cells.
type ClimateSide struct {
	Outdoors bool
	HeldC    float64
	Cells    int
}

// ClimateLink is one opening of a room.
type ClimateLink struct {
	Opening Opening
	Side    ClimateSide
}

// ClimateRoom is a roofed room: its floor cells, band and openings.
type ClimateRoom struct {
	Cells int
	Band  Band
	Links []ClimateLink
}

// ClimateWatts is the power a room's openings cost over a year: the mean and
// peak electrical watts of the heaters and coolers that hold its band, and the
// mean of each.
type ClimateWatts struct {
	MeanW, PeakW, HeatingW, CoolingW float64
}

// ErrClimate marks a model, room or curve the model cannot price.
var ErrClimate = errors.New("climate model")

// conductance is the heat one opening moves per tick per degree of difference.
// traffic is the fraction of the time each airlock door is open (at most half,
// as the two are never open together).
func (m ClimateModel) conductance(l ClimateLink, cells int, traffic float64) float64 {
	if !l.Side.Outdoors && l.Side.Cells > 0 {
		cells = min(cells, l.Side.Cells)
	}
	switch l.Opening {
	case OpeningVent:
		return m.Vent.conductance(cells)
	case OpeningDoorOpen:
		return m.DoorOpen.conductance(cells)
	case OpeningDoorClosed:
		return m.DoorClosed.conductance(cells)
	case OpeningAirlock:
		open, closed := m.DoorOpen.conductance(airlockVestibuleCells), m.DoorClosed.conductance(airlockVestibuleCells)
		f := math.Max(0, math.Min(traffic, 0.5))
		return 2*f*series(open, closed) + (1-2*f)*series(closed, closed)
	}
	return 0
}

// series is the conductance of two openings one after the other.
func series(a, b float64) float64 { return a * b / (a + b) }

// ClimateLoad prices a room over one day-resolved year of the outdoor curve
// (twelve means, C): at each step the room floats to the conductance-weighted
// mean of what is beyond its openings, is held at the band edge it floats
// past, and the heat that costs is turned into heater or cooler watts.
func ClimateLoad(m ClimateModel, curve []float64, room ClimateRoom, traffic float64) (ClimateWatts, error) {
	if !m.Valid() {
		return ClimateWatts{}, fmt.Errorf("%w: model has unknown numbers", ErrClimate)
	}
	if len(curve) != 12 || slices.ContainsFunc(curve, func(v float64) bool { return math.IsNaN(v) || math.IsInf(v, 0) }) {
		return ClimateWatts{}, fmt.Errorf("%w: outdoor curve is not twelve finite means", ErrClimate)
	}
	if room.Cells <= 0 || math.IsNaN(room.Band.MinC) || math.IsNaN(room.Band.MaxC) || room.Band.MinC > room.Band.MaxC {
		return ClimateWatts{}, fmt.Errorf("%w: room has %d cells and band %v", ErrClimate, room.Cells, room.Band)
	}
	g := make([]float64, len(room.Links))
	for i, l := range room.Links {
		g[i] = m.conductance(l, room.Cells, traffic)
	}
	var out ClimateWatts
	steps := 0
	for tick := 0; tick < domain.TicksPerYear; tick += m.StepTicks {
		steps++
		var flow, total float64
		for i, l := range room.Links {
			side := l.Side.HeldC
			if l.Side.Outdoors {
				side = outdoorAt(curve, m.SwingC, tick)
			}
			flow += g[i] * side
			total += g[i]
		}
		if total == 0 {
			continue
		}
		// heat and cool are the heat per tick (cell-degrees) held at the edge.
		var heat, cool float64
		switch floating := flow / total; {
		case floating < room.Band.MinC:
			heat = room.Band.MinC*total - flow
		case floating > room.Band.MaxC:
			cool = flow - room.Band.MaxC*total
		}
		heatingW := heat * m.TicksPerSecond / m.Heater.HeatPerSecond * m.Heater.PowerW
		coolingW := cool * m.TicksPerSecond / m.Cooler.HeatPerSecond * m.Cooler.PowerW
		out.HeatingW += heatingW
		out.CoolingW += coolingW
		out.PeakW = math.Max(out.PeakW, heatingW+coolingW)
	}
	out.HeatingW /= float64(steps)
	out.CoolingW /= float64(steps)
	out.MeanW = out.HeatingW + out.CoolingW
	return out, nil
}

// outdoorAt is the outdoor temperature at a tick: the twelfths' means
// interpolated linearly between their midpoints (around the year), plus the
// diurnal swing.
func outdoorAt(temps []float64, swingC float64, tick int) float64 {
	const twelfths = 12
	year := tick % domain.TicksPerYear
	pos := float64(year)/(float64(domain.TicksPerYear)/twelfths) - 0.5
	if pos < 0 {
		pos += twelfths
	}
	i := int(pos)
	frac := pos - float64(i)
	mean := temps[i%twelfths]*(1-frac) + temps[(i+1)%twelfths]*frac
	dayPct := float64(tick%domain.TicksPerDay) / domain.TicksPerDay
	return mean + math.Cos(2*math.Pi*(dayPct+climateTable.SunPhase))*swingC
}

// BuildW is the one-time cost of a building list as sustained watts: the cost
// in steel (its market value over steel's) charged at the layout's own rate, the
// site pricing's (SiteTypeWeights: Construction per 100 steel a day, amortised
// over one 60-day year, against Power per kilowatt averaged over the day).
func BuildW(w SiteTypeWeights, value, steelValue float64) float64 {
	const steelPerCharge, wattsPerKilowatt = 100, 1000
	if !finitePositive(steelValue) || !finitePositive(w.Power) {
		return 0
	}
	return value / steelValue / steelPerCharge * w.Construction / w.Power * wattsPerKilowatt
}

// buildValue is what an opening costs to build beyond the door that stands
// there anyway: a vent, or an airlock's second door and two walls.
func (b ClimateBuildValue) buildValue(o Opening) float64 {
	switch o {
	case OpeningVent:
		return b.Vent
	case OpeningAirlock:
		return b.Door + 2*b.Wall
	}
	return 0
}

// ClimateOption is one opening's price: the operating watts and the build cost
// as watts; TotalW is their sum, the number options are compared by.
type ClimateOption struct {
	Opening Opening
	Watts   ClimateWatts
	BuildW  float64
	TotalW  float64
}

// ClimateOptions prices each of the openings a room could have toward side,
// beside the links it already has, cheapest first-listed on a tie. An opening
// the caller leaves out (an unresearched vent) is absent, not a veto.
func ClimateOptions(m ClimateModel, w SiteTypeWeights, steelValue float64, curve []float64, room ClimateRoom, side ClimateSide, traffic float64, openings []Opening) ([]ClimateOption, error) {
	out := make([]ClimateOption, 0, len(openings))
	for _, o := range openings {
		r := room
		r.Links = append(slices.Clone(room.Links), ClimateLink{Opening: o, Side: side})
		watts, err := ClimateLoad(m, curve, r, traffic)
		if err != nil {
			return nil, err
		}
		build := BuildW(w, m.Build.buildValue(o), steelValue)
		out = append(out, ClimateOption{Opening: o, Watts: watts, BuildW: build, TotalW: watts.MeanW + build})
	}
	return out, nil
}

// BestClimateOption is the cheapest option by total watts, the first listed on
// a tie; false for none.
func BestClimateOption(options []ClimateOption) (ClimateOption, bool) {
	best := -1
	for i, o := range options {
		if best < 0 || o.TotalW < options[best].TotalW {
			best = i
		}
	}
	if best < 0 {
		return ClimateOption{}, false
	}
	return options[best], true
}

// BestCooler is the catalog's cooling station that draws the least power per
// heat moved, false when it has none.
func (c StationCatalog) BestCooler() (string, bool) { return bestCooler(c) }

// GroupClimate is the climate answer beside a cold group's cooling station: the
// group's room priced against the outdoors through each opening.
type GroupClimate struct {
	Cells   int
	Band    Band
	Options []ClimateOption
	Best    Opening
}

// groupClimate is the climate answer beside a cold group's cooling station:
// nil for a group that does not require cold, or while the curve, the
// full-rot temperature, the model or steel's value is unknown. The band is
// everything under the full-rot temperature (slows rot; it does not stop it).
func groupClimate(f StationFacts, g RoomGroup) *GroupClimate {
	curve, known := f.OutdoorCurve.Value()
	m := f.Catalog.Climate
	if !known || f.FullRotRateC <= 0 || !slices.Contains(g.Requires, EnvCold) || !m.Valid() {
		return nil
	}
	steel, err := f.Items.MarketValue(siteSteelUnit)
	if err != nil {
		return nil
	}
	band := Band{MinC: math.Inf(-1), MaxC: f.FullRotRateC}
	options, err := ClimateOptions(m, DefaultSiteTypeWeights(), steel, curve, ClimateRoom{Cells: g.Area, Band: band}, ClimateSide{Outdoors: true}, 0, OpeningOrder)
	if err != nil {
		return nil
	}
	out := &GroupClimate{Cells: g.Area, Band: band, Options: options}
	if best, ok := BestClimateOption(options); ok {
		out.Best = best.Opening
	}
	return out
}
