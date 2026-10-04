package buildingruntime

import (
	"context"
	"fmt"
	"log/slog"
	"slices"
	"strings"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// NewRoundsFlooringPlanner composes MaintainFlooring's building method: lay
// a role-appropriate floor on the cells native measures short of their
// room's requirement (issue #6 slice 4). Completion is the next measured
// census, not the build receipts: the review releases a room only once no
// cell of it reads deficient.
func NewRoundsFlooringPlanner(reviewer *Rounder, native RoundsBuildingSource) (*RoundsBuildingPlanner, error) {
	if reviewer == nil || native == nil || !reviewer.methodEnabled(policy.MaintainFlooring) {
		return nil, fmt.Errorf("%w: NewRoundsFlooringPlanner: reviewer == nil || native == nil || !reviewer.methodEnabled(policy.MaintainFlooring)", ErrControl)
	}
	if _, ok := native.(observation.RoundsSource); !ok {
		return nil, fmt.Errorf("%w: NewRoundsFlooringPlanner: !ok", ErrControl)
	}
	return &RoundsBuildingPlanner{reviewer: reviewer, native: native, concern: policy.MaintainFlooring, definition: "WoodPlankFloor"}, nil
}

// SetFirebreakPave supplies the firebreak tier's cells: pave reports the
// firebreak ring's pave cells that are still natural ground with no floor
// ordered, read at each review.
func (r *RoundsBuildingPlanner) SetFirebreakPave(pave func() []domain.Cell) {
	r.firebreakPave = pave
}

// incineratorFloor reads the terrain under the standing incinerator's
// interior (#1821): the native flooring census lists only roofed rooms, so
// the nine cells come from one defense-site read. Nil when no incinerator
// stands, the source cannot read a site, or a cell is fogged or stands on
// terrain the catalog does not price.
func (r *RoundsBuildingPlanner) incineratorFloor(call context.Context, current domain.GenerationSnapshot, facts observation.ColonyProjection, terrains map[string]policy.FloorTerrain) ([]policy.FloorCell, error) {
	room := standingIncinerator(facts)
	reader, ok := r.native.(interface {
		ReadDefenseSite(context.Context, *c.Identity, bridge.CellRect) (bridge.DefenseSite, bridge.Result, error)
	})
	if room == nil || !ok {
		return nil, nil
	}
	in := room.Interior
	region := bridge.CellRect{Min: domain.Cell{X: in.X, Z: in.Z}, Max: domain.Cell{X: in.X + in.Width - 1, Z: in.Z + in.Height - 1}}
	site, _, err := reader.ReadDefenseSite(call, boundary.Identity(current), region)
	if err != nil {
		return nil, err
	}
	if _, err = boundary.Context(site.Context, current); err != nil || domain.Tick(site.Context.GetTick()) < facts.Identity.Tick {
		return nil, fmt.Errorf("%w: incinerator floor: defense site context", ErrControl)
	}
	var out []policy.FloorCell
	for _, cell := range site.Cells {
		if _, priced := terrains[cell.Terrain]; cell.Fogged || !priced {
			return nil, nil
		}
		out = append(out, policy.FloorCell{Cell: cell.Cell, Terrain: cell.Terrain})
	}
	return out, nil
}

// flooringDefinitions lists every floor the policy may choose so the census
// read carries each one's availability, stats and cost list, the entry floors included.
func (r *RoundsBuildingPlanner) flooringDefinitions() []string {
	p := r.reviewer.policy.Flooring
	names := append([]string(nil), p.Floors...)
	for _, name := range p.EntryFloors {
		if !slices.Contains(names, name) {
			names = append(names, name)
		}
	}
	return names
}

// selectFlooring re-reviews the fresh census under the review's latch and
// maps the policy outcome onto the planner: a build resolves the floor
// definition and its cells, everything else is a reason.
func (r *RoundsBuildingPlanner) selectFlooring(call context.Context, current domain.GenerationSnapshot, facts observation.ColonyProjection, latches policy.RoundsLatches) (*RoundsBuildingPlanner, Verdict, error) {
	p := r.reviewer.policy.Flooring
	census := trafficFlooringFacts(facts, p)
	if v, known := census.Value(); known {
		if r.firebreakPave != nil {
			v.Firebreak = r.firebreakPave()
		}
		var err error
		if v.Incinerator, err = r.incineratorFloor(call, current, facts, v.Terrains); err != nil {
			return nil, Verdict{}, err
		}
		census = domain.Known(v)
	}
	logTrafficFindings(census)
	review, err := policy.ReviewFlooring(census, facts.Rooms, latches.Flooring, p)
	if err != nil {
		return nil, Verdict{}, err
	}
	if !review.Active {
		return nil, BuildingReasonNoDeficit, nil
	}
	if !review.Known {
		return nil, fieldUnavailable("flooring"), nil
	}
	flooring := policy.FlooringFacts{Definitions: map[string]policy.FloorDefinition{}, Stock: facts.Resources, Style: floorStyle(facts)}
	for _, d := range facts.Definitions {
		flooring.Definitions[d.Name] = policy.FloorDefinition{Available: d.Available, Terrain: d.Terrain, Cleanliness: d.Cleanliness, Beauty: d.Beauty, Flammability: d.Flammability, PathCost: d.PathCost, Costs: d.Costs, WorkToBuild: d.WorkToBuild, Tags: d.FloorTags}
	}
	proposal, err := policy.SelectFlooringMethod(review, flooring, p)
	if err != nil {
		return nil, Verdict{}, err
	}
	if clockDebug() {
		clockSchedulerLog("flooring: review=%+v definitions=%d stock=%+v proposal=%+v", review, len(flooring.Definitions), flooring.Stock, proposal)
	}
	switch proposal.Method {
	case policy.FlooringBuild:
		resolved := *r
		resolved.flooring = &proposal
		resolved.definition = proposal.Definition
		return &resolved, Verdict{}, nil
	case policy.FlooringUnknown:
		return nil, fieldUnavailable("flooring"), nil
	case policy.FlooringNoMethod:
		return nil, BuildingReasonNoDeficit, nil
	default:
		return nil, awaitingMethod(proposal.Method), nil
	}
}

// previewFlooring previews the proposal's cells in order and admits every
// one native reports legal and safe; a floor has no rotation and a
// one-cell footprint. Cells native refuses (a wall, an identical floor laid
// meanwhile) are skipped; the batch is the cells that remain.
func (r *RoundsBuildingPlanner) previewFlooring(ctx context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, check func() error) ([]policy.Preview, policy.StockObservation, Verdict, error) {
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	if r.flooring == nil || r.flooring.Method != policy.FlooringBuild {
		return nil, stock, Verdict{}, fmt.Errorf("%w: previewFlooring: r.flooring == nil || r.flooring.Method != policy.FlooringBuild", ErrControl)
	}
	guarded := map[domain.Cell]bool{}
	for _, c := range protected {
		guarded[c] = true
	}
	// Listed natural rock on a floor cell is the floor already: the rock step
	// leaves it, so no floor is ordered or previewed. A cell the census does
	// not list is the native preview's to judge (the census is a window, not
	// the whole map), so it is not classified here.
	listed := make(map[domain.Cell]bool, len(facts.Cells))
	for _, c := range facts.Cells {
		listed[c.Cell] = true
	}
	var role []policy.RoleCell
	for _, cell := range r.flooring.Cells {
		if listed[cell] {
			role = append(role, policy.RoleCell{Cell: cell, Role: policy.RockBlocks})
		}
	}
	for _, cell := range policy.RockStep(role, facts.Cells).Left {
		guarded[cell] = true
	}
	var selected []policy.Preview
	unknown := false
	for _, cell := range r.flooring.Cells {
		if guarded[cell] {
			continue
		}
		building, err := domain.NewBuilding(r.flooring.Definition, cell, domain.North, "")
		if err != nil {
			return nil, stock, Verdict{}, err
		}
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, len(selected))), building)
		if err != nil {
			return nil, stock, Verdict{}, err
		}
		preview, _, err := r.native.PreviewBuilding(ctx, action, snapshot)
		if err != nil {
			return nil, stock, Verdict{}, err
		}
		p := preview.Preview
		footprint, fk := p.Footprint.Value()
		made, mk := p.MadeFromStuff.Value()
		legal, lk := p.CanPlace.Value()
		safe, sk := p.SafeToPlace.Value()
		if !fk || !mk || !lk || !sk {
			unknown = true
			continue
		}
		if made || len(footprint) != 1 || footprint[0] != cell || !legal || !safe {
			if clockDebug() {
				clockSchedulerLog("flooring: cell %v refused legal=%v safe=%v footprint=%v", cell, legal, safe, footprint)
			}
			continue
		}
		if err = mergeRoundsStock(&stock, preview.Stock, len(selected) == 0); err != nil {
			return nil, stock, Verdict{}, err
		}
		selected = append(selected, p)
	}
	if len(selected) > 0 {
		if clockDebug() {
			clockSchedulerLog("flooring: %d cells previewed for %s in room %s stock=%+v", len(selected), r.flooring.Definition, r.flooring.Room, stock.Values)
		}
		return selected, stock, Verdict{}, nil
	}
	if unknown {
		return nil, stock, fieldUnavailable("flooring_preview"), nil
	}
	return nil, stock, noSpace("floor_cells"), nil
}

// trafficFindingsLogged is the last finding set logged, so the service log
// names a flagged room when it changes rather than on every review.
var trafficFindingsLogged struct {
	sync.Mutex
	last string
}

// logTrafficFindings flags thoroughfares and animals in clean rooms from
// the traffic layers (#817) in the service log; no planner acts on them yet.
func logTrafficFindings(fact domain.Fact[policy.FlooringObservation]) {
	v, known := fact.Value()
	if !known {
		return
	}
	var lines []string
	for _, f := range policy.TrafficFindings(v) {
		lines = append(lines, f.String())
	}
	joined := strings.Join(lines, "; ")
	trafficFindingsLogged.Lock()
	changed := joined != trafficFindingsLogged.last
	trafficFindingsLogged.last = joined
	trafficFindingsLogged.Unlock()
	if !changed || joined == "" {
		return
	}
	slog.Default().Info("traffic findings: "+joined, telemetry.ComponentKey, "routine-flooring", telemetry.KindKey, "traffic_finding")
}

// trafficFlooringFacts adds what the traffic tier prices its floor from
// (#950) to the flooring census: every policy floor's planning row, the
// accessible stock and the tier style's aisle floor. An unknown census
// stays unknown.
func trafficFlooringFacts(facts observation.ColonyProjection, p policy.FlooringPolicy) domain.Fact[policy.FlooringObservation] {
	v, known := facts.Facts.Upkeep.Flooring.Value()
	if !known {
		return facts.Facts.Upkeep.Flooring
	}
	v.Floors = map[string]policy.FloorDefinition{}
	for _, d := range facts.Definitions {
		if slices.Contains(p.Floors, d.Name) {
			v.Floors[d.Name] = policy.FloorDefinition{Available: d.Available, Terrain: d.Terrain, Cleanliness: d.Cleanliness, Beauty: d.Beauty, Flammability: d.Flammability, PathCost: d.PathCost, Costs: d.Costs, WorkToBuild: d.WorkToBuild, Tags: d.FloorTags}
		}
	}
	v = withThroneFloor(facts, v)
	v.Stock = facts.Resources
	if style := floorStyle(facts); style != nil {
		v.TrafficStyle, _ = style(policy.RoomRoleNone)
	}
	return domain.Known(v)
}
