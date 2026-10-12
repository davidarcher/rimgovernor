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
// A lit cell is one within lampReach of a lamp (the distance a standing lamp
// holds above LitGlow, layout_infestation.go); glow beyond it is not credited.
// The standing lamp gives no heat, so lamps add nothing to the climate load. The
// advice sits beside the old planner, which stays authoritative until #2931;
// MaintainLighting stays the runtime reconciler for what is built.

// LightTable is the lamp pricing's table, registered in DefTables so a renamed
// def fails the dangling-reference gate. LampDef is the building the plan
// places; DarkSpeed is StatPart_Glow's factor at glow 0 for WorkSpeedGlobal and
// MoveSpeed (the end of the curve that the def's curve states, 0.8 at glow 0 to
// 1 at 0.3; the glow where it reaches 1 is the lighting policy's LitGlow).
// Research: https://github.com/davidarcher/rimgovernor/issues/2945 (first comment).
type LightTable struct {
	LampDef   string
	DarkSpeed float64
}

var lightTable = LightTable{LampDef: "StandingLamp", DarkSpeed: 0.8}

// LightDefs is the building the lamp pricing places.
func LightDefs() LightTable { return lightTable }

// LightModel is the lamp's catalog numbers: the power it draws and the market
// value of its cheapest cost list. The zero value is unknown.
type LightModel struct{ LampPowerW, LampBuild float64 }

// LightSpeed is the speed factor of a pawn on ground of the given glow
// (StatPart_Glow): linear from DarkSpeed at 0 to 1 at LitGlow.
func LightSpeed(glow float64) float64 {
	lit := DefaultLightingPolicy().LitGlow
	g := min(max(glow, 0), lit) / lit
	return lightTable.DarkSpeed + (1-lightTable.DarkSpeed)*g
}

// LightPrice is what darkness and light cost, in sustained watts. WorkDayW is a
// colonist's work-day, LampW one lamp's draw and amortised build, CellWalkDays
// the work-days a colonist spends walking one cell.
type LightPrice struct {
	WorkDayW, LampW, CellWalkDays float64
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
	if !finitePositive(in.Model.LampPowerW) || !finitePositive(in.Model.LampBuild) || !finitePositive(in.SteelValue) ||
		!finitePositive(in.UpkeepPerDay) || !finitePositive(in.UnitNutrition) || !finitePositive(in.Climate.TicksPerSecond) || !finitePositive(w.Power) {
		return LightPrice{}
	}
	return LightPrice{
		WorkDayW:     unitsW(w, in.UpkeepPerDay/in.UnitNutrition),
		LampW:        in.Model.LampPowerW + BuildW(w, in.Model.LampBuild, in.SteelValue),
		CellWalkDays: in.Climate.TicksPerSecond / colonistMoveSpeed / domain.TicksPerDay,
	}
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

// planLamps places lamps on spots, greedily: each step puts the lamp that
// lights the most dark cost, and only while that is more than a lamp costs
// (ties and losses place none). It returns the lamps in the order placed and
// the dark cost left unlit. The spots' order breaks ties.
func planLamps(cells []DarkCell, spots []domain.Cell, lampW float64) (lamps []domain.Cell, leftW float64) {
	lit := make([]bool, len(cells))
	for _, c := range cells {
		leftW += c.W
	}
	for {
		best, bestGain := -1, lampW
		for i, s := range spots {
			gain := 0.0
			for j, c := range cells {
				if !lit[j] && distance(s, c.Cell) <= lampReach {
					gain += c.W
				}
			}
			if gain > bestGain {
				best, bestGain = i, gain
			}
		}
		if best < 0 {
			return lamps, leftW
		}
		lamps = append(lamps, spots[best])
		for j, c := range cells {
			if !lit[j] && distance(spots[best], c.Cell) <= lampReach {
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
// lamps' cost plus the dark left over. Zero for a segment nobody walks.
func hallwayLightW(rect Rectangle, moves map[domain.Cell]float64, p LightPrice) float64 {
	var cells []DarkCell
	spots := rectCells(rect)
	for _, c := range spots {
		if m := moves[c]; m > 0 {
			cells = append(cells, DarkCell{Cell: c, W: p.WalkCellW(m)})
		}
	}
	lamps, left := planLamps(cells, spots, p.LampW)
	return float64(len(lamps))*p.LampW + left
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
// station or worker holds. Lamps are placed when lighting the dark costs less
// than leaving it, never vetoed either way; a room under the sky gets none.
func (t *tiler) placeLamps() {
	p := t.out.Plan
	var moves map[domain.Cell]float64
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
		lamps, left := planLamps(cells, spots, t.in.Light.LampW)
		total := 0.0
		for _, c := range cells {
			total += c.W
		}
		if len(lamps) == 0 {
			if total > 0 {
				t.noteLamps(g.ID, ReasonTileLampsNotPaid, TileUnsatisfied, total, t.in.Light.LampW)
			}
			continue
		}
		if t.out.Lamps == nil {
			t.out.Lamps = map[int32][]domain.Cell{}
		}
		t.out.Lamps[room.ID] = lamps
		t.noteLamps(g.ID, ReasonTileLamps, TilePlace, left, float64(len(lamps))*t.in.Light.LampW)
	}
}

func (t *tiler) noteLamps(group int, reason, verdict string, darkW, lampW float64) {
	t.out.Decisions = append(t.out.Decisions, TileDecision{Group: group, Other: -1, Verdict: verdict, Reason: reason, DarkW: darkW, LampW: lampW})
}
