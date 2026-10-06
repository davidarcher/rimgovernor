package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"math"
	"sort"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

type deepDrillBuildingSource interface {
	RoundsBuildingSource
	ReadBuildings(context.Context, *c.Identity) (bridge.EntityRows[*o.BuildingState], bridge.Result, error)
	ReadResearch(context.Context, *c.Identity) (bridge.ResearchRead, bridge.Result, error)
}

// The native lump centre is an actual discovered resource cell, not the
// arithmetic centroid (which could lie in an empty hole). Never offset it.
func deepDrillSites(f observation.ColonyProjection, runways []policy.ResourceRunway) []observation.DeepResourceLump {
	deep, known := f.DeepResources.Value()
	available, ak := f.DefinitionAvailable("DeepDrill").Value()
	center, planned := f.Center().Value()
	if !planned || !known || policy.ResearchGate([]string{"DeepDrilling", "GroundPenetratingScanner"}, f.Facts.Research) != "" || !ak || !available {
		return nil
	}
	scanner := false
	for _, row := range deep.GroundScanners {
		built, known := row.Built.Value()
		scanner = scanner || row.Definition == "GroundPenetratingScanner" && known && built
	}
	if !scanner {
		return nil
	}
	needed := map[string]bool{}
	for _, row := range runways {
		deficit, known := row.Deficit.Value()
		stock, sk := f.ResourceStock(row.Resource).Value()
		if known && deficit && sk && stock < row.Target && (row.Resource == "Steel" || row.Resource == "Plasteel") {
			needed[string(row.Resource)] = true
		}
	}
	var sites []observation.DeepResourceLump
	for _, lump := range deep.Lumps {
		if needed[lump.Definition] && lump.Count > 0 {
			sites = append(sites, lump)
		}
	}
	distance := func(c domain.Cell) int64 {
		x, z := int64(c.X)-int64(center.X), int64(c.Z)-int64(center.Z)
		return x*x + z*z
	}
	sort.Slice(sites, func(i, j int) bool {
		a, b := sites[i], sites[j]
		if distance(a.Centre) != distance(b.Centre) {
			return distance(a.Centre) < distance(b.Centre)
		}
		if a.Centre.X != b.Centre.X {
			return a.Centre.X < b.Centre.X
		}
		return a.Centre.Z < b.Centre.Z
	})
	return sites
}

// exhaustedDrills selects the colonist drills whose seam the native census
// reads as depleted and that carry no Deconstruct designation yet, by id.
// Unknown depletion and a shifted lump centre (which is not a drill fact at
// all) never qualify; every colonist drill is the controller's.
func exhaustedDrills(deep observation.DeepResources) []observation.DeepDrill {
	var out []observation.DeepDrill
	for _, drill := range deep.Drills {
		depleted, dk := drill.Depleted.Value()
		designated, gk := drill.Designated.Value()
		if dk && depleted && gk && !designated {
			out = append(out, drill)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].ID < out[j].ID })
	return out
}

// removeExhaustedDrill admits one Deconstruction of an exhausted drill through
// the ordinary Hands path; the dispatch guard re-reads depletion before
// designating. Attempts per drill are bounded per episode.
func (r *RoundsResourcePlanner) removeExhaustedDrill(call, epoch context.Context, state ControlState, goal store.StandardState, f observation.ColonyProjection, started time.Time) (RoundsResourceResult, bool, error) {
	deep, known := f.DeepResources.Value()
	if !known {
		return RoundsResourceResult{}, false, nil
	}
	for _, drill := range exhaustedDrills(deep) {
		prefix := fmt.Sprintf("deconstruct-drill-%s-", drill.ID)
		attempt := medicalAttemptCount(goal.History, goal.Standard.Episode, prefix)
		if attempt >= maxMedicalAttemptsPerPatient {
			continue
		}
		method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
		planID := domain.MintPlanID()
		value, err := domain.NewDeconstruction(drill.ID, drill.Definition, drill.Position)
		if err != nil {
			return RoundsResourceResult{}, true, err
		}
		action, err := domain.NewDeconstructionAction(domain.ActionID(string(planID)+"-0"), value)
		if err != nil {
			return RoundsResourceResult{}, true, err
		}
		plan, err := domain.NewPlan(planID, 1, []domain.Action{action})
		if err != nil {
			return RoundsResourceResult{}, true, err
		}
		player := r.reviewer.player
		if err = player.current(call, epoch); err != nil {
			return RoundsResourceResult{}, true, err
		}
		elapsed := r.reviewer.clock.Now().Sub(started)
		if player.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
			return RoundsResourceResult{}, true, fmt.Errorf("%w: removeExhaustedDrill: player.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
		}
		if _, err = player.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
			return RoundsResourceResult{}, true, err
		}
		return RoundsResourceResult{Verdict: BuildingReasonAdmitted, Plan: planID}, true, nil
	}
	return RoundsResourceResult{}, false, nil
}

func deepDrillFootprint(cells []policy.SiteCell, footprint []domain.Cell, anchor domain.Cell) bool {
	if len(footprint) == 0 {
		return false
	}
	seen := map[domain.Cell]policy.SiteCell{}
	for _, row := range cells {
		seen[row.Cell] = row
	}
	onLump := false
	for _, cell := range footprint {
		row, exists := seen[cell]
		roof, rk := row.Roofed.Value()
		occupied, ok := row.Occupied.Value()
		walkable, wk := row.Walkable.Value()
		if !exists || !rk || roof || !ok || occupied || !wk || !walkable {
			return false
		}
		onLump = onLump || cell == anchor
	}
	return onLump
}

// deepDrillPlacement is one site a drill can be placed on: the candidate and
// method id, the lump, and what shared building admission needs.
type deepDrillPlacement struct {
	id       string
	site     observation.DeepResourceLump
	distance float64
	snapshot domain.GenerationSnapshot
	plan     domain.PlanSpec
	preview  bridge.BuildingPreview
}

// deepDrillReading is the Round's drill facts: the gate's reads, the lumps of
// the metals in deficit and, while no drill stands, the sites a preview
// accepts, nearest first. The Round's supply plan prices those sites as
// candidates and deepDrill places the ones the plan opened.
type deepDrillReading struct {
	native deepDrillBuildingSource
	f      observation.ColonyProjection
	sites  []observation.DeepResourceLump
	// built: a drill or drill blueprint stands, so no further drill is placed.
	built     bool
	placeable []deepDrillPlacement
}

// deepDrillReading reads the drill gate; nil when no metal runway is in
// deficit, the native cannot drill, or no lump of a needed metal is scanned.
func (r *RoundsResourcePlanner) deepDrillReading(call context.Context, state ControlState, review store.Rounds) (*deepDrillReading, error) {
	if len(policy.DeepDrillingResearch(nil, review.ResourceRunwayState())) == 0 {
		return nil, nil
	}
	native, ok := r.native.(deepDrillBuildingSource)
	if !ok {
		return nil, nil
	}
	id, _, err := native.Identity(call)
	if err != nil {
		return nil, err
	}
	expected, err := observation.DecodeIdentity(id)
	if err != nil || !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return nil, fmt.Errorf("%w: deepDrillReading: err != nil || !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	reading, err := r.reviewer.observeColony(call, native, expected, []string{"DeepDrill"})
	if err != nil {
		return nil, err
	}
	f := reading.Projection
	research, _, err := native.ReadResearch(call, boundary.Identity(state.Snapshot))
	if err != nil {
		return nil, err
	}
	if _, err := boundary.Context(research.Context, state.Snapshot); err != nil || !roundsCachedFresh(bridge.FactResearch, domain.Tick(research.Context.GetTick()), f.Identity.Tick) {
		return nil, fmt.Errorf("%w: deepDrillReading: err != nil || !roundsCachedFresh(bridge.FactResearch, domain.Tick(research.Context.GetTick()), f.Identity", ErrControl)
	}
	finished := policy.ResearchFacts{}
	for _, name := range research.Finished {
		finished.Finished = append(finished.Finished, policy.ResearchProjectID(name))
	}
	f.Facts.Research = domain.Known(finished)
	recordStepRead("deepdrill", policy.MaintainResource, state.Snapshot, f)
	out := &deepDrillReading{native: native, f: f}
	out.sites = deepDrillSites(f, review.ResourceRunwayState())
	if len(out.sites) == 0 {
		return out, nil
	}
	buildings, _, err := native.ReadBuildings(call, boundary.Identity(state.Snapshot))
	if err != nil {
		return nil, err
	}
	if _, err := boundary.Context(buildings.Context, state.Snapshot); err != nil || !roundsCachedFresh(bridge.FactColony, domain.Tick(buildings.AsOf()), f.Identity.Tick) {
		return nil, fmt.Errorf("%w: deepDrillReading: err != nil || !roundsCachedFresh(bridge.FactColony, domain.Tick(buildings.AsOf()), f.Identity.Tick)", ErrControl)
	}
	// Any remaining drill or drill blueprint holds placement: a working drill
	// is not multiplied, and an exhausted drill already designated for removal
	// leaves the census once demolished.
	for row := range buildings.Rows.Values() {
		if row.GetBuilding().GetDefName() == "DeepDrill" || row.GetBuildDefName() == "DeepDrill" {
			out.built = true
			return out, nil
		}
	}
	center, _ := f.Center().Value()
	for _, site := range out.sites {
		placement, ok, err := r.deepDrillPlacement(call, native, state, f, site, math.Hypot(float64(site.Centre.X-center.X), float64(site.Centre.Z-center.Z)))
		if err != nil {
			return nil, err
		}
		if ok {
			out.placeable = append(out.placeable, placement)
		}
	}
	return out, nil
}

// deepDrillPlacement previews a drill over the lump's centre; ok is false when
// native refuses it, it is unreachable or its footprint is not clear.
func (r *RoundsResourcePlanner) deepDrillPlacement(call context.Context, native deepDrillBuildingSource, state ControlState, f observation.ColonyProjection, site observation.DeepResourceLump, distance float64) (deepDrillPlacement, bool, error) {
	planID := domain.MintPlanID()
	snapshot := state.Snapshot
	snapshot.Plan, snapshot.Revision = planID, 1
	building, err := domain.NewBuilding("DeepDrill", site.Centre, domain.North, "")
	if err != nil {
		return deepDrillPlacement{}, false, err
	}
	action, err := domain.NewBuildingAction(domain.ActionID(string(planID)+"-0"), building)
	if err != nil {
		return deepDrillPlacement{}, false, err
	}
	preview, _, err := native.PreviewBuilding(call, action, snapshot)
	if err != nil {
		return deepDrillPlacement{}, false, err
	}
	p := preview.Preview
	footprint, fk := p.Footprint.Value()
	legal, lk := p.CanPlace.Value()
	safe, sk := p.SafeToPlace.Value()
	reachable, rk := p.WatchCellsAccessible.Value()
	if !fk || !lk || !legal || !sk || !safe || !rk || !reachable || !deepDrillFootprint(f.Cells, footprint, site.Centre) {
		return deepDrillPlacement{}, false, nil
	}
	plan, err := domain.NewPlan(planID, 1, []domain.Action{action})
	if err != nil {
		return deepDrillPlacement{}, false, err
	}
	return deepDrillPlacement{id: fmt.Sprintf("deep-drill-%s-%d-%d", site.Definition, site.Centre.X, site.Centre.Z), site: site, distance: distance, snapshot: snapshot, plan: plan, preview: preview}, true, nil
}

// deepDrill removes an exhausted drill and places the drill the Round's supply
// plan opened: the plan decides whether a lump beats the mines, bills and
// caravans for the metal; this step only executes it.
func (r *RoundsResourcePlanner) deepDrill(call, epoch context.Context, state ControlState, goal store.StandardState, review store.Rounds, started time.Time) (RoundsResourceResult, bool, error) {
	if len(policy.DeepDrillingResearch(nil, review.ResourceRunwayState())) == 0 {
		return RoundsResourceResult{}, false, nil
	}
	supply, err := r.reviewer.resourceSupply(call, state, review, goal)
	if err != nil {
		return RoundsResourceResult{}, true, err
	}
	drill := supply.drill
	if drill == nil {
		return RoundsResourceResult{}, false, nil
	}
	if result, handled, err := r.removeExhaustedDrill(call, epoch, state, goal, drill.f, started); err != nil || handled {
		return result, handled, err
	}
	if len(drill.sites) == 0 {
		return RoundsResourceResult{}, false, nil
	}
	if drill.built {
		return RoundsResourceResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: stockWaitTicks}, true, nil
	}
	for _, e := range supply.plan.Plan.Portfolio {
		if e.Candidate.Kind != policy.CandidateDeepDrill || e.Decision != policy.SupplyOpen {
			continue
		}
		place, ok := drill.place(e.Candidate.ID)
		if !ok {
			continue
		}
		if _, err := r.reviewer.player.journal.LoadMethod(call, goal.Standard.ID, goal.Standard.Episode, domain.MethodID(place.id)); err == nil {
			return RoundsResourceResult{Verdict: waitFor(WaitMethodUsed, "deep_drill_method")}, true, nil
		} else if !errors.Is(err, store.ErrNotFound) {
			return RoundsResourceResult{}, true, err
		}
		player := r.reviewer.player
		if err = player.current(call, epoch); err != nil {
			return RoundsResourceResult{}, true, err
		}
		elapsed := r.reviewer.clock.Now().Sub(started)
		if player.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
			return RoundsResourceResult{}, true, fmt.Errorf("%w: deepDrill: player.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
		}
		decision, err := admitMethod(call, player.journal, store.BuildingMethodRequest{Owner: goal, Method: domain.MethodID(place.id), Plan: place.plan, Current: place.snapshot, Tick: drill.f.Identity.Tick, Bounds: domain.Known(drill.f.Bounds), Stock: place.preview.Stock, Previews: []policy.Preview{place.preview.Preview}, Purpose: policy.Rounds})
		if err != nil {
			return RoundsResourceResult{}, true, err
		}
		if !decision.Admitted {
			return RoundsResourceResult{Verdict: admissionRefused(decision)}, true, nil
		}
		return RoundsResourceResult{Verdict: BuildingReasonAdmitted, Plan: place.plan.ID()}, true, nil
	}
	return RoundsResourceResult{}, false, nil
}

func (d *deepDrillReading) place(id string) (deepDrillPlacement, bool) {
	for _, p := range d.placeable {
		if p.id == id {
			return p, true
		}
	}
	return deepDrillPlacement{}, false
}
