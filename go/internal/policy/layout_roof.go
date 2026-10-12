package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Roofed set (#2941): on a map whose outdoor temperature is not good for
// pawns, every room and hallway except the outdoor roles is roofed, so the
// roofed rooms and hallways are one envelope (no outer climate wall; the raider
// perimeter is separate). A room roofs itself when its ring closes, so a room is
// roofed without a price; a hallway is roofed with the existing
// BuildRoofAction only when the roof pays: the watts its roof saves the doors
// that open onto it against the watts that hold it (climate.go). A roof takes
// work, not materials, so watts are the only price. The set is advice beside
// the old planner, which stays authoritative until #2931 issues the roofs.
//
// Bad climate is the map's two existing latches, no new constant: the seasonal
// curve dips below ColdMapBelowC or peaks above HotEnter (ColdMapCurve,
// HotMapCurve), or the survey's Cold or Hot latch is set. The load is priced
// in the temperature planner's comfort band (ColdEnter to HotEnter). Roofs hold only within
// RoofMaxSupportDistance of a wall; a roofed cell farther than that stays in
// the set and is reported (ReasonRoofUnsupported), never vetoed.

// Reasons of roofed-set decisions.
const (
	ReasonRoofTemperate   = "roof_temperate"
	ReasonRoofRoom        = "roof_room"
	ReasonRoofHallway     = "roof_hallway"
	ReasonRoofHallwaySkip = "roof_hallway_not_paid"
	ReasonRoofUnpriced    = "roof_hallway_unpriced"
	ReasonRoofUnsupported = "roof_unsupported"
	ReasonRoofSupportless = "roof_support_unknown"
)

// RoofDecision is one roofed-set decision for the caller to journal. Room is
// the room's id (0 for a hallway); Segment the hallway's index in
// LayoutPlan.Hallways (-1 for a room). Cells are the cells a reason names;
// SavedW and HeldW are a hallway's priced watts.
type RoofDecision struct {
	Room    int32
	Segment int
	Verdict string
	Reason  string
	Cells   []domain.Cell `json:",omitempty"`
	SavedW  float64       `json:",omitempty"`
	HeldW   float64       `json:",omitempty"`
	// LightW is the part of HeldW that lights the dark the roof makes (lamps
	// plus the dark left over); LightUnpriced says the price was unknown and
	// the roof was weighed without it.
	LightW        float64 `json:",omitempty"`
	LightUnpriced bool    `json:",omitempty"`
}

// RoofInput is what the roofed set reads beside the plan. Curve is the
// seasonal outdoor temperature curve (twelve means, C); Cold and Hot are the
// survey's latches. RoofDistance is the game's roof support distance; zero is
// unknown.
type RoofInput struct {
	Curve        domain.Fact[[]float64]
	Cold, Hot    bool
	Climate      ClimateModel
	RoofDistance float64
	// Light prices the lamps a roofed hallway needs for the walkers it carries
	// (layout_light.go); invalid adds no lamp term and the decision says so.
	Light LightPrice
	// Rock is the survey's natural rock cells, which hold a hallway's wall lamps
	// as a wall does.
	Rock map[domain.Cell]bool
}

// RoofResult is the roofed cells and the decisions behind them.
type RoofResult struct {
	Roofed    Region
	Decisions []RoofDecision
}

// ComfortBand is the temperature range pawns are comfortable in: the
// temperature planner's cold and hot limits.
func ComfortBand() Band {
	p := DefaultRoundsPolicy()
	return Band{MinC: p.ColdEnter, MaxC: p.HotEnter}
}

// BadClimate reports a map whose outdoor temperature is not good for pawns: a
// latch set, or the curve cold or hot.
func BadClimate(in RoofInput) bool {
	curve, known := in.Curve.Value()
	return in.Cold || in.Hot || known && (ColdMapCurve(curve) || HotMapCurve(curve))
}

// PlanRoofed is the roofed set of a plan: empty on a temperate map; else the
// floor of every room that is not an outdoor role, and each hallway segment
// whose roof pays. It is a pure function of the plan and the input.
func PlanRoofed(p LayoutPlan, in RoofInput) RoofResult {
	var out RoofResult
	if !BadClimate(in) {
		out.Decisions = append(out.Decisions, RoofDecision{Segment: -1, Verdict: TilePlace, Reason: ReasonRoofTemperate})
		return out
	}
	var cells []domain.Cell
	outdoor := map[domain.Cell]bool{}
	for _, r := range p.AllRooms() {
		if r.Role.IsOutdoor() {
			for _, c := range r.Region.Cells() {
				outdoor[c] = true
			}
			continue
		}
		cells = append(cells, r.Region.Cells()...)
		out.Decisions = append(out.Decisions, RoofDecision{Room: r.ID, Segment: -1, Verdict: TilePlace, Reason: ReasonRoofRoom})
	}
	var moves map[domain.Cell]float64
	var walls func(domain.Cell) bool
	for i, rect := range spineRects(p.Hallways()) {
		saved, held, priced := hallwayRoofPrice(p, in, rect)
		// A roof takes the sky's light: the walkers it carries need lamps, or
		// walk slower in the dark, whichever costs less.
		var lightW float64
		if in.Light.Valid() {
			if moves == nil {
				moves = tripCellMovements(p)
			}
			if walls == nil {
				walls = plainWalls(p, in.Rock)
			}
			lightW = hallwayLightW(rect, moves, walls, in.Light)
			held += lightW
		}
		d := RoofDecision{Segment: i, Verdict: TilePlace, Reason: ReasonRoofHallway, SavedW: saved, HeldW: held, LightW: lightW, LightUnpriced: !in.Light.Valid()}
		switch {
		case !priced:
			d.Verdict, d.Reason = TileUnsatisfied, ReasonRoofUnpriced
		case saved <= held:
			d.Verdict, d.Reason = TileUnsatisfied, ReasonRoofHallwaySkip
		default:
			for z := rect.Z; z < rect.Z+rect.Height; z++ {
				for x := rect.X; x < rect.X+rect.Width; x++ {
					cells = append(cells, domain.Cell{X: x, Z: z})
				}
			}
		}
		out.Decisions = append(out.Decisions, d)
	}
	kept := cells[:0]
	for _, c := range cells {
		if !outdoor[c] { // an outdoor room holds no roof, even under a hallway rectangle
			kept = append(kept, c)
		}
	}
	out.Roofed = RegionOf(kept)
	if in.RoofDistance <= 0 {
		out.Decisions = append(out.Decisions, RoofDecision{Segment: -1, Verdict: TileUnsatisfied, Reason: ReasonRoofSupportless})
		return out
	}
	if far := unsupportedRoof(p, out.Roofed, in.RoofDistance); len(far) > 0 {
		out.Decisions = append(out.Decisions, RoofDecision{Segment: -1, Verdict: TileUnsatisfied, Reason: ReasonRoofUnsupported, Cells: far})
	}
	return out
}

// hallwayRoofPrice is what roofing a hallway segment saves and costs in
// sustained watts, with the envelope held in the comfort band: the doors that
// open onto it stop exchanging heat with the outdoors (each room's load through
// an open door to the outdoors), and the segment itself exchanges through its
// entrances (open doors to the outdoors). False while the curve or the model is
// unknown.
func hallwayRoofPrice(p LayoutPlan, in RoofInput, rect Rectangle) (saved, held float64, priced bool) {
	curve, known := in.Curve.Value()
	if !known || !in.Climate.Valid() {
		return 0, 0, false
	}
	band := ComfortBand()
	door := ClimateLink{Opening: OpeningDoorOpen, Side: ClimateSide{Outdoors: true}}
	load := func(cells int, links ...ClimateLink) (float64, bool) {
		w, err := ClimateLoad(in.Climate, curve, ClimateRoom{Cells: cells, Band: band, Links: links}, 0)
		return w.MeanW, err == nil
	}
	for _, r := range p.AllRooms() {
		if r.Role.IsOutdoor() || r.Unfenced() {
			continue
		}
		for _, d := range r.BoundaryDoors() {
			if !contains(rect, domain.RoomDoor{Cell: d.Cell, Entrance: d.Rot}.Threshold()) {
				continue
			}
			w, ok := load(r.Region.Area(), door)
			if !ok {
				return 0, 0, false
			}
			saved += w
		}
	}
	var entrances []ClimateLink
	for _, c := range p.Entrances {
		if contains(rect, c) {
			entrances = append(entrances, door)
		}
	}
	if len(entrances) > 0 {
		w, ok := load(int(rect.Width*rect.Height), entrances...)
		if !ok {
			return 0, 0, false
		}
		held = w
	}
	return saved, held, true
}

// unsupportedRoof lists the roofed cells farther than radius from every wall
// cell of a room that holds a roof, in cell order.
func unsupportedRoof(p LayoutPlan, roofed Region, radius float64) []domain.Cell {
	var walls []domain.Cell
	for _, r := range p.AllRooms() {
		if !r.Role.IsOutdoor() && !r.Unfenced() {
			walls = append(walls, r.Region.Walls().Cells()...)
		}
	}
	var far []domain.Cell
	for _, c := range roofed.Cells() {
		near := false
		for _, w := range walls {
			if math.Hypot(float64(c.X)-float64(w.X), float64(c.Z)-float64(w.Z)) <= radius {
				near = true
				break
			}
		}
		if !near {
			far = append(far, c)
		}
	}
	sort.Slice(far, func(i, j int) bool { return cellLess(far[i], far[j]) })
	return far
}

// roofedOutdoors reports a roofed cell inside an outdoor room (Valid rejects it).
func (p LayoutPlan) roofedOutdoors() bool {
	if p.Roofed.Empty() {
		return false
	}
	for _, r := range p.AllRooms() {
		if r.Role.IsOutdoor() && p.Roofed.Overlaps(r.Region) {
			return true
		}
	}
	return false
}
