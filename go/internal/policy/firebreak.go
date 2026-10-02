package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// FirebreakWidth is the Chebyshev width of the firebreak ring around the
// base footprint (#1536).
const FirebreakWidth int32 = 2

// FirebreakSettleTicks is how long a cell stays in the ring before it may be
// paved: five in-game days.
const FirebreakSettleTicks domain.Tick = 5 * 60000

// FirebreakGround classifies what occupies a band cell.
type FirebreakGround string

const (
	// FirebreakOpen is ground the ring keeps cut or paves.
	FirebreakOpen FirebreakGround = "open"
	// FirebreakPlayerEdifice is a player-owned wall or other edifice.
	FirebreakPlayerEdifice FirebreakGround = "player_edifice"
	FirebreakWater         FirebreakGround = "water"
	FirebreakNaturalRock   FirebreakGround = "natural_rock"
	// FirebreakStoneRuin is a non-player, non-flammable edifice.
	FirebreakStoneRuin FirebreakGround = "stone_ruin"
	// FirebreakWoodenRuin is a non-player, flammable edifice: deconstructed,
	// then kept as a normal ring cell.
	FirebreakWoodenRuin FirebreakGround = "wooden_ruin"
)

func (g FirebreakGround) skipped() bool {
	switch g {
	case FirebreakPlayerEdifice, FirebreakWater, FirebreakNaturalRock, FirebreakStoneRuin:
		return true
	}
	return false
}

// FirebreakTreatment is what a ring cell is kept as.
type FirebreakTreatment string

const (
	FirebreakCut  FirebreakTreatment = "cut"
	FirebreakPave FirebreakTreatment = "pave"
)

// FirebreakRequest is everything PlanFirebreak reads. Ground must classify
// every band cell; a band cell it lacks, or holds unknown, leaves the plan
// unknown.
type FirebreakRequest struct {
	Bounds       domain.Fact[Bounds]
	Construction domain.Fact[CurrentConstruction]
	Claims       domain.Fact[[]ConstructionClaim]
	Home         domain.Fact[HomeCoverageObservation]
	// GrowingZones are the cells of every growing zone; they join the
	// footprint so a field is enclosed rather than cut.
	GrowingZones domain.Fact[[]domain.Cell]
	// Planned are the cells of the accepted layout plan's footprints.
	Planned domain.Fact[[]domain.Cell]
	Ground  map[domain.Cell]domain.Fact[FirebreakGround]
	Stage   domain.Fact[ColonyStage]
	Now     domain.Tick
	// Floors are the floor census rows, Stock the accessible colony stock
	// and Claimed the material queued construction already claims.
	Floors  map[string]FloorDefinition
	Stock   domain.Fact[map[Resource]int64]
	Claimed domain.Fact[map[Resource]int64]
	Policy  FlooringPolicy
}

// FirebreakCell is one ring cell's treatment.
type FirebreakCell struct {
	Cell      domain.Cell
	Treatment FirebreakTreatment
}

// FirebreakPlan is the ring work: every non-skipped band cell with its
// treatment, the wooden ruins to deconstruct, and Floor, the cheapest
// non-flammable floor the paved cells take (empty when none is paved).
type FirebreakPlan struct {
	Cells       []FirebreakCell
	Deconstruct []domain.Cell
	Floor       string
}

// PlanFirebreak derives the firebreak ring: the FirebreakWidth band outside
// the base footprint (the home base as PlanHomeArea picks it, plus growing
// zones), less skipped and planned cells. A cell is paved when the colony is
// in Development, the cell has been in the ring FirebreakSettleTicks, and
// the stock left after claimed material covers the cheapest non-flammable
// floor for every such cell; otherwise it is cut. dwell maps each ring cell
// to its first tick in the ring; the returned map drops cells that left, so
// a returning cell restarts. An unknown input returns an unknown plan and
// dwell unchanged.
func PlanFirebreak(r FirebreakRequest, dwell map[domain.Cell]domain.Tick) (domain.Fact[FirebreakPlan], map[domain.Cell]domain.Tick, error) {
	unknown := domain.Unknown[FirebreakPlan]()
	bounds, bk := r.Bounds.Value()
	zones, zk := r.GrowingZones.Value()
	planned, pk := r.Planned.Value()
	claims, ck := r.Claims.Value()
	stage, sk := r.Stage.Value()
	if !bk || !zk || !pk || !ck || !sk {
		return unknown, dwell, nil
	}
	extent, err := DeriveColonyExtent(ColonyExtentRequest{Bounds: r.Bounds, Construction: r.Construction, Claims: r.Claims, Home: r.Home, Margin: HomeAreaMargin})
	if err != nil {
		return unknown, dwell, err
	}
	value, known := extent.Value()
	if !known {
		return unknown, dwell, nil
	}
	footprint := homeBase(value)
	for _, c := range zones {
		footprint[c] = true
	}
	skip := map[domain.Cell]bool{}
	for _, c := range planned {
		skip[c] = true
	}
	for _, claim := range claims {
		for _, c := range claim.Cells {
			skip[c] = true
		}
	}
	band := map[domain.Cell]bool{}
	for c := range footprint {
		for dx := -FirebreakWidth; dx <= FirebreakWidth; dx++ {
			for dz := -FirebreakWidth; dz <= FirebreakWidth; dz++ {
				n := domain.Cell{X: c.X + dx, Z: c.Z + dz}
				if n.X >= 0 && n.Z >= 0 && n.X < bounds.Width && n.Z < bounds.Height && !footprint[n] && !skip[n] {
					band[n] = true
				}
			}
		}
	}
	plan := FirebreakPlan{Cells: []FirebreakCell{}, Deconstruct: []domain.Cell{}}
	next := map[domain.Cell]domain.Tick{}
	for c := range band {
		fact, ok := r.Ground[c]
		ground, gk := fact.Value()
		if !ok || !gk {
			return unknown, dwell, nil
		}
		if ground.skipped() {
			continue
		}
		if ground == FirebreakWoodenRuin {
			plan.Deconstruct = append(plan.Deconstruct, c)
		}
		first, held := dwell[c]
		if !held {
			first = r.Now
		}
		next[c] = first
		plan.Cells = append(plan.Cells, FirebreakCell{Cell: c, Treatment: FirebreakCut})
	}
	sort.Slice(plan.Cells, func(i, j int) bool { return extentCellLess(plan.Cells[i].Cell, plan.Cells[j].Cell) })
	sort.Slice(plan.Deconstruct, func(i, j int) bool { return extentCellLess(plan.Deconstruct[i], plan.Deconstruct[j]) })
	var settled []int
	if stage == StageDevelopment {
		for i, c := range plan.Cells {
			if r.Now-next[c.Cell] >= FirebreakSettleTicks {
				settled = append(settled, i)
			}
		}
	}
	if len(settled) > 0 {
		floor, known := firebreakFloor(r.Floors, r.Policy)
		if !known {
			return unknown, dwell, nil
		}
		stock, sk := r.Stock.Value()
		claimed, ck := r.Claimed.Value()
		if !sk || !ck {
			return unknown, dwell, nil
		}
		left := map[Resource]int64{}
		for k, v := range stock {
			left[k] = v - claimed[k]
		}
		if floor != "" && affordableCells(r.Floors[floor], domain.Known(left), len(settled)) >= len(settled) {
			plan.Floor = floor
			for _, i := range settled {
				plan.Cells[i].Treatment = FirebreakPave
			}
		}
	}
	return domain.Known(plan), next, nil
}

// firebreakFloor is the known-available, non-flammable policy floor with the
// fewest one-time ticks. known is false when no candidate can be judged and
// priced; an empty name with known true means none can be laid.
func firebreakFloor(floors map[string]FloorDefinition, p FlooringPolicy) (string, bool) {
	best, bestTicks, judged := "", 0.0, false
	for _, name := range p.Floors {
		def, ok := floors[name]
		available, ak := def.Available.Value()
		terrain, tk := def.Terrain.Value()
		flammability, fk := def.Flammability.Value()
		if !ok || !ak || !tk || !fk || !floorPriced(def) {
			continue
		}
		judged = true
		if !available || !terrain || flammability > 0 {
			continue
		}
		if ticks := floorTicks(def, p); best == "" || ticks < bestTicks {
			best, bestTicks = name, ticks
		}
	}
	return best, judged
}
