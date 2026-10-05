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

func (r *RoundsResourcePlanner) deepDrill(call, epoch context.Context, state ControlState, goal store.StandardState, review store.Rounds, started time.Time) (RoundsResourceResult, bool, error) {
	if len(policy.DeepDrillingResearch(nil, review.ResourceRunwayState())) == 0 {
		return RoundsResourceResult{}, false, nil
	}
	native, ok := r.native.(deepDrillBuildingSource)
	if !ok {
		return RoundsResourceResult{}, false, nil
	}
	id, _, err := native.Identity(call)
	if err != nil {
		return RoundsResourceResult{}, true, err
	}
	expected, err := observation.DecodeIdentity(id)
	if err != nil || !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsResourceResult{}, true, fmt.Errorf("%w: deepDrill: err != nil || !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	reading, err := r.reviewer.observeColony(call, native, expected, []string{"DeepDrill"})
	if err != nil {
		return RoundsResourceResult{}, true, err
	}
	f := reading.Projection
	research, _, err := native.ReadResearch(call, boundary.Identity(state.Snapshot))
	if err != nil {
		return RoundsResourceResult{}, true, err
	}
	if _, err := boundary.Context(research.Context, state.Snapshot); err != nil || !roundsCachedFresh(bridge.FactResearch, domain.Tick(research.Context.GetTick()), f.Identity.Tick) {
		return RoundsResourceResult{}, true, fmt.Errorf("%w: deepDrill: err != nil || !roundsCachedFresh(bridge.FactResearch, domain.Tick(research.Context.GetTick()), f.Identity", ErrControl)
	}
	finished := policy.ResearchFacts{}
	for _, name := range research.Finished {
		finished.Finished = append(finished.Finished, policy.ResearchProjectID(name))
	}
	f.Facts.Research = domain.Known(finished)
	recordStepRead("deepdrill", policy.MaintainResource, state.Snapshot, f)
	if result, handled, err := r.removeExhaustedDrill(call, epoch, state, goal, f, started); err != nil || handled {
		return result, handled, err
	}
	if len(deepDrillSites(f, review.ResourceRunwayState())) == 0 {
		return RoundsResourceResult{}, false, nil
	}
	buildings, _, err := native.ReadBuildings(call, boundary.Identity(state.Snapshot))
	if err != nil {
		return RoundsResourceResult{}, true, err
	}
	if _, err := boundary.Context(buildings.Context, state.Snapshot); err != nil || !roundsCachedFresh(bridge.FactColony, domain.Tick(buildings.AsOf()), f.Identity.Tick) {
		return RoundsResourceResult{}, true, fmt.Errorf("%w: deepDrill: err != nil || !roundsCachedFresh(bridge.FactColony, domain.Tick(buildings.AsOf()), f.Identity.Tick)", ErrControl)
	}
	// Any remaining drill or drill blueprint holds placement: a working drill
	// is not multiplied, and an exhausted drill already designated for removal
	// leaves the census once demolished.
	for row := range buildings.Rows.Values() {
		if row.GetBuilding().GetDefName() == "DeepDrill" || row.GetBuildDefName() == "DeepDrill" {
			return RoundsResourceResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: stockWaitTicks}, true, nil
		}
	}
	runways := review.ResourceRunwayState()
	center, _ := f.Center().Value()
	for _, site := range deepDrillSites(f, runways) {
		// The drill is one catalog row (#728): a deposit, bill, tree or
		// caravan the other planners bid higher for the same metal wins.
		resource := policy.Resource(site.Definition)
		deficit := int64(0)
		for _, row := range runways {
			if row.Resource == resource {
				stock, _ := f.ResourceStock(resource).Value()
				deficit = row.Target - stock
			}
		}
		if candidate, ok := policy.DeepDrillCandidate(resource, site.Definition, min(site.Count, deficit), math.Hypot(float64(site.Centre.X-center.X), float64(site.Centre.Z-center.Z)), domain.Unknown[int64]()); ok {
			ranked, err := policy.RankResourceCandidates(policy.ResourceDeficitDemand(resource, deficit), []policy.AcquisitionCandidate{candidate}, policy.AcquisitionCompetition{})
			if err != nil {
				return RoundsResourceResult{}, true, err
			}
			score := 0.0
			if len(ranked) > 0 {
				score = ranked[0].Score
			}
			if _, yield := r.reviewer.bids.outranked(state.Snapshot, resource, bidDeepDrill, score, f.Identity.Tick); yield {
				continue
			}
		}
		method := domain.MethodID(fmt.Sprintf("deep-drill-%s-%d-%d", site.Definition, site.Centre.X, site.Centre.Z))
		if _, err := r.reviewer.player.journal.LoadMethod(call, goal.Standard.ID, goal.Standard.Episode, method); err == nil {
			return RoundsResourceResult{Verdict: waitFor(WaitMethodUsed, "deep_drill_method")}, true, nil
		} else if !errors.Is(err, store.ErrNotFound) {
			return RoundsResourceResult{}, true, err
		}
		planID := domain.MintPlanID()
		snapshot := state.Snapshot
		snapshot.Plan, snapshot.Revision = planID, 1
		building, err := domain.NewBuilding("DeepDrill", site.Centre, domain.North, "")
		if err != nil {
			return RoundsResourceResult{}, true, err
		}
		action, err := domain.NewBuildingAction(domain.ActionID(string(planID)+"-0"), building)
		if err != nil {
			return RoundsResourceResult{}, true, err
		}
		preview, _, err := native.PreviewBuilding(call, action, snapshot)
		if err != nil {
			return RoundsResourceResult{}, true, err
		}
		p := preview.Preview
		footprint, fk := p.Footprint.Value()
		legal, lk := p.CanPlace.Value()
		safe, sk := p.SafeToPlace.Value()
		reachable, rk := p.WatchCellsAccessible.Value()
		if !fk || !lk || !legal || !sk || !safe || !rk || !reachable {
			continue
		}
		if !deepDrillFootprint(f.Cells, footprint, site.Centre) {
			continue
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
			return RoundsResourceResult{}, true, fmt.Errorf("%w: deepDrill: player.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
		}
		decision, err := admitMethod(call, player.journal, store.BuildingMethodRequest{Owner: goal, Method: method, Plan: plan, Current: snapshot, Tick: f.Identity.Tick, Bounds: domain.Known(f.Bounds), Stock: preview.Stock, Previews: []policy.Preview{p}, Purpose: policy.Rounds})
		if err != nil {
			return RoundsResourceResult{}, true, err
		}
		if !decision.Admitted {
			return RoundsResourceResult{Verdict: admissionRefused(decision)}, true, nil
		}
		return RoundsResourceResult{Verdict: BuildingReasonAdmitted, Plan: planID}, true, nil
	}
	return RoundsResourceResult{}, false, nil
}
