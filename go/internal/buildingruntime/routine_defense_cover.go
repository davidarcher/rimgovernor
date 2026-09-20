package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// Cover clearance (#581): once every tier of the accepted layout stands,
// the planner re-reads the census around the colony, recomputes the
// layout's approaches (raid arrival sectors from the native raid trails,
// each sector's route to Entry) and designates the cover things the policy
// selects inside the firing line's engagement zone for removal: plants are
// cut, chunks hauled, rock mined. Buildings are never touched here: a
// player's own structure is theirs, and a ruin the game's deconstruct
// designator would refuse is reported, not ordered. Each method is one plan
// of up to maxDefenseCoverBatch clearances, bounded per game day so a thing
// the player keeps undesignating does not become a loop.
const (
	maxDefenseCoverBatch    = 8
	maxDefenseCoverAttempts = 4
	defenseCoverWindowTicks = 60000
	defenseCoverPrefix      = "defense-cover-"
)

// defenseCoverAttempts counts the epoch's cover methods ordered within the
// window before tick.
func defenseCoverAttempts(history []domain.GoalMethod, tick domain.Tick) int {
	count := 0
	for _, m := range history {
		if !strings.HasPrefix(string(m.Method), defenseCoverPrefix) {
			continue
		}
		at, err := strconv.ParseInt(strings.TrimPrefix(string(m.Method), defenseCoverPrefix), 10, 64)
		if err == nil && tick-domain.Tick(at) < defenseCoverWindowTicks {
			count++
		}
	}
	return count
}

// defenseRecordLayout rebuilds the accepted geometry the approaches keep
// protected: lanes, firing cells and every tier's placements and reserved
// cells. A firing position's cover and retreat cells are tier placements
// or reserved cells, so the firing cell alone stands for the position.
func defenseRecordLayout(record store.DefenseLayoutRecord) (policy.DefenseLayout, error) {
	l := policy.DefenseLayout{Chokepoint: record.Chokepoint, Toward: record.Toward, Width: record.Width, Entry: record.Entry,
		TrapLane: append([]domain.Cell{}, record.TrapLane...), SafeLane: append([]domain.Cell{}, record.SafeLane...)}
	for _, cell := range record.Firing {
		l.Firing = append(l.Firing, policy.FiringPosition{Cell: cell, Cover: cell, Retreat: cell})
	}
	for _, tier := range record.Tiers {
		t := policy.DefenseTier{Name: tier.Name, Reserved: append([]domain.Cell{}, tier.Reserved...)}
		for _, b := range tier.Buildings {
			building, err := domain.NewBuilding(b.Definition, b.Cell, b.Rotation, b.Stuff)
			if err != nil {
				return policy.DefenseLayout{}, err
			}
			t.Buildings = append(t.Buildings, building)
		}
		l.Tiers = append(l.Tiers, t)
	}
	return l, nil
}

// defenseArrivals maps the native raid tracks to policy arrivals: ground
// raids only, each at the first sampled position inside the census region
// (its spawn when that already lies inside). A raid whose trail never
// entered the region has no local crossing and is left out.
func defenseArrivals(raids []bridge.RaidTrack, region bridge.CellRect) []policy.DefenseArrival {
	inside := func(c domain.Cell) bool {
		return c.X >= region.Min.X && c.X <= region.Max.X && c.Z >= region.Min.Z && c.Z <= region.Max.Z
	}
	var out []policy.DefenseArrival
	for _, raid := range raids {
		if !raid.Ground {
			continue
		}
		crossing, found := raid.Spawn, inside(raid.Spawn)
		for _, c := range raid.Trail {
			if found {
				break
			}
			crossing, found = c, inside(c)
		}
		if found {
			out = append(out, policy.DefenseArrival{ID: raid.LordID, Edge: crossing, Tick: raid.SpawnTick})
		}
	}
	return out
}

// defenderRange reads the colonists' combat gear for the shortest ranged
// weapon range, the engagement zone's radius; ok false means no complete
// colonist census or no ranged defender.
func (r *RoutineDefenseLayoutPlanner) defenderRange(call context.Context, state ControlState, read observation.RoutineReading) (int, domain.Fact[float64], bool, error) {
	colonistsComplete, ck := read.Emergency.ColonistsComplete.Value()
	if !ck || !colonistsComplete || len(read.Emergency.Colonists) == 0 {
		return 0, domain.Unknown[float64](), false, nil
	}
	ids := make([]string, 0, len(read.Emergency.Colonists))
	for _, pawn := range read.Emergency.Colonists {
		ids = append(ids, string(pawn.ID))
	}
	reply, _, err := r.native.ReadCombatPawns(call, boundary.Identity(state.Snapshot), ids)
	if err != nil {
		return 0, domain.Unknown[float64](), false, err
	}
	observed := reply.GetObserved()
	if observed == nil || len(observed.Pawns) != len(ids) {
		return 0, domain.Unknown[float64](), false, defenseControlErr(248)
	}
	if err = r.sameTick(observed.Context, state, read.Projection.Identity.Tick); err != nil {
		return 0, domain.Unknown[float64](), false, err
	}
	defenders, minRange := defenderRange(observed.Pawns)
	return defenders, minRange, defenders > 0, nil
}

// defenseCoverSelection lists the clearances the census identifies among
// the policy's cover demand, in the policy's order, and counts what it
// leaves out by reason for the scheduler log.
func defenseCoverSelection(approaches policy.DefenseApproaches, byCell map[domain.Cell]bridge.DefenseCell) ([]domain.CoverClearance, map[string]int, error) {
	var clearances []domain.CoverClearance
	held := map[string]int{}
	for _, cover := range approaches.Cover {
		if cover.Hold != "" {
			held[cover.Hold]++
			continue
		}
		cell := byCell[cover.Cell]
		switch {
		case cell.Cover == nil:
			held["unidentified"]++
		case cell.Cover.Designated:
			held["designated"]++
		case cell.PlayerOwned || cell.Cover.Kind == o.CoverKind_COVER_KIND_BUILDING:
			held["structure"]++
		case cell.Cover.Designation() == "":
			held["unknown_kind"]++
		default:
			clearance, err := domain.NewCoverClearance(cell.Cover.ThingID, cell.Cover.DefName, cell.Cover.Designation(), cover.Cell)
			if err != nil {
				return nil, nil, err
			}
			clearances = append(clearances, clearance)
		}
	}
	return clearances, held, nil
}

// clearCover proposes the next cover-clearance method for a complete
// layout. handled false means nothing to order: no cover selected, the
// policy holding, or no ranged defender to size the zone by.
func (r *RoutineDefenseLayoutPlanner) clearCover(call, epoch context.Context, goal store.GoalState, review store.RoutineReview, state ControlState, read observation.RoutineReading, record store.DefenseLayoutRecord) (RoutineDefenseLayoutResult, bool, error) {
	p := r.reviewer.player
	projection := read.Projection
	tick := projection.Identity.Tick
	identity := boundary.Identity(state.Snapshot)
	region, err := r.extentRegion(call, state.Snapshot, projection)
	if err != nil {
		return RoutineDefenseLayoutResult{}, false, err
	}
	site, _, err := r.native.ReadDefenseSite(call, identity, region)
	if err != nil {
		return RoutineDefenseLayoutResult{}, false, err
	}
	if err = r.sameTick(site.Context, state, tick); err != nil {
		return RoutineDefenseLayoutResult{}, false, err
	}
	request := defenseTurretRequest(read)
	request.Bounds, request.Home, request.Tick = projection.Bounds, projection.Center, tick
	request.Region = policy.Rectangle{X: region.Min.X, Z: region.Min.Z, Width: region.Max.X - region.Min.X + 1, Height: region.Max.Z - region.Min.Z + 1}
	byCell := map[domain.Cell]bridge.DefenseCell{}
	for _, cell := range site.Cells {
		byCell[cell.Cell] = cell
		request.Cells = append(request.Cells, defenseCellFacts(cell))
		if !cell.Fogged && cell.Door && cell.PlayerOwned {
			request.Entrances = append(request.Entrances, cell.Cell)
		}
	}
	request.Arrivals, request.CoverThreshold = defenseArrivals(site.Raids, region), domain.Known(site.CoverThreshold)
	defenders, minRange, ok, err := r.defenderRange(call, state, read)
	if err != nil {
		return RoutineDefenseLayoutResult{}, false, err
	}
	if !ok {
		clockSchedulerLog("defense-layout: cover clearance held: no ranged defender sizes the engagement zone")
		return RoutineDefenseLayoutResult{}, false, nil
	}
	request.Defenders, request.MinRange = defenders, minRange
	rangeLimit, _ := minRange.Value()
	layout, err := defenseRecordLayout(record)
	if err != nil {
		return RoutineDefenseLayoutResult{}, false, err
	}
	approaches, err := policy.DefenseApproachesFor(request, layout)
	if err != nil {
		clockSchedulerLog("defense-layout: cover census refused: %v", err)
		return RoutineDefenseLayoutResult{}, false, nil
	}
	if approaches.Hold != "" {
		clockSchedulerLog("defense-layout: cover clearance held: %s", approaches.Hold)
		return RoutineDefenseLayoutResult{}, false, nil
	}
	clearances, held, err := defenseCoverSelection(approaches, byCell)
	if err != nil {
		return RoutineDefenseLayoutResult{}, false, err
	}
	if len(held) > 0 || len(clearances) == 0 {
		clockSchedulerLog("defense-layout: cover demand %d, orderable %d, held %v (range %.1f, sectors %d, arrivals %d, unmatched %v, %s)", len(approaches.Cover), len(clearances), held, rangeLimit, len(approaches.Sectors), len(request.Arrivals), approaches.UnmatchedArrivals, defenseCensusSummary(request, byCell))
	}
	if len(clearances) == 0 {
		return RoutineDefenseLayoutResult{}, false, nil
	}
	if len(clearances) > maxDefenseCoverBatch {
		clearances = clearances[:maxDefenseCoverBatch]
	}
	history, err := p.journal.LoadGoalMethods(call, goal.Goal.ID, goal.Goal.Epoch)
	if err != nil {
		return RoutineDefenseLayoutResult{}, false, err
	}
	if defenseCoverAttempts(history, tick) >= maxDefenseCoverAttempts {
		clockSchedulerLog("defense-layout: cover clearance exhausted for the day (%d things waiting)", len(clearances))
		if err = yieldDevelopment(call, p.journal, review, policy.EnsureDefensiveLayout); err != nil {
			return RoutineDefenseLayoutResult{}, false, err
		}
		return RoutineDefenseLayoutResult{Reason: BuildingMethodExhausted}, true, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", defenseCoverPrefix, tick))
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-defense-cover-%x", digest[:16]))
	actions := make([]domain.Action, 0, len(clearances))
	for i, clearance := range clearances {
		action, err := domain.NewCoverClearanceAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), clearance)
		if err != nil {
			return RoutineDefenseLayoutResult{}, false, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineDefenseLayoutResult{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineDefenseLayoutResult{}, false, err
	}
	now := r.reviewer.clock.Now()
	if p.session.State() != state || now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoutineDefenseLayoutResult{}, false, defenseControlErr(360)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineDefenseLayoutResult{}, false, err
	}
	for _, clearance := range clearances {
		clockSchedulerLog("defense-layout: clear cover %s (%s) at %v by %s (%s)", clearance.Thing(), clearance.Definition(), clearance.Cell(), clearance.Designation(), method)
	}
	return RoutineDefenseLayoutResult{Reason: BuildingMethodAdmitted, Plan: id}, true, nil
}

// defenseCensusSummary is the census in one line for a sector-less or
// cover-less review: whether Home is passable and how many border cells
// are passable and reach the map edge, which is what the sectors need.
func defenseCensusSummary(request policy.DefenseRequest, byCell map[domain.Cell]bridge.DefenseCell) string {
	reg := request.Region
	home, homeKnown := byCell[request.Home]
	borderPassable, borderEdge := 0, 0
	for c, cell := range byCell {
		if c.X != reg.X && c.Z != reg.Z && c.X != reg.X+reg.Width-1 && c.Z != reg.Z+reg.Height-1 {
			continue
		}
		if cell.Passable {
			borderPassable++
		}
		if cell.EdgeReachable {
			borderEdge++
		}
	}
	return fmt.Sprintf("census cells %d, home %v known %v passable %v, border passable %d edge_reachable %d", len(byCell), request.Home, homeKnown, home.Passable, borderPassable, borderEdge)
}
