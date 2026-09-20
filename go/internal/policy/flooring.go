package policy

import (
	"crypto/sha256"
	"errors"
	"fmt"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainFlooring lays role-appropriate floors in the rooms the colony
// lives and works in (issue #6 slice 4). It reasons from the measured
// terrain under each room cell and the native stats of that terrain, never
// from what the controller last ordered: a clean workspace (kitchen,
// hospital, laboratory) is deficient while any cell's terrain cleanliness is
// negative, a living room (bedroom, barracks, dining, recreation) while any
// cell is still natural ground. Clean workspaces come first. The material
// is scored per tier from the native cleanliness, path cost, beauty,
// flammability and cost list of every affordable floor definition, and the
// latch releases only on a measured census with no deficient cell left.
// The traffic tier floors the home cells colonists are observed to walk
// most (the routes census samples actual movement) while they are still
// natural ground; it never projects traffic from layout.
const MaintainFlooring GoalID = "MaintainFlooring"

// FloorTier is the role-driven requirement a room's floor is measured
// against.
type FloorTier string

const (
	// FloorTierClean covers the rooms whose cleanliness the game reads for
	// food poisoning and infection: every cell needs a non-negative
	// terrain cleanliness.
	FloorTierClean FloorTier = "clean"
	// FloorTierLiving covers the rooms colonists rest, eat and relax in:
	// every cell needs a laid floor rather than natural ground.
	FloorTierLiving FloorTier = "living"
	// FloorTierTraffic covers the most-travelled home cells: every one
	// needs a laid floor with no path cost.
	FloorTierTraffic FloorTier = "traffic"
)

// trafficKey is the latch key of the single traffic deficit.
const trafficKey = "traffic"

type FlooringPolicy struct {
	// Floors lists the floor definitions the planner may choose from; the
	// native planning census reports each one's availability and stats.
	Floors []string
	// MaxCellsPerPlan bounds the cells one admitted plan floors.
	MaxCellsPerPlan int
	// Weights score a candidate floor for each tier.
	Clean, Living, Traffic FloorWeights
	// TrafficMinSamples is how many observed samples a natural home cell
	// needs before it counts as a bottleneck; the census must hold at least
	// four times as many samples in all so a short window never judges.
	TrafficMinSamples uint32
}

// FloorWeights price a floor's native stats: positive terms reward
// cleanliness and beauty, negative ones charge path cost, flammability and
// the summed cost list per cell.
type FloorWeights struct {
	Cleanliness, Beauty, PathCost, Flammability, Cost float64
}

func DefaultFlooringPolicy() FlooringPolicy {
	return FlooringPolicy{
		Floors:            []string{"SterileTile", "TileSandstone", "TileGranite", "TileLimestone", "TileSlate", "TileMarble", "FlagstoneSandstone", "FlagstoneGranite", "FlagstoneLimestone", "FlagstoneSlate", "FlagstoneMarble", Carpet, "PavedTile", "Concrete", "WoodPlankFloor"},
		MaxCellsPerPlan:   24,
		Clean:             FloorWeights{Cleanliness: 10, Beauty: 1, PathCost: 1, Flammability: 1, Cost: 0.2},
		Living:            FloorWeights{Cleanliness: 1, Beauty: 3, PathCost: 1, Flammability: 3, Cost: 0.2},
		Traffic:           FloorWeights{Cleanliness: 1, Beauty: 1, PathCost: 5, Flammability: 1, Cost: 0.5},
		TrafficMinSamples: 12,
	}
}

func (w FloorWeights) valid() bool {
	for _, v := range []float64{w.Cleanliness, w.Beauty, w.PathCost, w.Flammability, w.Cost} {
		if math.IsNaN(v) || math.IsInf(v, 0) || v < 0 {
			return false
		}
	}
	return true
}

func (p FlooringPolicy) valid() bool {
	if len(p.Floors) == 0 || len(p.Floors) > 64 || p.MaxCellsPerPlan < 1 || p.MaxCellsPerPlan > 256 || !p.Clean.valid() || !p.Living.valid() || !p.Traffic.valid() || p.TrafficMinSamples < 1 {
		return false
	}
	seen := map[string]bool{}
	for _, f := range p.Floors {
		if !foodID(f) || seen[f] {
			return false
		}
		seen[f] = true
	}
	return true
}

// FlooringObservation is the native flooring census: every proper indoor
// home room with the terrain under each cell, and the stats of every
// terrain named.
type FlooringObservation struct {
	Rooms    []FloorRoom
	Terrains map[string]FloorTerrain
	// Traffic lists the most-travelled cells the routes census observed,
	// most samples first, out of TrafficSamples samples in all; each names
	// a terrain in Terrains.
	Traffic        []TrafficCell
	TrafficSamples uint32
}
type FloorRoom struct {
	ID    string
	Role  domain.Fact[RoomRole]
	Cells []FloorCell
}
type FloorCell struct {
	Cell    domain.Cell
	Terrain string
	// Pending names the floor already ordered on the cell (a blueprint or
	// frame), empty when none.
	Pending string
}
type FloorTerrain struct {
	Cleanliness, Beauty, Flammability float64
	PathCost                          int32
	// Natural is native's own flag for unbuilt ground.
	Natural bool
}

func (v FlooringObservation) Validate() error {
	if len(v.Rooms) > 256 || len(v.Terrains) > 256 || len(v.Traffic) > 256 {
		return errors.New("flooring census exceeds bound")
	}
	for name, t := range v.Terrains {
		if !foodID(name) || !floorNumber(t.Cleanliness) || !floorNumber(t.Beauty) || !floorNumber(t.Flammability) || t.PathCost < 0 {
			return errors.New("invalid floor terrain")
		}
	}
	rooms := map[string]bool{}
	cells := map[domain.Cell]bool{}
	total := 0
	for _, room := range v.Rooms {
		if !foodID(room.ID) || rooms[room.ID] {
			return errors.New("invalid floor room")
		}
		rooms[room.ID] = true
		total += len(room.Cells)
		if total > 4096 {
			return errors.New("flooring census exceeds bound")
		}
		for _, c := range room.Cells {
			if _, ok := v.Terrains[c.Terrain]; !ok || cells[c.Cell] || c.Pending != "" && !foodID(c.Pending) {
				return errors.New("invalid floor cell")
			}
			cells[c.Cell] = true
		}
	}
	traffic := map[domain.Cell]bool{}
	for _, t := range v.Traffic {
		if _, ok := v.Terrains[t.Terrain]; !ok || traffic[t.Cell] || t.Pending != "" && !foodID(t.Pending) {
			return errors.New("invalid traffic cell")
		}
		traffic[t.Cell] = true
	}
	return nil
}

func floorNumber(v float64) bool { return !math.IsNaN(v) && !math.IsInf(v, 0) }

// FloorRoomKey identifies a flooring room across native renumbering the way
// RoomLatchKey does: by its lowest-sorted cell.
func FloorRoomKey(room FloorRoom) string {
	if len(room.Cells) == 0 {
		return room.ID
	}
	low := room.Cells[0].Cell
	for _, c := range room.Cells[1:] {
		if cellLess(c.Cell, low) {
			low = c.Cell
		}
	}
	return fmt.Sprintf("%d,%d", low.X, low.Z)
}

// FloorDeficit is one room measured short of its tier's requirement.
type FloorDeficit struct {
	Key, Room string
	Tier      FloorTier
	// Role is the room's role as the flooring census reports it, RoomRoleNone
	// for the traffic deficit's aisles; the tier style reads it (#610).
	Role RoomRole
	// Cells are the deficient cells not yet ordered, in cell order;
	// Pending counts the deficient cells with a floor already ordered.
	Cells   []domain.Cell
	Pending int
}

// FlooringReview is the per-room latch: Deficits lists every room measured
// short, clean workspaces first and then by key, and Latched is the key set
// persisted between reviews. Known is false when the census was unknown,
// in which case Latched repeats the previous latch and Deficits is empty.
type FlooringReview struct {
	Active   bool
	Known    bool
	Deficits []FloorDeficit
	Latched  []string
}

// floorTier classifies a room by the same role facts the cleanliness slice
// uses: the room census row supplies role and contents (a cooking bench
// makes a "Room" a clean workspace), the flooring row's own role stands in
// when the room census does not list the room. Inherently dirty rooms and
// every role outside the two tiers have no requirement.
func floorTier(room FloorRoom, census map[string]Room) (FloorTier, bool) {
	if r, ok := census[room.ID]; ok {
		if InherentlyDirty(r) {
			return "", false
		}
		if CleanWorkspace(r) {
			return FloorTierClean, true
		}
	}
	role, known := room.Role.Value()
	if !known {
		return "", false
	}
	switch role {
	case RoomRoleKitchen, RoomRoleHospital, RoomRoleLaboratory:
		return FloorTierClean, true
	case RoomRoleBedroom, RoomRoleBarracks, RoomRoleDiningRoom, RoomRoleRecRoom:
		return FloorTierLiving, true
	}
	return "", false
}

func floorDeficient(tier FloorTier, t FloorTerrain) bool {
	switch tier {
	case FloorTierClean:
		return t.Cleanliness < 0
	case FloorTierLiving, FloorTierTraffic:
		return t.Natural
	}
	return false
}

// trafficDeficit gathers the natural home cells observed busy enough to
// count as bottlenecks, most samples first. Cells inside a tiered room are
// that room's business.
func trafficDeficit(v FlooringObservation, roomed map[domain.Cell]bool, p FlooringPolicy) (FloorDeficit, bool) {
	d := FloorDeficit{Key: trafficKey, Tier: FloorTierTraffic}
	if v.TrafficSamples < 4*p.TrafficMinSamples {
		return d, false
	}
	for _, t := range v.Traffic {
		if !t.Home || t.Samples < p.TrafficMinSamples || roomed[t.Cell] || !floorDeficient(FloorTierTraffic, v.Terrains[t.Terrain]) {
			continue
		}
		if t.Pending != "" {
			d.Pending++
			continue
		}
		d.Cells = append(d.Cells, t.Cell)
	}
	return d, len(d.Cells) > 0 || d.Pending > 0
}

// ReviewFlooring measures every tiered room against its requirement. There
// is no hysteresis: terrain does not flap. A cell whose floor is already
// ordered still counts as deficient, so the latch holds until the floor is
// actually laid, but the planner never orders it twice. An unknown census
// keeps the previous latch.
func ReviewFlooring(fact domain.Fact[FlooringObservation], rooms domain.Fact[RoomObservation], previous []string, p FlooringPolicy) (FlooringReview, error) {
	if !p.valid() {
		return FlooringReview{}, errors.New("invalid flooring policy")
	}
	if len(previous) > 256 {
		return FlooringReview{}, errors.New("invalid flooring latch")
	}
	v, known := fact.Value()
	if !known {
		latched := append([]string(nil), previous...)
		sort.Strings(latched)
		return FlooringReview{Active: len(latched) > 0, Latched: latched}, nil
	}
	if err := v.Validate(); err != nil {
		return FlooringReview{}, err
	}
	census := map[string]Room{}
	if rc, ok := rooms.Value(); ok {
		for _, r := range rc.Rooms {
			census[r.ID] = r
		}
	}
	r := FlooringReview{Known: true}
	roomed := map[domain.Cell]bool{}
	for _, room := range v.Rooms {
		tier, ok := floorTier(room, census)
		if !ok {
			continue
		}
		for _, c := range room.Cells {
			roomed[c.Cell] = true
		}
		role, _ := room.Role.Value()
		d := FloorDeficit{Key: FloorRoomKey(room), Room: room.ID, Tier: tier, Role: role}
		for _, c := range room.Cells {
			if !floorDeficient(tier, v.Terrains[c.Terrain]) {
				continue
			}
			if c.Pending != "" {
				d.Pending++
				continue
			}
			d.Cells = append(d.Cells, c.Cell)
		}
		if len(d.Cells) == 0 && d.Pending == 0 {
			continue
		}
		sort.Slice(d.Cells, func(i, j int) bool { return cellLess(d.Cells[i], d.Cells[j]) })
		r.Deficits = append(r.Deficits, d)
		r.Latched = append(r.Latched, d.Key)
	}
	if d, ok := trafficDeficit(v, roomed, p); ok {
		r.Deficits = append(r.Deficits, d)
		r.Latched = append(r.Latched, d.Key)
	}
	sort.SliceStable(r.Deficits, func(i, j int) bool {
		if r.Deficits[i].Tier != r.Deficits[j].Tier {
			return floorTierOrder[r.Deficits[i].Tier] < floorTierOrder[r.Deficits[j].Tier]
		}
		return r.Deficits[i].Key < r.Deficits[j].Key
	})
	sort.Strings(r.Latched)
	r.Active = len(r.Deficits) > 0
	return r, nil
}

var floorTierOrder = map[FloorTier]int{FloorTierClean: 0, FloorTierLiving: 1, FloorTierTraffic: 2}

type FlooringMethod string

const (
	FlooringUnknown         FlooringMethod = "unknown"
	FlooringNoMethod        FlooringMethod = "no_deficit"
	FlooringPending         FlooringMethod = "floor_pending"
	FlooringResearchNeeded  FlooringMethod = "floor_research_needed"
	FlooringMaterialsNeeded FlooringMethod = "floor_materials_needed"
	FlooringBuild           FlooringMethod = "lay_floor"
)

type FlooringProposal struct {
	Method FlooringMethod
	Key    domain.MethodID
	// Room and Tier name the deficient room served; Definition is the
	// chosen floor and Cells the cells to lay it on, each to be validated by
	// a native placement preview before admission.
	Room       string
	Tier       FloorTier
	Definition string
	Cells      []domain.Cell
}

// FloorDefinition is what the planning census reports for one candidate
// floor.
type FloorDefinition struct {
	Available                         domain.Fact[bool]
	Terrain                           domain.Fact[bool]
	Cleanliness, Beauty, Flammability domain.Fact[float64]
	PathCost                          domain.Fact[int32]
	Costs                             domain.Fact[[]Amount]
}

// FlooringFacts is what SelectFlooringMethod needs beyond the review.
type FlooringFacts struct {
	// Definitions maps floor definition names to their census row; a floor
	// absent here is unknown.
	Definitions map[string]FloorDefinition
	// Stock is the accessible colony stock by resource; unknown skips the
	// affordability test and leaves it to admission.
	Stock domain.Fact[map[Resource]int64]
	// Style is the tier's floor rule per room role (FloorDef, #610): a
	// styled floor that is known available, meets the tier and pays for
	// the whole batch is chosen before any scoring. Nil styles nothing.
	Style func(RoomRole) (string, bool)
}

// floorScore prices one candidate for a tier; ok is false when the floor
// fails the tier's requirement outright.
func floorScore(tier FloorTier, d FloorDefinition, w FloorWeights) (float64, bool) {
	cleanliness, _ := d.Cleanliness.Value()
	beauty, _ := d.Beauty.Value()
	flammability, _ := d.Flammability.Value()
	pathCost, _ := d.PathCost.Value()
	if tier == FloorTierClean && cleanliness < 0 || tier == FloorTierLiving && beauty < 0 || tier == FloorTierTraffic && pathCost > 0 {
		return 0, false
	}
	var cost float64
	if costs, known := d.Costs.Value(); known {
		for _, a := range costs {
			cost += float64(a.Count)
		}
	}
	return w.Cleanliness*cleanliness + w.Beauty*beauty - w.PathCost*float64(pathCost) - w.Flammability*flammability - w.Cost*cost, true
}

// affordableCells is how many cells of the floor the stock pays for; -1
// when the stock is unknown.
func affordableCells(d FloorDefinition, stock domain.Fact[map[Resource]int64], want int) int {
	values, known := stock.Value()
	costs, ck := d.Costs.Value()
	if !known || !ck {
		return -1
	}
	n := want
	for _, a := range costs {
		if a.Count <= 0 {
			continue
		}
		n = min(n, int(values[a.Resource]/a.Count))
	}
	return n
}

// SelectFlooringMethod resolves the first deficit: clean workspaces before
// living rooms. Candidates must be known available, be terrain, meet the
// tier's requirement and be affordable for at least one cell; a floor that
// pays for the whole batch beats one that pays for part of it, then the
// tier's score decides. A room whose deficient cells are all ordered
// already waits for them.
func SelectFlooringMethod(review FlooringReview, facts FlooringFacts, p FlooringPolicy) (FlooringProposal, error) {
	if !p.valid() {
		return FlooringProposal{}, errors.New("invalid flooring policy")
	}
	if !review.Active {
		return FlooringProposal{Method: FlooringNoMethod}, nil
	}
	if !review.Known {
		return FlooringProposal{Method: FlooringUnknown}, nil
	}
	var deferred FlooringMethod
	for _, d := range review.Deficits {
		if len(d.Cells) == 0 {
			deferred = firstFlooringReason(deferred, FlooringPending)
			continue
		}
		batch := min(len(d.Cells), p.MaxCellsPerPlan)
		weights := p.Clean
		switch d.Tier {
		case FloorTierLiving:
			weights = p.Living
		case FloorTierTraffic:
			weights = p.Traffic
		}
		type candidate struct {
			name  string
			cells int
			score float64
		}
		var best *candidate
		reason := FlooringResearchNeeded
		unknown := false
		// The tier style decides when it can be laid; the scored list is
		// consulted only otherwise.
		scored := p.Floors
		if name, ok := styledFloor(d, facts, batch, weights); ok {
			best, scored = &candidate{name, batch, 0}, nil
		}
		for _, name := range scored {
			def, ok := facts.Definitions[name]
			if !ok {
				unknown = true
				continue
			}
			available, ak := def.Available.Value()
			terrain, tk := def.Terrain.Value()
			if !ak || !tk {
				unknown = true
				continue
			}
			if !available || !terrain {
				continue
			}
			score, meets := floorScore(d.Tier, def, weights)
			if !meets {
				continue
			}
			cells := affordableCells(def, facts.Stock, batch)
			if cells < 0 {
				cells = batch
			}
			if cells == 0 {
				reason = FlooringMaterialsNeeded
				continue
			}
			c := candidate{name, cells, score}
			if best == nil || c.cells > best.cells || c.cells == best.cells && c.score > best.score {
				best = &c
			}
		}
		if best == nil {
			if unknown {
				reason = FlooringUnknown
			}
			deferred = firstFlooringReason(deferred, reason)
			continue
		}
		cells := append([]domain.Cell(nil), d.Cells[:best.cells]...)
		digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%s/%s/%d,%d/%d", d.Key, d.Tier, best.name, cells[0].X, cells[0].Z, len(cells))))
		return FlooringProposal{Method: FlooringBuild, Key: domain.MethodID(fmt.Sprintf("flooring-%x", digest[:12])), Room: d.Room, Tier: d.Tier, Definition: best.name, Cells: cells}, nil
	}
	if deferred == "" {
		return FlooringProposal{Method: FlooringNoMethod}, nil
	}
	return FlooringProposal{Method: deferred}, nil
}

func firstFlooringReason(current, next FlooringMethod) FlooringMethod {
	if current == "" || current == FlooringUnknown {
		return next
	}
	return current
}

// styledFloor is the style's floor for the deficit when it can be laid
// now: known available terrain that meets the tier's requirement and pays
// for the whole batch. Otherwise the scored candidates decide.
func styledFloor(d FloorDeficit, facts FlooringFacts, batch int, weights FloorWeights) (string, bool) {
	if facts.Style == nil {
		return "", false
	}
	name, ok := facts.Style(d.Role)
	if !ok {
		return "", false
	}
	def, ok := facts.Definitions[name]
	if !ok {
		return "", false
	}
	available, ak := def.Available.Value()
	terrain, tk := def.Terrain.Value()
	if !ak || !tk || !available || !terrain {
		return "", false
	}
	if _, meets := floorScore(d.Tier, def, weights); !meets {
		return "", false
	}
	if cells := affordableCells(def, facts.Stock, batch); cells >= 0 && cells < batch {
		return "", false
	}
	return name, true
}
