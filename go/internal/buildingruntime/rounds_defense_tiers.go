package buildingruntime

import (
	"context"
	"fmt"
	"log/slog"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// defenseCensusRequest is the census the layout planners site from, read off
// the projection's cell mirror and the observed gates: the cells of region,
// the colony doors, the arrivals, the ranged defenders, the definitions and
// the turret and IED gates. Not ok with a held verdict when there is no ranged
// defender to site the firing line for.
func (r *RoundsDefenseLayoutPlanner) defenseCensusRequest(call context.Context, state ControlState, read observation.RoundsReading, killbox policy.DefenseKillbox, region bridge.CellRect, home domain.Cell) (policy.DefenseRequest, Verdict, bool, error) {
	projection := read.Projection
	identity := boundary.Identity(state.Snapshot)
	site, err := r.defenseSite(call, identity, projection, region)
	if err != nil {
		return policy.DefenseRequest{}, Verdict{}, false, err
	}
	request := defenseTurretRequest(read)
	request.Bounds, request.Home, request.Killbox = projection.Bounds, home, killbox
	// Observed arrivals rank the sectors; the bait tier stands only on a
	// sector a raid used.
	request.Arrivals, request.Tick = defenseArrivals(projection.Raids, region), projection.Identity.Tick
	request.Region = policy.Rectangle{X: region.Min.X, Z: region.Min.Z, Width: region.Max.X - region.Min.X + 1, Height: region.Max.Z - region.Min.Z + 1}
	for _, cell := range site.Cells {
		request.Cells = append(request.Cells, defenseCellFacts(cell))
		if !cell.Fogged && cell.Door && cell.PlayerOwned {
			request.Entrances = append(request.Entrances, cell.Cell)
		}
	}
	defenders, minRange, ok, err := r.defenderRange(call, state, read)
	if err != nil || !ok {
		if err != nil {
			return policy.DefenseRequest{}, Verdict{}, false, err
		}
		return policy.DefenseRequest{}, noWorker("ranged_defender"), false, nil
	}
	request.Defenders, request.MinRange = defenders, minRange
	stock, stockKnown := projection.Resources.Value()
	request.Definitions = policy.DefenseCoverChoice(projection.Facts.Items, request.Definitions, stock, stockKnown,
		defenseDefinitionAvailable(read, policy.DefenseSandbags), defenseDefinitionAvailable(read, policy.DefenseEmbrasure), defenders)
	defenseIEDRequest(read, &request)
	if err = r.markWallSupport(call, identity, &request, site.Cells); err != nil {
		return policy.DefenseRequest{}, Verdict{}, false, err
	}
	return request, Verdict{}, true, nil
}

// defenseTierPins are the record's built killbox tiers, for PlanDefenseTiers:
// the placements and reserved cells of each tier whose buildings all stand,
// with the corridor and firing line geometry the proposal observed. A
// perimeter section, a removal or a turret replacement is not a killbox tier.
func defenseTierPins(record store.DefenseLayoutRecord) policy.DefenseTierPins {
	pins := policy.DefenseTierPins{Tiers: map[policy.DefenseTierName]policy.DefenseTier{}, TrapLane: slices.Clone(record.TrapLane)}
	for i, cell := range record.Firing {
		position := policy.FiringPosition{Cell: cell, Cover: cell}
		if len(record.Retreat) == len(record.Firing) {
			position.Retreat = record.Retreat[i]
		}
		pins.Firing = append(pins.Firing, position)
	}
	for _, t := range record.Tiers {
		if !t.Built || t.Remove || t.NodeBacked() || policy.IsPerimeterTier(t.Name) {
			continue
		}
		_, buildings, ok := record.Tier(t.Name)
		if !ok {
			continue
		}
		pins.Tiers[t.Name] = policy.DefenseTier{Name: t.Name, Buildings: buildings, Reserved: slices.Clone(t.Reserved)}
	}
	return pins
}

// tierOverlay is the tier nodes the defense round last cut, for the layout
// overlay to draw.
type tierOverlay struct {
	key   string
	nodes []policy.PlannedSite
}

// adviseTiers cuts the plan's killbox into the defense tier nodes from the
// round's census and the record's built tiers (#2973), holds them for the
// overlay and journals the change as layout_plan rows. Advice only: the
// layout planners above stay authoritative, and a failure here never fails
// the round.
func (r *RoundsDefenseLayoutPlanner) adviseTiers(call context.Context, state ControlState, read observation.RoundsReading, record store.DefenseLayoutRecord) {
	plan, known := read.Projection.LayoutPlan.Value()
	if !known {
		return
	}
	killbox, region, home, ok := defenseKillbox(read.Projection)
	if !ok {
		return
	}
	census, _, ok, err := r.defenseCensusRequest(call, state, read, killbox, region, home)
	if err != nil || !ok {
		return
	}
	census.Mortar = defenseMortarRequest(read).Mortar
	census.UnitCosts[defenseMortarDefinition] = defenseMortarRequest(read).UnitCosts[defenseMortarDefinition]
	tiers, err := policy.PlanDefenseTiers(plan, census, defenseTierPins(record))
	if err != nil {
		return
	}
	key := tierKey(tiers.Nodes)
	r.reviewer.tiers.Store(&tierOverlay{key: key, nodes: tiers.Nodes})
	if key == r.tiersLogged {
		return
	}
	r.tiersLogged = key
	for _, d := range tiers.Decisions {
		telemetry.Decide(call, telemetry.Decision{Kind: "layout_plan", Component: "layout", Level: slog.LevelInfo, Verdict: d.Verdict, Reason: d.Reason, Target: "defense_tiers",
			Attrs: map[string]any{"node": d.Node, "nodes": len(tiers.Nodes), "why": tiers.Refusal}})
	}
}

// tierKey digests the tier nodes: their ids, kinds, pinned state and cells.
func tierKey(nodes []policy.PlannedSite) string {
	key := ""
	for _, n := range nodes {
		key += fmt.Sprintf("%s/%v/%v;", n.Node, n.Tier != nil && n.Tier.Pinned, n.Footprint.Runs)
	}
	return key
}
