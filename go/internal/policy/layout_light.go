package policy

import (
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
// A lit cell is one within a lamp's reach (the distance a standing lamp
// holds above LitGlow, lampReach, layout_infestation.go); glow beyond it is not
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
// its wall keeps the light in. Glow at path distance d is a blend of 1-d/radius
// and 1/d^2, so the distance that holds LitGlow scales with the radius: the wall
// lamp's reach is lampReach times its radius over the standing lamp's (11/12,
// within 0.1% of solving the falloff for both).
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
// is the lighting policy's LitGlow).
// Research: https://github.com/davidarcher/rimgovernor/issues/2945 (first comment).
type LightTable struct {
	LampDef, WallLampDef string
	DarkSpeed            float64
}

var lightTable = LightTable{LampDef: "StandingLamp", WallLampDef: "WallLamp", DarkSpeed: 0.8}

// LightDefs is the buildings the lamp pricing places.
func LightDefs() LightTable { return lightTable }

// LampModel is one lamp's catalog numbers: the power it draws, the market value
// of its cheapest cost list and its glow radius. The zero value is unknown.
type LampModel struct{ PowerW, Build, Radius float64 }

func (m LampModel) priced() bool { return finitePositive(m.PowerW) && finitePositive(m.Build) }

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
// CellWalkDays the work-days a colonist spends walking one cell. WallLampW and
// WallReach are the wall lamp's price and reach in cells; zero while the wall
// lamp is unknown, which leaves it out of the options.
type LightPrice struct {
	WorkDayW, LampW, CellWalkDays float64
	WallLampW, WallReach          float64
}

// Valid reports a price with every number known.
func (p LightPrice) Valid() bool {
	return finitePositive(p.WorkDayW) && finitePositive(p.LampW) && finitePositive(p.CellWalkDays)
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
	}
	// The wall lamp's reach is the standing lamp's scaled by glow radius (see
	// above); without both radii it cannot be placed, and is left out.
	if wall := in.Model.Wall; wall.priced() && finitePositive(wall.Radius) && finitePositive(in.Model.Standing.Radius) {
		p.WallLampW = wall.PowerW + BuildW(w, wall.Build, in.SteelValue)
		p.WallReach = lampReach * wall.Radius / in.Model.Standing.Radius
	}
	return p
}

// stationLightPrice prices the colony's darkness from its station facts: a
// colonist's upkeep is the food census's nutrition over the colonists, a unit
// the best crop's nutrition a day on normal soil (the cheapest food a cell can
// make, which keeps the work-day a lower bound). Invalid while the census, the
// colonists, a crop or the lamp is unknown.
func stationLightPrice(f StationFacts, steel float64) LightPrice {
	supply, sk := f.Food.Value()
	colonists, ck := f.Colonists.Value()
	nutrition, nk := HumanNutritionPerDay(supply)
	if !sk || !ck || colonists <= 0 || !nk {
		return LightPrice{}
	}
	unit := 0.0
	for _, crop := range f.Crops {
		unit = max(unit, cropRate(crop, 1))
	}
	return NewLightPrice(LightPriceInput{Model: f.Catalog.Light, Climate: f.Catalog.Climate, SteelValue: steel, UpkeepPerDay: nutrition / float64(colonists), UnitNutrition: unit})
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
		out[i] = lampCand{PlannedLamp: PlannedLamp{Def: lightTable.LampDef, Cell: s}, W: p.LampW, Reach: lampReach}
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
// hallway's lamps are standing lamps: the plan does not say which of its edge
// cells are plain wall.
func hallwayLightW(rect Rectangle, moves map[domain.Cell]float64, p LightPrice) float64 {
	var cells []DarkCell
	spots := rectCells(rect)
	for _, c := range spots {
		if m := moves[c]; m > 0 {
			cells = append(cells, DarkCell{Cell: c, W: p.WalkCellW(m)})
		}
	}
	lamps, left := planLamps(cells, p.standingCands(spots))
	return lampsW(lamps) + left
}

func lampsW(lamps []lampCand) float64 {
	total := 0.0
	for _, l := range lamps {
		total += l.W
	}
	return total
}

// notPlainWall are the cells a wall lamp cannot hang on: the floor of every room
// and hallway, and the wall cells another wall building holds (only a wall and
// natural rock have supportsWallAttachments): the doors, links and entrances of
// the plan and the vents the tiler placed (#2915). A lamp that loses its wall to
// one is priced as the standing lamp that answers it, not refused.
func (t *tiler) notPlainWall() map[domain.Cell]bool {
	p := t.out.Plan
	out := map[domain.Cell]bool{}
	for _, r := range p.AllRooms() {
		for _, c := range r.Region.Cells() {
			out[c] = true
		}
		out[r.Door] = true
		for _, d := range r.Doors {
			out[d.Cell] = true
		}
	}
	for _, rect := range spineRects(p.Hallways()) {
		for _, c := range rectCells(rect) {
			out[c] = true
		}
	}
	for _, l := range p.Links {
		out[l.Cell] = true
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
func wallLampCands(spots []domain.Cell, notWall map[domain.Cell]bool, p LightPrice) []lampCand {
	if p.WallLampW <= 0 || p.WallReach <= 0 {
		return nil
	}
	var out []lampCand
	for _, s := range spots {
		for _, dir := range directions {
			if notWall[addCell(s, dir)] {
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
// speed) and every cell the plan's trips walk; its lamps stand on floor cells no
// station or worker holds, each a standing lamp or a wall lamp hung on a plain
// wall beside it, whichever lights more dark for its cost. Lamps are placed when
// lighting the dark costs less than leaving it, never vetoed either way; a room
// under the sky gets none.
func (t *tiler) placeLamps() {
	p := t.out.Plan
	var moves map[domain.Cell]float64
	var notWall map[domain.Cell]bool
	for i := range t.rooms {
		room := p.Rooms[i]
		g := t.in.Groups[t.rooms[i].group]
		if !lightRoofed(room) {
			t.noteLamps(g.ID, ReasonTileLampsSky, TilePlace, 0, 0)
			continue
		}
		work := map[domain.Cell]bool{}
		taken := map[domain.Cell]bool{}
		for _, piece := range t.out.Pieces[room.ID] {
			for _, c := range rectCells(piece.Rect) {
				taken[c] = true
			}
			if s, ok := t.in.Catalog.Station(piece.Def); ok && s.Capacity.Kind == CapacityWorkers {
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
			notWall = t.notPlainWall()
		}
		var cells []DarkCell
		var spots []domain.Cell
		for _, c := range room.Region.Cells() {
			w := t.in.Light.WalkCellW(moves[c])
			if work[c] {
				w += t.in.Light.WorkCellW()
			}
			if w > 0 {
				cells = append(cells, DarkCell{Cell: c, W: w})
			}
			if !taken[c] && !work[c] {
				spots = append(spots, c)
			}
		}
		sortCells(spots)
		cands := t.in.Light.standingCands(spots)
		cands = append(cands, wallLampCands(spots, notWall, t.in.Light)...)
		lamps, left := planLamps(cells, cands)
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
