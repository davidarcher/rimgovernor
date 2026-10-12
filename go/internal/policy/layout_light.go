package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Lamp pricing (#2945). A roofed cell gets no sky light, so a colonist who
// works or walks on it loses speed to darkness until a lamp lights it. The
// planner weighs the two in sustained watts (the layout's own currency, see
// BuildW): the speed lost against the lamps that would cure it, chosen per room
// with the room's work cells and traffic, and never as a veto.
//
// What darkness costs, from the game (research on #2945): StatPart_Glow scales
// WorkSpeedGlobal and MoveSpeed by 0.80 at glow 0 up to 1.00 at glow 0.3
// (LitGlow), for humanlike pawns, every job alike. The other effects are not
// priced here: the mood penalty (EnvironmentDark) waits for #2878's mood
// currency, crop growth is already priced against sun lamps in PlanSiteType
// (fields stay outdoors, so a field never takes a roof or a standing lamp),
// and the rest (surgery success, infestation odds) belong to other terms.
//
// The exchange rate is the epic's decision: a colonist's work-day is worth
// their own food upkeep a day (the colony's nutrition per colonist, from the
// food-demand census), counted in site-type units against the crop's per-cell
// yield and turned into watts at SiteTypeWeights.Power, the same units BuildW
// turns a build cost into. It is a derived lower bound; no new constant.
// A lamp costs its draw (the def's power comp) plus its build cost amortised at
// the same weights. A work cell is one colonist at the bench for the whole day
// (the busiest it can be) and a walked cell costs the extra time its
// passages take at the slower speed.
//
// A lit cell is one within a lamp's reach (glowReach: the distance its glow holds
// above LitGlow, solved from the def's radius and colour); glow beyond it is not
// credited. The lamps give no heat, so they add nothing to the climate load. The
// advice sits beside the old planner, which stays authoritative until #2931;
// MaintainLighting stays the runtime reconciler for what is built.
//
// The wall lamp (#2948) is the second option. From the game (ilspycmd and
// Buildings_Furniture.xml): it is not built into a wall cell but hung on one,
// standing on a floor cell beside it (Placeworker_AttachedToWall tests the
// cardinal neighbour in the direction it faces), Standable and with no path cost,
// so it blocks nobody; the neighbour must be a Full-fill building that
// BuildingProperties.supportsWallAttachments (the wall and natural rock; not a
// door or a vent), so a door or vent cell cannot carry one. It costs 15 steel
// against the standing lamp's 20, draws the same 30 W, and glows to radius 11
// against 12. The glow is a flood fill from the lamp's cell that light blockers
// stop (ComputeGlowGridsJob), so a lamp on a room's floor lights that room and
// its wall keeps the light in.
//
// A wall lamp is not an edifice (#2951): Placeworker_AttachedToWall refuses only a
// Full-fill building on its own cell (fillPercent over 0.99: a cooler, a
// crematorium, a nutrient paste dispenser) or another attachment facing the same
// way, and CanPlaceBlueprintOver lets a non-edifice go over any building but
// natural rock. So it shares a cell with a bench, a bed or a table, and a work
// cell's interaction spot (it is Standable), and only a Full-fill piece or a piece
// of unknown def withholds the cell. A room's wall ring and a hallway's edge
// are walls only where a fenced indoor room's ring stands (plainWalls): an open
// edge, a fence and a door are not supports.
//
// Reach is solved from the game's falloff, not scaled (glowReach): the lamp's
// colour is multiplied by a blend of 1-x/radius and 1/x^2 at x = 1 + path
// distance, truncated per channel, and ground glow is the brightest channel over
// 255 times 3.6. Solving that for LitGlow gives about 9.0 cells from a standing
// lamp and 8.2 from a wall lamp along an axis (the wiki's "9 to 8 tiles"), and the
// planner's Euclidean radius is that over the worst octile stretch of a
// diagonal path.
//
// Choosing between them is the same watt price: each step takes the lamp,
// of either kind at any spot, whose lit dark cost less its own cost is largest.
// A wall cell another wall building holds (a door, a link, a vent) is not a
// support, so a wall lamp there is an absent option and the standing lamp
// answers: contention is priced by what the substitute costs, never refused.

// LightTable is the lamp pricing's table, registered in DefTables so a renamed
// def fails the dangling-reference gate. LampDef is the standing lamp the plan
// places and WallLampDef the one hung on a wall; DarkSpeed is StatPart_Glow's
// factor at glow 0 for WorkSpeedGlobal and MoveSpeed (the end of the curve that
// the def's curve states, 0.8 at glow 0 to 1 at 0.3; the glow where it reaches 1
// is the lighting policy's LitGlow). The glow constants are the game's, read
// with ilspycmd: GroundScale is GlowGrid.GroundGlowAt's 3.6 (brightest channel
// over 255, times it), FalloffBlend ComputeGlowGridsJob.SetGlowFromDist's lerp
// weight of 1/x^2 against 1-x/radius, and DiagonalCost its 141/100 per diagonal
// step.
// Research: https://github.com/davidarcher/rimgovernor/issues/2945 (first comment).
type LightTable struct {
	LampDef, WallLampDef                    string
	DarkSpeed                               float64
	GroundScale, FalloffBlend, DiagonalCost float64
}

var lightTable = LightTable{LampDef: "StandingLamp", WallLampDef: "WallLamp", DarkSpeed: 0.8, GroundScale: 3.6, FalloffBlend: 0.4, DiagonalCost: 1.41}

// glowReach is the Euclidean radius (cells) within which a lamp of the given glow
// radius and brightest colour channel keeps ground glow above LitGlow; zero when
// either is unknown or the lamp never lights its own neighbour. The flood runs
// over octile path steps, so a cell at Euclidean distance e is at most
// sqrt(1+k^2)*e path cells away (k the diagonal's extra cost, at the angle
// where the path bends most): dividing by it keeps every cell inside lit.
func glowReach(radius, brightness float64) float64 {
	if !finitePositive(radius) || !finitePositive(brightness) {
		return 0
	}
	t := lightTable
	// The channel survives int(brightness*falloff), and ground glow must pass
	// LitGlow strictly (PsychGlowAtGlow).
	need := (math.Floor(DefaultLightingPolicy().LitGlow/t.GroundScale*255) + 1) / brightness
	falloff := func(x float64) float64 { return (1-t.FalloffBlend)*(1-x/radius) + t.FalloffBlend/(x*x) }
	if falloff(1) < need {
		return 0
	}
	lo, hi := 1.0, radius // the flood stops at the radius
	if falloff(hi) >= need {
		lo = hi
	}
	for i := 0; i < 60 && hi-lo > 1e-9; i++ {
		mid := (lo + hi) / 2
		if falloff(mid) >= need {
			lo = mid
		} else {
			hi = mid
		}
	}
	return (lo - 1) / math.Sqrt(1+(t.DiagonalCost-1)*(t.DiagonalCost-1))
}

// LightDefs is the buildings the lamp pricing places.
func LightDefs() LightTable { return lightTable }

// LampModel is one lamp's catalog numbers: the power it draws, the market value
// of its cheapest cost list, its glow radius and the brightest channel of its
// glow colour. The zero value is unknown.
type LampModel struct{ PowerW, Build, Radius, Brightness float64 }

func (m LampModel) priced() bool { return finitePositive(m.PowerW) && finitePositive(m.Build) }

func (m LampModel) reach() float64 { return glowReach(m.Radius, m.Brightness) }

// LightModel is the catalog's two lamps; an unknown one is an absent option.
type LightModel struct{ Standing, Wall LampModel }

// LightSpeed is the speed factor of a pawn on ground of the given glow
// (StatPart_Glow): linear from DarkSpeed at 0 to 1 at LitGlow.
func LightSpeed(glow float64) float64 {
	lit := DefaultLightingPolicy().LitGlow
	g := min(max(glow, 0), lit) / lit
	return lightTable.DarkSpeed + (1-lightTable.DarkSpeed)*g
}

// LightPrice is what darkness and light cost, in sustained watts. WorkDayW is a
// colonist's work-day, LampW one standing lamp's draw and amortised build,
// CellWalkDays the work-days a colonist spends walking one cell. LampReach is the
// standing lamp's lit radius in cells. WallLampW and WallReach are the wall
// lamp's price and reach; zero while the wall lamp is unknown, which leaves it
// out of the options.
type LightPrice struct {
	WorkDayW, LampW, CellWalkDays, LampReach float64
	WallLampW, WallReach                     float64
}

// Valid reports a price with every number known.
func (p LightPrice) Valid() bool {
	return finitePositive(p.WorkDayW) && finitePositive(p.LampW) && finitePositive(p.CellWalkDays) && finitePositive(p.LampReach)
}

// LightPriceInput is what the price reads. UpkeepPerDay is one colonist's
// nutrition a day (the food-demand census over the colonists) and UnitNutrition
// the nutrition a day of one site-type unit (the best crop's cropRate at normal
// soil); Climate gives the game's ticks per second.
type LightPriceInput struct {
	Model         LightModel
	Climate       ClimateModel
	Weights       SiteTypeWeights
	SteelValue    float64
	UpkeepPerDay  float64
	UnitNutrition float64
}

// NewLightPrice prices darkness against light; the zero value (invalid) while
// any number is unknown.
func NewLightPrice(in LightPriceInput) LightPrice {
	w := in.Weights
	if w == (SiteTypeWeights{}) {
		w = DefaultSiteTypeWeights()
	}
	if !in.Model.Standing.priced() || !finitePositive(in.SteelValue) ||
		!finitePositive(in.UpkeepPerDay) || !finitePositive(in.UnitNutrition) || !finitePositive(in.Climate.TicksPerSecond) || !finitePositive(w.Power) {
		return LightPrice{}
	}
	p := LightPrice{
		WorkDayW:     unitsW(w, in.UpkeepPerDay/in.UnitNutrition),
		LampW:        in.Model.Standing.PowerW + BuildW(w, in.Model.Standing.Build, in.SteelValue),
		CellWalkDays: in.Climate.TicksPerSecond / colonistMoveSpeed / domain.TicksPerDay,
		LampReach:    in.Model.Standing.reach(),
	}
	// A lamp whose glow is unknown cannot be placed: the standing lamp's leaves
	// the price invalid, the wall lamp's leaves it out.
	if !p.Valid() {
		return LightPrice{}
	}
	if wall := in.Model.Wall; wall.priced() && wall.reach() > 0 {
		p.WallLampW = wall.PowerW + BuildW(w, wall.Build, in.SteelValue)
		p.WallReach = wall.reach()
	}
	return p
}

// stationLightPrice prices the colony's darkness from its station facts: a
// colonist's upkeep is the food census's nutrition over the colonists, a unit
// the best crop's nutrition a day on normal soil (the cheapest food a cell can
// make, which keeps the work-day a lower bound). Invalid while the census, the
// colonists, a crop or the lamp is unknown.
func stationLightPrice(f StationFacts, steel float64) LightPrice {
	upkeep, unit, ok := stationUpkeep(f)
	if !ok {
		return LightPrice{}
	}
	return NewLightPrice(LightPriceInput{Model: f.Catalog.Light, Climate: f.Catalog.Climate, SteelValue: steel, UpkeepPerDay: upkeep, UnitNutrition: unit})
}

// stationUpkeep is a colonist's nutrition a day (the food census over the
// colonists) and a site-type unit's nutrition a day (the best crop on normal
// soil); false while the census or the colonists are unknown. A unit of 0 is a
// colony with no crop: the caller's price stays invalid.
func stationUpkeep(f StationFacts) (upkeep, unit float64, ok bool) {
	supply, sk := f.Food.Value()
	colonists, ck := f.Colonists.Value()
	nutrition, nk := HumanNutritionPerDay(supply)
	if !sk || !ck || colonists <= 0 || !nk {
		return 0, 0, false
	}
	for _, crop := range f.Crops {
		unit = max(unit, cropRate(crop, 1))
	}
	return nutrition / float64(colonists), unit, true
}

// stationWorkDayW is a colonist's work-day in sustained watts, the lamp price's
// own (NewLightPrice): upkeep over the best crop's unit at the site-type
// weights. Zero while unknown.
func stationWorkDayW(f StationFacts) float64 {
	upkeep, unit, ok := stationUpkeep(f)
	if !ok || !finitePositive(unit) {
		return 0
	}
	w := DefaultSiteTypeWeights()
	return unitsW(w, upkeep/unit)
}

// DarkCell is a cell that costs W watts a day while it stays dark.
type DarkCell struct {
	Cell domain.Cell
	W    float64
}

// WorkCellW is what a dark work cell costs: the work speed lost over one
// colonist's work-day.
func (p LightPrice) WorkCellW() float64 { return (1 - LightSpeed(0)) * p.WorkDayW }

// WalkCellW is what a dark cell costs the movements a day through it: the extra
// time each passage takes at the dark speed, as a share of a work-day.
func (p LightPrice) WalkCellW(movements float64) float64 {
	return movements * p.CellWalkDays * (1/LightSpeed(0) - 1) * p.WorkDayW
}

// PlannedLamp is a lamp the plan places: Def the building, Cell the floor cell it
// stands on and Facing the direction of the wall it hangs on (a wall lamp's
// rotation, Placeworker_AttachedToWall); empty for a standing lamp.
type PlannedLamp struct {
	Def    string
	Cell   domain.Cell
	Facing domain.Rotation `json:",omitempty"`
}

// lampCand is one way to light from a floor cell: a lamp def, its cost in
// sustained watts and the distance it lights. Wall lamps carry the direction of
// the wall that holds them.
type lampCand struct {
	PlannedLamp
	W, Reach float64
}

// standingCands are the standing lamps that could stand on spots.
func (p LightPrice) standingCands(spots []domain.Cell) []lampCand {
	out := make([]lampCand, len(spots))
	for i, s := range spots {
		out[i] = lampCand{PlannedLamp: PlannedLamp{Def: lightTable.LampDef, Cell: s}, W: p.LampW, Reach: p.LampReach}
	}
	return out
}

// planLamps places lamps greedily from the candidates: each step takes the one
// whose newly lit dark cost, less its own cost, is largest, and only while that
// is positive (ties and losses place none). Of two lamps on one cell the cheaper
// cover wins; the candidates' order breaks ties, and a cell holds one lamp. It
// returns the lamps in the order placed with their costs and the dark cost left
// unlit.
func planLamps(cells []DarkCell, cands []lampCand) (lamps []lampCand, leftW float64) {
	lit := make([]bool, len(cells))
	taken := map[domain.Cell]bool{}
	for _, c := range cells {
		leftW += c.W
	}
	for {
		best, bestNet := -1, 0.0
		for i, l := range cands {
			if taken[l.Cell] {
				continue
			}
			gain := 0.0
			for j, c := range cells {
				if !lit[j] && distance(l.Cell, c.Cell) <= l.Reach {
					gain += c.W
				}
			}
			if net := gain - l.W; net > bestNet {
				best, bestNet = i, net
			}
		}
		if best < 0 {
			return lamps, leftW
		}
		l := cands[best]
		lamps = append(lamps, l)
		taken[l.Cell] = true
		for j, c := range cells {
			if !lit[j] && distance(l.Cell, c.Cell) <= l.Reach {
				lit[j] = true
				leftW -= c.W
			}
		}
	}
}

// lightRoofed reports a room with a roof and no sky: a fenced room that is not
// an outdoor role. An outdoor room gets the sun's light and no lamp.
func lightRoofed(r PlannedRoom) bool { return !r.Role.IsOutdoor() && !r.Unfenced() }

// hallwayLightW is the price of the dark a roofed hallway segment makes: the
// cheaper of lighting the movements through it and leaving them dark, as the
// lamps' cost plus the dark left over. Zero for a segment nobody walks. A
// hallway cell takes a wall lamp where a neighbour is a plain wall of the plan
// (walls, from plainWalls) and a standing lamp anywhere.
func hallwayLightW(rect Rectangle, moves map[domain.Cell]float64, walls func(domain.Cell) bool, p LightPrice) float64 {
	var cells []DarkCell
	spots := rectCells(rect)
	for _, c := range spots {
		if m := moves[c]; m > 0 {
			cells = append(cells, DarkCell{Cell: c, W: p.WalkCellW(m)})
		}
	}
	lamps, left := planLamps(cells, append(p.standingCands(spots), wallLampCands(spots, walls, p)...))
	return lampsW(lamps) + left
}

func lampsW(lamps []lampCand) float64 {
	total := 0.0
	for _, l := range lamps {
		total += l.W
	}
	return total
}

// plainWalls are the cells of the plan a wall lamp can hang on: the wall ring of
// every fenced indoor room (a fence, or no ring at all, supports nothing), less
// the cells another building holds (only a wall and natural rock have
// supportsWallAttachments): the floor of every room and hallway, the doors,
// links and entrances of the plan and the vents the tiler placed (#2915). A
// lamp that loses its wall to one is priced as the standing lamp that answers
// it, not refused. Natural rock supports a lamp as a wall does
// (RockBase.supportsWallAttachments): a survey rock cell outside the held
// cells counts, so a room or hallway cut into a mountain keeps its wall lamps.
func plainWalls(p LayoutPlan, rock map[domain.Cell]bool) func(domain.Cell) bool {
	held := heldWallCells(p)
	ring := map[domain.Cell]bool{}
	for _, r := range p.AllRooms() {
		if lightRoofed(r) {
			for _, c := range r.Region.Walls().Cells() {
				ring[c] = true
			}
		}
	}
	return func(c domain.Cell) bool { return !held[c] && (ring[c] || rock[c]) }
}

// surveyRock is the survey's natural rock cells.
func surveyRock(s MapSurvey) map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	for _, c := range s.Cells {
		if c.Rock {
			out[c.Cell] = true
		}
	}
	return out
}

// heldWallCells are the plan's cells that are not a bare wall: floor, doors,
// links, entrances and vents.
func heldWallCells(p LayoutPlan) map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	for _, r := range p.AllRooms() {
		for _, c := range r.Region.Cells() {
			out[c] = true
		}
		for _, c := range p.DoorsOf(r) {
			out[c] = true
		}
	}
	for _, rect := range spineRects(p.Hallways()) {
		for _, c := range rectCells(rect) {
			out[c] = true
		}
	}
	for _, c := range p.Entrances {
		out[c] = true
	}
	for _, s := range p.LiveSites(NodeVent) {
		for _, c := range s.Footprint.Cells() {
			out[c] = true
		}
	}
	return out
}

// wallLampCands are the wall lamps that could hang over floor spots: on a spot
// with a cardinal neighbour that is a plain wall, the first in the plan's
// direction order carrying the lamp. The lamp stands on the floor and takes no
// wall cell; it needs only that a wall is there.
func wallLampCands(spots []domain.Cell, isWall func(domain.Cell) bool, p LightPrice) []lampCand {
	if p.WallLampW <= 0 || p.WallReach <= 0 || isWall == nil {
		return nil
	}
	var out []lampCand
	for _, s := range spots {
		for _, dir := range directions {
			if !isWall(addCell(s, dir)) {
				continue
			}
			out = append(out, lampCand{PlannedLamp: PlannedLamp{Def: lightTable.WallLampDef, Cell: s, Facing: rotationOf(dir)}, W: p.WallLampW, Reach: p.WallReach})
			break
		}
	}
	return out
}

// Reasons of lamp decisions.
const (
	ReasonTileLamps         = "tile_lamps"
	ReasonTileLampsNotPaid  = "tile_lamps_not_paid"
	ReasonTileLampsSky      = "tile_lamps_sky"
	ReasonTileLampsUnpriced = "tile_lamps_unpriced"
)

// placeLamps lights the roofed rooms where the lamps pay. A room's priced cells
// are its work cells (a worker station's interaction cell, a full work-day of
// speed) and every cell the plan's trips walk; a standing lamp stands on a floor
// cell no station or worker holds, a wall lamp hangs on a plain wall beside any
// floor cell a Full-fill station does not hold (it shares the cell with the
// furniture), whichever lights more dark for its cost. Lamps are placed when
// lighting the dark costs less than leaving it, never vetoed either way; a room
// under the sky gets none.
func (t *tiler) placeLamps() {
	p := t.out.Plan
	var moves map[domain.Cell]float64
	var isWall func(domain.Cell) bool
	for i := range t.rooms {
		room := p.Rooms[i]
		g := t.in.Groups[t.rooms[i].group]
		if !lightRoofed(room) {
			t.noteLamps(g.ID, ReasonTileLampsSky, TilePlace, 0, 0)
			continue
		}
		work := map[domain.Cell]bool{}
		taken := map[domain.Cell]bool{}
		filled := map[domain.Cell]bool{} // cells a wall lamp cannot share
		for _, piece := range t.out.Pieces[room.ID] {
			s, known := t.in.Catalog.Station(piece.Def)
			for _, c := range rectCells(piece.Rect) {
				taken[c] = true
				filled[c] = filled[c] || !known || s.FillsCell
			}
			if known && s.Capacity.Kind == CapacityWorkers {
				if c, ok := piece.Interaction(); ok && room.Region.Contains(c) {
					work[c] = true
				}
			}
		}
		if !t.in.Light.Valid() {
			if len(work) > 0 {
				t.noteLamps(g.ID, ReasonTileLampsUnpriced, TileUnsatisfied, 0, 0)
			}
			continue
		}
		if moves == nil {
			moves = tripCellMovements(p)
			isWall = plainWalls(p, surveyRock(t.in.Survey))
		}
		var cells []DarkCell
		for _, c := range room.Region.Cells() {
			w := t.in.Light.WalkCellW(moves[c])
			if work[c] {
				w += t.in.Light.WorkCellW()
			}
			if w > 0 {
				cells = append(cells, DarkCell{Cell: c, W: w})
			}
		}
		lamps, left := planLamps(cells, t.in.Light.roomCands(room.Region.Cells(), taken, work, filled, isWall))
		total := 0.0
		for _, c := range cells {
			total += c.W
		}
		if len(lamps) == 0 {
			if total > 0 {
				t.noteLamps(g.ID, ReasonTileLampsNotPaid, TileUnsatisfied, total, t.in.Light.cheapestLampW())
			}
			continue
		}
		if t.out.Lamps == nil {
			t.out.Lamps = map[int32][]PlannedLamp{}
		}
		for _, l := range lamps {
			t.out.Lamps[room.ID] = append(t.out.Lamps[room.ID], l.PlannedLamp)
		}
		t.noteLamps(g.ID, ReasonTileLamps, TilePlace, left, lampsW(lamps))
	}
}

// roomCands are the lamps a room's floor offers: a standing lamp on each cell no
// piece or worker holds, and a wall lamp on each cell no Full-fill piece holds
// (it shares the cell with the furniture and the work cell's standing spot)
// that has a plain wall beside it.
func (p LightPrice) roomCands(floor []domain.Cell, held, work, full map[domain.Cell]bool, isWall func(domain.Cell) bool) []lampCand {
	var standing, wall []domain.Cell
	for _, c := range floor {
		if !held[c] && !work[c] {
			standing = append(standing, c)
		}
		if !full[c] {
			wall = append(wall, c)
		}
	}
	sortCells(standing)
	sortCells(wall)
	return append(p.standingCands(standing), wallLampCands(wall, isWall, p)...)
}

// cheapestLampW is the price of the cheapest lamp of either kind.
func (p LightPrice) cheapestLampW() float64 {
	if p.WallLampW > 0 {
		return min(p.LampW, p.WallLampW)
	}
	return p.LampW
}

func (t *tiler) noteLamps(group int, reason, verdict string, darkW, lampW float64) {
	t.out.Decisions = append(t.out.Decisions, TileDecision{Group: group, Other: -1, Verdict: verdict, Reason: reason, DarkW: darkW, LampW: lampW})
}

// PlannedLampsAt is the lamps the plan places in the room whose floor holds c,
// in the order the plan placed them (the most valuable first); nil when c is in
// no room or its room was planned no lamp.
func (p LayoutPlan) PlannedLampsAt(c domain.Cell) []PlannedLamp {
	for _, r := range p.AllRooms() {
		if r.Region.Contains(c) {
			return p.Lamps[r.ID]
		}
	}
	return nil
}
