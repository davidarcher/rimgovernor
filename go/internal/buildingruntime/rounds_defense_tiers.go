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
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
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
	mortar := defenseMortarRequest(read)
	request.Mortar, request.UnitCosts[defenseMortarDefinition] = mortar.Mortar, mortar.UnitCosts[defenseMortarDefinition]
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

// defenseTierStarted reports a tier the plan must keep where it is (#2973): it
// was built, an attempt was admitted for it, a lost building re-opened it, or
// any of its buildings stands in the census. A tier mid-construction would
// otherwise re-site on every census.
func defenseTierStarted(t store.DefenseTierRecord, census *defenseCensus) bool {
	if t.Built || t.Attempts > 0 || t.Reopened > 0 {
		return true
	}
	return census != nil && slices.ContainsFunc(t.Buildings, func(b store.DefenseBuilding) bool { return census.standing(b.Definition, b.Cell) })
}

// defenseTierPins are the record's started killbox tiers, for PlanDefenseTiers:
// the placements and reserved cells of each tier that is built, has an admitted
// attempt or has a building standing (defenseTierStarted), with the corridor and
// firing line geometry the proposal observed. A perimeter section, a removal or
// a turret replacement is not a killbox tier.
func defenseTierPins(record store.DefenseLayoutRecord, census *defenseCensus) policy.DefenseTierPins {
	pins := policy.DefenseTierPins{Tiers: map[policy.DefenseTierName]policy.DefenseTier{}, TrapLane: slices.Clone(record.TrapLane)}
	for i, cell := range record.Firing {
		position := policy.FiringPosition{Cell: cell, Cover: cell}
		if len(record.Retreat) == len(record.Firing) {
			position.Retreat = record.Retreat[i]
		}
		pins.Firing = append(pins.Firing, position)
	}
	for _, t := range record.Tiers {
		if !defenseTierStarted(t, census) || t.Remove || t.NodeBacked() || policy.IsPerimeterTier(t.Name) {
			continue
		}
		_, buildings, ok := record.Tier(t.Name)
		if !ok {
			continue
		}
		pins.Tiers[t.Name] = policy.DefenseTier{Name: t.Name, Buildings: buildings, Reserved: slices.Clone(t.Reserved)}
	}
	// The corridor is the ground every other tier stands on: it stays where it is
	// once any tier is started.
	if choke, _, ok := record.Tier(policy.TierChokepoint); ok && len(pins.Tiers) > 0 {
		pins.Tiers[policy.TierChokepoint] = policy.DefenseTier{Name: policy.TierChokepoint, Reserved: slices.Clone(choke.Reserved)}
	}
	return pins
}

// defenseKillboxTiers are the record's tiers the plan's killbox node is cut
// into (policy.PlanDefenseTiers), in the order they are built.
var defenseKillboxTiers = []policy.DefenseTierName{policy.TierChokepoint, policy.TierFiringLine, policy.TierFunnel, policy.TierTrapCorridor, policy.TierTurrets, policy.TierMortars, policy.TierBait, policy.TierIEDs}

// siteUnstartedTiers sets the record's killbox tiers that are not pinned to the
// tiers the census sites (#2973): an unstarted tier moves with the census, one
// the census no longer sites leaves the record, a new one joins it, and the
// geometry the proposal observed (the trap lane, the firing line and its
// retreat) follows the tiers that define it. A refusal of the layout planner, or
// a layout without a firing position, changes nothing. It reports whether the
// record changed.
func siteUnstartedTiers(record *store.DefenseLayoutRecord, tiers policy.DefenseTiers, pins policy.DefenseTierPins) bool {
	if tiers.Refusal != "" || len(tiers.Layout.Firing) == 0 {
		return false
	}
	before := *record
	before.Tiers = slices.Clone(record.Tiers)
	for _, name := range defenseKillboxTiers {
		if _, pinned := pins.Tiers[name]; pinned {
			continue
		}
		sited := slices.IndexFunc(tiers.Layout.Tiers, func(t policy.DefenseTier) bool { return t.Name == name })
		old := slices.IndexFunc(record.Tiers, func(t store.DefenseTierRecord) bool { return t.Name == name })
		switch {
		case sited < 0 && old >= 0:
			record.Tiers = slices.Delete(record.Tiers, old, old+1)
		case sited >= 0:
			record.SetPolicyTier(tiers.Layout.Tiers[sited])
		}
	}
	if _, pinned := pins.Tiers[policy.TierChokepoint]; !pinned {
		record.TrapLane = slices.Clone(tiers.Layout.TrapLane)
	}
	if _, pinned := pins.Tiers[policy.TierFiringLine]; !pinned {
		record.Firing, record.Retreat = nil, nil
		for _, f := range tiers.Layout.Firing {
			record.Firing, record.Retreat = append(record.Firing, f.Cell), append(record.Retreat, f.Retreat)
		}
	}
	return !sameSiting(before, *record)
}

// sameSiting reports two records with the same killbox siting: the geometry and,
// tier by tier, the placements, reservations and progress.
func sameSiting(a, b store.DefenseLayoutRecord) bool {
	if !slices.Equal(a.TrapLane, b.TrapLane) || !slices.Equal(a.Firing, b.Firing) || !slices.Equal(a.Retreat, b.Retreat) || len(a.Tiers) != len(b.Tiers) {
		return false
	}
	for _, t := range a.Tiers {
		i := slices.IndexFunc(b.Tiers, func(u store.DefenseTierRecord) bool { return u.Name == t.Name })
		if i < 0 {
			return false
		}
		u := b.Tiers[i]
		if !slices.Equal(t.Buildings, u.Buildings) || !slices.Equal(t.Reserved, u.Reserved) || t.Attempts != u.Attempts || t.Built != u.Built || t.Reopened != u.Reopened {
			return false
		}
	}
	return true
}

// tierOverlay is the tier nodes the defense round last cut, for the layout
// overlay to draw.
type tierOverlay struct {
	key   string
	nodes []policy.PlannedSite
}

// siteTiers cuts the plan's killbox into the defense tier nodes from the
// round's census and the record's started tiers (#2973), sets the record's
// unstarted tiers to what the nodes site (siteUnstartedTiers), holds the nodes
// for the overlay and journals the change as layout_plan rows. It reports
// a census it cannot cut leaves the record as it was.
func (r *RoundsDefenseLayoutPlanner) siteTiers(call context.Context, state ControlState, read observation.RoundsReading, record *store.DefenseLayoutRecord, tierCensus *defenseCensus) {
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
	pins := defenseTierPins(*record, tierCensus)
	tiers, err := policy.PlanDefenseTiers(plan, census, pins)
	if err != nil {
		return
	}
	siteUnstartedTiers(record, tiers, pins)
	for _, g := range tiers.Layout.Gated {
		defenseTierGated(call, g.Tier, g.Reason)
	}
	r.adviseBlocks(call, state, read, census, pins, tiers)
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

// adviseBlocks prices each pinned tier a tree, chunk or new building blocks
// (clear it or move the tier, the cheaper; downtime unpriced) and journals the
// choice as a layout_plan row with target defense_tier_block. The prices read
// the catalog's work amounts, a colonist's work-day from the pawns' schedules
// and the #2945 work-day price. Advice only.
func (r *RoundsDefenseLayoutPlanner) adviseBlocks(call context.Context, state ControlState, read observation.RoundsReading, census policy.DefenseRequest, pins policy.DefenseTierPins, tiers policy.DefenseTiers) {
	things := policy.BlockedThings(census.Cells, pins)
	if len(things) == 0 {
		r.blocksLogged = ""
		return
	}
	catalog, err := pawnCatalog(call, r.native, boundary.Identity(state.Snapshot))
	if err != nil || catalog == nil {
		return
	}
	rows := map[string]policy.ClearRow{}
	for _, thing := range things {
		if row, ok, err := catalog.ClearRow(thing.Def, thing.Stuff); err == nil && ok {
			rows[policy.ClearKey(thing.Def, thing.Stuff)] = row
		}
	}
	p := read.Projection
	facts := policy.StationFacts{Catalog: p.Stations, Items: p.Facts.Items, Colonists: p.Facts.Colonists, Food: p.FoodSupply, WorkPawns: p.WorkPawns, Crops: growingCrops(p)}
	decisions := policy.PriceTierBlocks(census.Cells, pins, tiers.Sited, policy.NewBlockPricing(facts, census.UnitCosts, rows))
	key := fmt.Sprint(decisions)
	if key == r.blocksLogged {
		return
	}
	r.blocksLogged = key
	for _, d := range decisions {
		verdict := string(d.Choice)
		telemetry.Decide(call, telemetry.Decision{Kind: "layout_plan", Component: "layout", Level: slog.LevelInfo, Verdict: verdict, Reason: d.Reason, Target: "defense_tier_block",
			Attrs: map[string]any{"tier": string(d.Tier), "blocked": len(d.Blocked), "clear_w": d.ClearW, "clear_known": d.ClearKnown, "move_w": d.MoveW, "move_known": d.MoveKnown,
				"downtime": "unpriced"}})
	}
}

// defenseCover is the census cell's removable cover as the policy reads it: the
// thing, its stuff when it is the edifice, and the kind of removal it takes.
func defenseCover(cell bridge.DefenseCell) policy.CoverThing {
	if cell.Cover == nil {
		return policy.CoverThing{}
	}
	thing := policy.CoverThing{Def: cell.Cover.DefName}
	switch cell.Cover.Kind {
	case o.CoverKind_COVER_KIND_PLANT:
		thing.Kind = policy.CoverPlant
	case o.CoverKind_COVER_KIND_CHUNK:
		thing.Kind = policy.CoverChunk
	case o.CoverKind_COVER_KIND_MINEABLE:
		thing.Kind = policy.CoverMineable
	case o.CoverKind_COVER_KIND_BUILDING:
		thing.Kind = policy.CoverBuilding
		if cell.EdificeDefName == thing.Def {
			thing.Stuff = cell.EdificeStuff
		}
	}
	return thing
}

// tierKey digests the tier nodes: their ids, kinds, pinned state and cells.
func tierKey(nodes []policy.PlannedSite) string {
	key := ""
	for _, n := range nodes {
		key += fmt.Sprintf("%s/%v/%v;", n.Node, n.Tier != nil && n.Tier.Pinned, n.Footprint.Runs)
	}
	return key
}
