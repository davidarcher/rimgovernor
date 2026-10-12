package policy

import (
	"math"
	"slices"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A blocked tier (#2973). A tier the plan holds in place can find a tree, a
// rock chunk or a new building on a cell it needs. The two ways out are priced
// in the layout's units and the cheaper is taken; neither is a veto.
//
// Clear: the work of removing what blocks it (the plant's harvestWork, the
// building's deconstruct work, read from the game's defs) at a colonist's work
// speed, charged at the #2945 work-day price, net of what the removal yields
// (a felled tree's wood, a deconstruction's refund). A chunk is hauled, which
// has no work amount in the defs: it costs the walk of one cell (the lower
// bound of a haul). Rock needs mining, whose work the defs do not state, so it
// is unpriced.
//
// Move: the build cost of the tier at its new site plus the work of removing
// the standing buildings of the old one. The downtime, the days the old tier no
// longer covers the lane, is not priced: a move's price is a stated lower
// bound, and a move taken carries that in its reason.

// CoverKind is what stands on a cell the census reports as raider cover and the
// game's designators could remove.
type CoverKind uint8

const (
	CoverNone CoverKind = iota
	CoverPlant
	CoverChunk
	CoverMineable
	CoverBuilding
)

// CoverThing is the census's identification of the thing on a cell. Stuff is
// the building's material, empty for a stuffless thing.
type CoverThing struct {
	Def, Stuff string
	Kind       CoverKind
}

// ClearKey indexes BlockPricing.Rows: a def made of a stuff takes the stuff's
// work.
func ClearKey(def, stuff string) string { return def + "|" + stuff }

// ClearRow is what removing one thing takes and gives back, from the game's
// defs: Work is the work amount in ticks at work speed 1 (a plant's
// harvestWork, a building's clamped deconstruct work) and Yield the resources
// the removal leaves (a tree's wood, a deconstruction's refund).
type ClearRow struct {
	Work  float64
	Yield []Amount
}

// BlockPricing is what the blocked-tier price reads: a colonist's time (the
// #2945 work-day price, and the work ticks a work-day holds), the market values
// the yield and the build cost are counted in, the cost of each placed
// definition and the clearance rows of the things on the tier's cells.
type BlockPricing struct {
	Work            WorkPrice
	TicksPerWorkDay float64
	SteelValue      float64
	Market          map[Resource]float64
	UnitCosts       map[string][]Amount
	Rows            map[string]ClearRow
}

// Valid reports the pricing with the work-day, the work ticks of one and the
// steel the build cost is counted against known.
func (p BlockPricing) Valid() bool {
	return p.Work.Valid() && finitePositive(p.TicksPerWorkDay) && finitePositive(p.SteelValue)
}

// ColonistWorkTicksPerDay is the work ticks one colonist's work-day holds: the
// mean over the available colonists of their timetable's work hours at their
// work speed. Unknown while a schedule or speed is.
func ColonistWorkTicksPerDay(pawns []WorkPawn) domain.Fact[float64] {
	workers := DispatchWorkers(pawns, nil)
	if len(workers) == 0 {
		return domain.Unknown[float64]()
	}
	mean := AverageWorker(workers)
	hours, hk := mean.WorkHoursPerDay.Value()
	speed, sk := mean.WorkSpeed.Value()
	if !hk || !sk || !finitePositive(hours*speed) {
		return domain.Unknown[float64]()
	}
	return domain.Known(hours * domain.TicksPerHour * speed)
}

// valueW is amounts at their market value as watts, the build price's unit.
func (p BlockPricing) valueW(amounts []Amount) (float64, bool) {
	value := 0.0
	for _, a := range amounts {
		m, ok := p.Market[a.Resource]
		if !ok || m < 0 || math.IsNaN(m) || math.IsInf(m, 0) {
			return 0, false
		}
		value += m * float64(a.Count)
	}
	return BuildW(DefaultSiteTypeWeights(), value, p.SteelValue), true
}

// workW is ticks of work at a colonist's work-day price.
func (p BlockPricing) workW(ticks float64) float64 {
	return ticks / p.TicksPerWorkDay * p.Work.WorkDayW
}

// clearW is what removing the thing costs net of its yield.
func (p BlockPricing) clearW(t CoverThing) (float64, bool) {
	switch t.Kind {
	case CoverChunk:
		return p.Work.CellWalkDays * p.Work.WorkDayW, true
	case CoverPlant, CoverBuilding:
		row, ok := p.Rows[ClearKey(t.Def, t.Stuff)]
		if !ok || math.IsNaN(row.Work) || math.IsInf(row.Work, 0) || row.Work < 0 {
			return 0, false
		}
		yield, ok := p.valueW(row.Yield)
		if !ok {
			return 0, false
		}
		return p.workW(row.Work) - yield, true
	}
	return 0, false
}

// TierBlockChoice is the way a blocked tier is resolved.
type TierBlockChoice string

const (
	TierBlockClear TierBlockChoice = "clear"
	TierBlockMove  TierBlockChoice = "move"
	// TierBlockUnpriced: neither way could be priced; the tier stays and the
	// blockers are reported.
	TierBlockUnpriced TierBlockChoice = "unpriced"
)

// Why a blocked tier went the way it did; the caller journals them as
// layout_plan rows (target defense_tier_block).
const (
	// ReasonBlockClear: clearing is the cheaper way, or the only priced one.
	ReasonBlockClear = "tier_clear_cheaper"
	// ReasonBlockMove: moving is the cheaper way. The downtime of the move is
	// unpriced, so its price is a lower bound.
	ReasonBlockMove = "tier_move_cheaper_downtime_unpriced"
	// ReasonBlockUnpriced: no price for either way.
	ReasonBlockUnpriced = "tier_block_unpriced"
)

// TierBlockDecision is one blocked tier's price. ClearW and MoveW are valid
// where ClearKnown and MoveKnown say so.
type TierBlockDecision struct {
	Tier       DefenseTierName
	Blocked    []domain.Cell
	ClearW     float64
	ClearKnown bool
	MoveW      float64
	MoveKnown  bool
	Choice     TierBlockChoice
	Reason     string
}

// blockedTier is a pinned tier with a building missing from a cell something
// else stands on: the blockers by cell, and the tier's buildings that stand.
type blockedTier struct {
	name     DefenseTierName
	blocked  map[domain.Cell]CoverThing
	standing []domain.Building
}

// blockedTiers finds the pinned tiers a thing blocks, in name order. census is
// the raw census (the pinned buildings not read as ground).
func blockedTiers(census []DefenseCell, pins DefenseTierPins) []blockedTier {
	byCell := make(map[domain.Cell]DefenseCell, len(census))
	for _, c := range census {
		byCell[c.Cell] = c
	}
	names := make([]DefenseTierName, 0, len(pins.Tiers))
	for name := range pins.Tiers {
		names = append(names, name)
	}
	sort.Slice(names, func(i, j int) bool { return names[i] < names[j] })
	var out []blockedTier
	for _, name := range names {
		t := blockedTier{name: name, blocked: map[domain.Cell]CoverThing{}}
		for _, b := range pins.Tiers[name].Buildings {
			c, seen := byCell[b.Cell()]
			switch {
			case !seen:
			case c.Edifice == b.Definition():
				t.standing = append(t.standing, b)
			case c.Cover.Kind != CoverNone:
				t.blocked[b.Cell()] = c.Cover
			}
		}
		if len(t.blocked) > 0 {
			out = append(out, t)
		}
	}
	return out
}

// BlockedThings are the things whose clearance rows PriceTierBlocks reads: what
// blocks a pinned tier and the pinned tiers' standing buildings (a move removes
// them). Sorted, without repeats.
func BlockedThings(census []DefenseCell, pins DefenseTierPins) []CoverThing {
	seen := map[string]CoverThing{}
	for _, t := range blockedTiers(census, pins) {
		for _, thing := range t.blocked {
			seen[ClearKey(thing.Def, thing.Stuff)] = thing
		}
		for _, b := range t.standing {
			seen[ClearKey(b.Definition(), b.Stuff())] = CoverThing{Def: b.Definition(), Stuff: b.Stuff(), Kind: CoverBuilding}
		}
	}
	out := make([]CoverThing, 0, len(seen))
	for _, thing := range seen {
		out = append(out, thing)
	}
	slices.SortFunc(out, func(a, b CoverThing) int { return strings.Compare(ClearKey(a.Def, a.Stuff), ClearKey(b.Def, b.Stuff)) })
	return out
}

// PriceTierBlocks prices every pinned tier that has a building missing from a
// cell something else stands on. census is the raw census; sited are the tiers
// the census sites with nothing pinned, the places a blocked tier could move
// to. Tiers come in name order.
func PriceTierBlocks(census []DefenseCell, pins DefenseTierPins, sited []DefenseTier, p BlockPricing) []TierBlockDecision {
	var out []TierBlockDecision
	for _, t := range blockedTiers(census, pins) {
		d := TierBlockDecision{Tier: t.name}
		for cell := range t.blocked {
			d.Blocked = append(d.Blocked, cell)
		}
		slices.SortFunc(d.Blocked, func(a, b domain.Cell) int {
			if a.X != b.X {
				return int(a.X - b.X)
			}
			return int(a.Z - b.Z)
		})
		d.ClearKnown = p.Valid()
		for _, cell := range d.Blocked {
			w, ok := p.clearW(t.blocked[cell])
			d.ClearW, d.ClearKnown = d.ClearW+w, d.ClearKnown && ok
		}
		d.MoveW, d.MoveKnown = p.moveW(t.standing, sited, t.name, t.blocked)
		d.Choice, d.Reason = chooseBlock(d)
		out = append(out, d)
	}
	return out
}

// NewBlockPricing prices a colonist's time and the build cost from the station
// facts (the food census, the colonists, the crops, the items and the work
// pawns); unit costs and clearance rows are the caller's. Unknown facts leave
// the pricing invalid.
func NewBlockPricing(f StationFacts, unitCosts map[string][]Amount, rows map[string]ClearRow) BlockPricing {
	steel, _ := f.Items.MarketValue(siteSteelUnit)
	p := BlockPricing{Work: StationWorkPrice(f), SteelValue: steel, Market: f.Items.Market, UnitCosts: unitCosts, Rows: rows}
	if pawns, known := f.WorkPawns.Value(); known {
		p.TicksPerWorkDay, _ = ColonistWorkTicksPerDay(pawns).Value()
	}
	return p
}

// moveW is the price of moving the tier to the place the census sites it: its
// build cost plus the work of removing the old tier's standing buildings. Not
// known when the census sites no such place (or only on the blocked cells), or
// a cost, value or removal work is unknown.
func (p BlockPricing) moveW(standing []domain.Building, sited []DefenseTier, name DefenseTierName, blocked map[domain.Cell]CoverThing) (float64, bool) {
	i := slices.IndexFunc(sited, func(t DefenseTier) bool { return t.Name == name })
	if i < 0 || len(sited[i].Buildings) == 0 || !p.Valid() {
		return 0, false
	}
	var cost []Amount
	for _, b := range sited[i].Buildings {
		if _, still := blocked[b.Cell()]; still {
			return 0, false
		}
		unit, ok := p.UnitCosts[b.Definition()]
		if !ok {
			return 0, false
		}
		cost = append(cost, unit...)
	}
	w, ok := p.valueW(cost)
	if !ok {
		return 0, false
	}
	for _, b := range standing {
		row, ok := p.Rows[ClearKey(b.Definition(), b.Stuff())]
		if !ok {
			return 0, false
		}
		w += p.workW(row.Work)
	}
	return w, true
}

// chooseBlock takes the cheaper way; a tie keeps the tier where it stands.
func chooseBlock(d TierBlockDecision) (TierBlockChoice, string) {
	switch {
	case d.ClearKnown && (!d.MoveKnown || d.ClearW <= d.MoveW):
		return TierBlockClear, ReasonBlockClear
	case d.MoveKnown:
		return TierBlockMove, ReasonBlockMove
	}
	return TierBlockUnpriced, ReasonBlockUnpriced
}
