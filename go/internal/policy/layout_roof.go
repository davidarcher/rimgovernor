package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Roofed set (#2941): on a map whose outdoor temperature is not good for
// pawns, every room except the outdoor roles is roofed, so the roofed rooms are
// one envelope (no outer climate wall; the raider perimeter is separate). A
// room roofs itself when its ring closes, so a room is roofed without a price.
// The climate envelope round roofs the set (MaintainClimateEnvelope).
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
	ReasonRoofUnsupported = "roof_unsupported"
	ReasonRoofSupportless = "roof_support_unknown"
)

// RoofDecision is one roofed-set decision for the caller to journal. Room is
// the room's id (0 for a decision about the whole set). Cells are the cells a
// reason names.
type RoofDecision struct {
	Room    int32
	Verdict string
	Reason  string
	Cells   []domain.Cell `json:",omitempty"`
}

// RoofInput is what the roofed set reads beside the plan. Curve is the
// seasonal outdoor temperature curve (twelve means, C); Cold and Hot are the
// survey's latches. RoofDistance is the game's roof support distance; zero is
// unknown.
type RoofInput struct {
	Curve        domain.Fact[[]float64]
	Cold, Hot    bool
	RoofDistance float64
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
// floor of every room that is not an outdoor role. It is a pure function of the
// plan and the input.
func PlanRoofed(p LayoutPlan, in RoofInput) RoofResult {
	var out RoofResult
	if !BadClimate(in) {
		out.Decisions = append(out.Decisions, RoofDecision{Verdict: TilePlace, Reason: ReasonRoofTemperate})
		return out
	}
	var cells []domain.Cell
	outdoor := map[domain.Cell]bool{}
	for _, r := range p.Rooms {
		if r.Role.IsOutdoor() {
			for _, c := range r.Region.Cells() {
				outdoor[c] = true
			}
			continue
		}
		cells = append(cells, r.Region.Cells()...)
		out.Decisions = append(out.Decisions, RoofDecision{Room: r.ID, Verdict: TilePlace, Reason: ReasonRoofRoom})
	}
	kept := cells[:0]
	for _, c := range cells {
		if !outdoor[c] { // an outdoor room holds no roof
			kept = append(kept, c)
		}
	}
	out.Roofed = RegionOf(kept)
	if in.RoofDistance <= 0 {
		out.Decisions = append(out.Decisions, RoofDecision{Verdict: TileUnsatisfied, Reason: ReasonRoofSupportless})
		return out
	}
	if far := unsupportedRoof(p, out.Roofed, in.RoofDistance); len(far) > 0 {
		out.Decisions = append(out.Decisions, RoofDecision{Verdict: TileUnsatisfied, Reason: ReasonRoofUnsupported, Cells: far})
	}
	return out
}

// unsupportedRoof lists the roofed cells farther than radius from every wall
// cell of a room that holds a roof, in cell order.
func unsupportedRoof(p LayoutPlan, roofed Region, radius float64) []domain.Cell {
	var walls []domain.Cell
	for _, r := range p.Rooms {
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
	for _, r := range p.Rooms {
		if r.Role.IsOutdoor() && p.Roofed.Overlaps(r.Region) {
			return true
		}
	}
	return false
}
