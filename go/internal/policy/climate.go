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
// The heat given off inside the room (#2915) is in: each station with a
// CompProperties_HeatPusher (stoves, smelters, torches) pushes heatPerSecond
// into its room whenever the room's temperature lies between its
// heatPushMinTemperature and heatPushMaxTemperature (CompHeatPusher.ShouldPushHeatNow;
// the powered subclass adds only "has power and fuel", always true of a station
// the colony runs). That load is what a vent sheds faster than a closed door:
// it wins where the load pushes the room out of its band. A cooler's hot side
// (Building_Cooler pushes 1.25 times its output) stands on the far side of the
// wall, so it loads the neighbour, not the room it cools.
//
// Left out, because every option pays it alike: heat leaking through walls and
// roofs, weather offsets and drift inside a twelfth of the year. The room has no
// thermal inertia: at each step of a day resolved year it is held where the
// openings and the heat inside would float it, clamped into the band, and the
// power is the heat that holding it costs.

// ClimateTable is the model's one judgment table, registered in DefTables so a
// renamed def fails the dangling-reference gate. The defs are the buildings the
// options are priced in; SunPhase is the phase of GenTemperature.OffsetFromSunCycle,
// cos(2pi(dayPct+SunPhase)) times TemperatureTuning.DailyTempVariationAmplitude:
// a literal in the game's code that no def or constant states. RaceDef and its
// two stats are where a room people live in takes its band (the race's
// comfortable range; the stat def's own base is the unclothed 0..40 and not the
// range a colonist is comfortable in). A neighbouring room is held at the
// default target of the catalog's heater (ClimateModel.HeldC): the game's own
// default for a heated room.
type ClimateTable struct {
	VentDef, DoorDef, WallDef  string
	RaceDef                    string
	ComfyMinStat, ComfyMaxStat string
	SunPhase                   float64
	// CellMoveTicks is the ticks a pawn takes to walk one cell, the move cost
	// the pathfinder charges (pathMoveCardinal): a pawn walks two cells in a
	// door's window, into the door's cell and out of it. Pawns differ in speed
	// and no def states one, so it is a judgment.
	CellMoveTicks float64
}

var climateTable = ClimateTable{VentDef: "Vent", DoorDef: ShellDoorDefinition, WallDef: ShellWallDefinition, RaceDef: "Human", ComfyMinStat: "ComfyTemperatureMin", ComfyMaxStat: "ComfyTemperatureMax", SunPhase: 0.32, CellMoveTicks: pathMoveCardinal}

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

// DoorTiming is how long a door pawns use stays open for one passage, from the
// door's def and the game's constants. OpenTicks is Building_Door.TicksToOpenNow
// (OpenTicks 45 over the def's DoorOpenSpeed, times the unpowered factor): the
// pawn waits that long at a closed door, which exchanges heat from the first
// tick. CloseDelayTicks is the delay after the last pawn leaves
// (CloseDelayTicks 110 times the def's unpowered close factor). MoveTicks is the
// walk through the door's cell, two cell moves (climateTable.CellMoveTicks).
type DoorTiming struct {
	OpenTicks, CloseDelayTicks, MoveTicks float64
}

func (t DoorTiming) valid() bool {
	return finitePositive(t.OpenTicks) && finitePositive(t.CloseDelayTicks) && t.MoveTicks >= 0 && !math.IsInf(t.MoveTicks, 0)
}

// window is the ticks one passage holds a door open: the wait, the walk
// through and the close delay.
func (t DoorTiming) window() float64 { return t.OpenTicks + t.MoveTicks + t.CloseDelayTicks }

// openFraction is the fraction of the time covered by windows of the given
// ticks when passages a day each start one (a Poisson stream: windows that
// overlap share the open time).
func openFraction(passages, ticks float64) float64 {
	if !(passages > 0) || math.IsInf(passages, 0) || !(ticks > 0) {
		return 0
	}
	return 1 - math.Exp(-passages*ticks/domain.TicksPerDay)
}

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
	// Door is the time one passage holds a door open.
	Door DoorTiming
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
	// Living is the band a room people live in is held in (the race's
	// comfortable range) and HeldC the temperature a heated neighbour is held
	// at (the heater's default target). Both are zero for a catalog that
	// cannot state them (HasLiving false); the tiler needs them, the cold
	// group's advice does not.
	Living Band
	HeldC  float64
}

// HasLiving reports a model that states the living band and the temperature a
// heated neighbour is held at.
func (m ClimateModel) HasLiving() bool {
	return m.Valid() && m.Living.MinC < m.Living.MaxC && !math.IsInf(m.Living.MinC, 0) && !math.IsInf(m.Living.MaxC, 0) &&
		!math.IsNaN(m.HeldC) && !math.IsInf(m.HeldC, 0)
}

// HeatSource is the heat a station gives off into its room: HeatPerSecond
// while the room's temperature is between MinC and MaxC (exclusive), as
// CompProperties_HeatPusher states. Negative heat cools (the passive cooler).
type HeatSource struct {
	HeatPerSecond, MinC, MaxC float64
}

// Pushes reports a source that gives off any heat.
func (h HeatSource) Pushes() bool { return h.HeatPerSecond != 0 }

// Valid reports a model with every number present.
func (m ClimateModel) Valid() bool {
	return m.Vent.valid() && m.DoorOpen.valid() && m.DoorClosed.valid() && m.Door.valid() && m.Heater.valid() && m.Cooler.valid() &&
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
	// OpeningDoor is a door pawns use: open for the share of the time the
	// passages through it hold it, shut otherwise. It is priced beside an airlock (OpeningOrder, the
	// list of the vent site, leaves it out).
	OpeningDoor Opening = "door"
	// OpeningAirlock is two doors around a one-cell vestibule. A pawn opens
	// them one after the other, and the inner one opens while the outer is
	// still in its close delay, so each passage has a window with both open.
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
	// Heat is what the room's own stations give off.
	Heat []HeatSource
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
// passages is the pawn passages a day through the opening (a door's or an
// airlock's; the others ignore it).
func (m ClimateModel) conductance(l ClimateLink, cells int, passages float64) float64 {
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
	case OpeningDoor:
		f := openFraction(passages, m.Door.window())
		return f*m.DoorOpen.conductance(cells) + (1-f)*m.DoorClosed.conductance(cells)
	case OpeningAirlock:
		return m.airlockConductance(passages)
	}
	return 0
}

// airlockConductance is the airlock's conductance with passages a day. A
// passage opens the outer door for the door's window; the pawn walks to the
// vestibule one wait and one door-cell walk later, where the inner door opens
// a close delay before the outer shuts. The two are open together for that
// close delay (the overlap) and each alone for the rest of its window. The
// time splits into overlap, one door open (the other closed) and both closed,
// each stream of windows a Poisson cover as a door's.
//
// With both open, the one-cell vestibule sits between two events of one
// interval, each moving the fraction f of the way to its side, so the heat
// through per event pair is 1/(2-f) of one open door's per event: 2/3 at
// f=1/2, where a one-cell room moves min(rate, 1)/2. One open door leaves the
// closed one's leak in series.
func (m ClimateModel) airlockConductance(passages float64) float64 {
	open, closed := m.DoorOpen.conductance(airlockVestibuleCells), m.DoorClosed.conductance(airlockVestibuleCells)
	f := math.Min(m.DoorOpen.Rate, airlockVestibuleCells) / 2
	both := openFraction(passages, m.Door.CloseDelayTicks)
	one := math.Min(math.Max(0, 2*(openFraction(passages, m.Door.window())-both)), 1-both)
	return both*open/(2-f) + one*series(open, closed) + (1-both-one)*series(closed, closed)
}

// series is the conductance of two openings one after the other.
func series(a, b float64) float64 { return a * b / (a + b) }

// ClimateLoad prices a room over one day-resolved year of the outdoor curve
// (twelve means, C): at each step the room floats to the conductance-weighted
// mean of what is beyond its openings, is held at the band edge it floats
// past, and the heat that costs is turned into heater or cooler watts.
func ClimateLoad(m ClimateModel, curve []float64, room ClimateRoom, passages float64) (ClimateWatts, error) {
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
		g[i] = m.conductance(l, room.Cells, passages)
	}
	inside := newInsideHeat(room.Heat, m.TicksPerSecond)
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
		// heat and cool are the heat per tick (cell-degrees) held at the edge,
		// beyond what the room's own stations give off at that temperature.
		var heat, cool float64
		switch floating := inside.floats(flow, total); {
		case floating < room.Band.MinC:
			heat = math.Max(0, room.Band.MinC*total-flow-inside.pushAt(room.Band.MinC))
		case floating > room.Band.MaxC:
			cool = math.Max(0, flow+inside.pushAt(room.Band.MaxC)-room.Band.MaxC*total)
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

// insideHeat is a room's stations' heat as the model reads it: the heat per
// tick each gives off between its limits, cut into the temperature intervals
// the limits bound so that a step finds where the room floats without sorting.
type insideHeat struct {
	sources []tickHeat
	// pieces are the intervals between the limits, lowest first, with the
	// heat given off throughout each.
	pieces []heatPiece
}

type tickHeat struct{ perTick, min, max float64 }

type heatPiece struct{ lo, hi, push float64 }

func newInsideHeat(sources []HeatSource, ticksPerSecond float64) insideHeat {
	var h insideHeat
	edges := []float64{math.Inf(-1), math.Inf(1)}
	for _, s := range sources {
		if !s.Pushes() || math.IsNaN(s.HeatPerSecond) || math.IsNaN(s.MinC) || math.IsNaN(s.MaxC) {
			continue
		}
		h.sources = append(h.sources, tickHeat{s.HeatPerSecond / ticksPerSecond, s.MinC, s.MaxC})
		edges = append(edges, s.MinC, s.MaxC)
	}
	if len(h.sources) == 0 {
		return h
	}
	slices.Sort(edges)
	edges = slices.Compact(edges)
	for i := 0; i+1 < len(edges); i++ {
		p := heatPiece{lo: edges[i], hi: edges[i+1]}
		for _, s := range h.sources {
			if s.min <= p.lo && s.max >= p.hi {
				p.push += s.perTick
			}
		}
		h.pieces = append(h.pieces, p)
	}
	return h
}

// pushAt is the heat per tick given off with the room held at c.
func (h insideHeat) pushAt(c float64) float64 {
	var out float64
	for _, s := range h.sources {
		if c > s.min && c < s.max {
			out += s.perTick
		}
	}
	return out
}

// floats is the temperature the room settles at when its openings carry flow
// (conductance times the temperature beyond, summed) over total conductance
// and its stations give off heat inside their limits: the first temperature
// where the heat given off at it holds the room there, else the limit a
// station reaches and switches off at (the room sits there, the station
// working part of the time).
func (h insideHeat) floats(flow, total float64) float64 {
	for i, p := range h.pieces {
		t := (flow + p.push) / total
		if t > p.lo && t < p.hi {
			return t
		}
		if t >= p.hi && i+1 < len(h.pieces) && (flow+h.pieces[i+1].push)/total <= p.hi {
			return p.hi
		}
	}
	return flow / total
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
// site pricing's (SiteTypeWeights: Construction per 100 steel a day against
// Power per kilowatt averaged over the day). The weights are per day and
// already amortised: Construction's 2.6 is 100 steel (190 silver) over one
// 60-day year (DefaultSiteTypeWeights), so the formula divides by no horizon.
func BuildW(w SiteTypeWeights, value, steelValue float64) float64 {
	const steelPerCharge = 100
	if !finitePositive(steelValue) || !finitePositive(w.Power) {
		return 0
	}
	return unitsW(w, value/steelValue/steelPerCharge*w.Construction)
}

// unitsW turns site-type units (a normal-soil cell's harvest per grow day) into
// sustained watts: Power units are a kilowatt averaged over the day.
func unitsW(w SiteTypeWeights, units float64) float64 {
	const wattsPerKilowatt = 1000
	return units / w.Power * wattsPerKilowatt
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
func ClimateOptions(m ClimateModel, w SiteTypeWeights, steelValue float64, curve []float64, room ClimateRoom, side ClimateSide, passages float64, openings []Opening) ([]ClimateOption, error) {
	out := make([]ClimateOption, 0, len(openings))
	for _, o := range openings {
		r := room
		r.Links = append(slices.Clone(room.Links), ClimateLink{Opening: o, Side: side})
		watts, err := ClimateLoad(m, curve, r, passages)
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

// GroupHeat is the heat the group's stations give off into its room, one
// source per station def at its count.
func (c StationCatalog) GroupHeat(g RoomGroup) []HeatSource {
	var out []HeatSource
	for _, s := range g.Stations {
		st, ok := c.Station(s.Def)
		if !ok || !st.Heat.Pushes() || s.Count <= 0 {
			continue
		}
		h := st.Heat
		h.HeatPerSecond *= float64(s.Count)
		out = append(out, h)
	}
	return out
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
	options, err := ClimateOptions(m, DefaultSiteTypeWeights(), steel, curve, ClimateRoom{Cells: g.Area, Band: band, Heat: f.Catalog.GroupHeat(g)}, ClimateSide{Outdoors: true}, 0, OpeningOrder)
	if err != nil {
		return nil
	}
	out := &GroupClimate{Cells: g.Area, Band: band, Options: options}
	if best, ok := BestClimateOption(options); ok {
		out.Best = best.Opening
	}
	return out
}
