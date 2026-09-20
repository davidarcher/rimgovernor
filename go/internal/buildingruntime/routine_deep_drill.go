package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
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
	RoutineBuildingSource
	ReadBuildings(context.Context, *c.Identity, int64) (bridge.EntityRows[*o.BuildingState], bridge.Result, error)
	ReadResearch(context.Context, *c.Identity) (bridge.ResearchRead, bridge.Result, error)
}

// The native lump centre is an actual discovered resource cell, not the
// arithmetic centroid (which could lie in an empty hole). Never offset it.
func deepDrillSites(f observation.ColonyProjection, runways []policy.ResourceRunway) []observation.DeepResourceLump {
	deep, known := f.DeepResources.Value()
	available, ak := f.DefinitionAvailable("DeepDrill").Value()
	if !known || policy.ResearchGate([]string{"DeepDrilling", "GroundPenetratingScanner"}, f.Facts.Research) != "" || !ak || !available {
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
		x, z := int64(c.X)-int64(f.Center.X), int64(c.Z)-int64(f.Center.Z)
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

func (r *RoutineResourcePlanner) deepDrill(call, epoch context.Context, state ControlState, goal store.GoalState, review store.RoutineReview, started time.Time) (RoutineResourceResult, bool, error) {
	if len(policy.DeepDrillingResearch(nil, review.ResourceRunwayState())) == 0 {
		return RoutineResourceResult{}, false, nil
	}
	native, ok := r.native.(deepDrillBuildingSource)
	if !ok {
		return RoutineResourceResult{}, false, nil
	}
	id, _, err := native.Identity(call)
	if err != nil {
		return RoutineResourceResult{}, true, err
	}
	expected, err := observation.DecodeIdentity(id)
	if err != nil || !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineResourceResult{}, true, ErrControl
	}
	reading, err := r.reviewer.observeColony(call, native, expected, []string{"DeepDrill"})
	if err != nil {
		return RoutineResourceResult{}, true, err
	}
	f := reading.Projection
	research, _, err := native.ReadResearch(call, boundary.Identity(state.Snapshot))
	if err != nil {
		return RoutineResourceResult{}, true, err
	}
	if _, err := boundary.Context(research.Context, state.Snapshot); err != nil || !domain.Tick(research.Context.GetTick()).FreshFor(f.Identity.Tick) {
		return RoutineResourceResult{}, true, ErrControl
	}
	finished := policy.ResearchFacts{}
	for _, name := range research.Finished {
		finished.Finished = append(finished.Finished, policy.ResearchProjectID(name))
	}
	f.Facts.Research = domain.Known(finished)
	if len(deepDrillSites(f, review.ResourceRunwayState())) == 0 {
		return RoutineResourceResult{}, false, nil
	}
	buildings, _, err := native.ReadBuildings(call, boundary.Identity(state.Snapshot), 0)
	if err != nil {
		return RoutineResourceResult{}, true, err
	}
	if _, err := boundary.Context(buildings.Context, state.Snapshot); err != nil || buildings.Delta || !domain.Tick(buildings.AsOf()).FreshFor(f.Identity.Tick) {
		return RoutineResourceResult{}, true, ErrControl
	}
	// Until per-drill resource observations can distinguish an exhausted seam
	// from a shifted lump centre, retain an existing drill and do not multiply it.
	for _, row := range buildings.Rows {
		if row.GetBuilding().GetDefName() == "DeepDrill" || row.GetBuildDefName() == "DeepDrill" {
			return RoutineResourceResult{Reason: BuildingMethodExistingWork, NativeWorkTicks: stockWaitTicks}, true, nil
		}
	}
	for _, site := range deepDrillSites(f, review.ResourceRunwayState()) {
		method := domain.MethodID(fmt.Sprintf("deep-drill-%s-%d-%d", site.Definition, site.Centre.X, site.Centre.Z))
		if _, err := r.reviewer.player.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
			return RoutineResourceResult{Reason: BuildingMethodUsed}, true, nil
		} else if !errors.Is(err, store.ErrNotFound) {
			return RoutineResourceResult{}, true, err
		}
		digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
		planID := domain.PlanID(fmt.Sprintf("routine-deep-drill-%x", digest[:16]))
		snapshot := state.Snapshot
		snapshot.Plan, snapshot.Revision = planID, 1
		building, err := domain.NewBuilding("DeepDrill", site.Centre, domain.North, "")
		if err != nil {
			return RoutineResourceResult{}, true, err
		}
		action, err := domain.NewBuildingAction(domain.ActionID(string(planID)+"-0"), building)
		if err != nil {
			return RoutineResourceResult{}, true, err
		}
		preview, _, err := native.PreviewBuilding(call, action, snapshot)
		if err != nil {
			return RoutineResourceResult{}, true, err
		}
		p := preview.Preview
		if p.Action != action || !p.Snapshot.Matches(snapshot) || !p.Tick.FreshFor(f.Identity.Tick) || !preview.Stock.Snapshot.Matches(snapshot) || !preview.Stock.Tick.FreshFor(f.Identity.Tick) {
			return RoutineResourceResult{}, true, ErrControl
		}
		footprint, fk := p.Footprint.Value()
		legal, lk := p.CanPlace.Value()
		safe, sk := p.SafeToPlace.Value()
		reachable, rk := p.WatchCellsAccessible.Value()
		if !fk || !lk || !legal || !sk || !safe || !rk || !reachable {
			continue
		}
		cells := f.Cells
		if source := observation.PlanningWindowFrom(call); source != nil {
			minX, minZ, maxX, maxZ := site.Centre.X, site.Centre.Z, site.Centre.X, site.Centre.Z
			for _, cell := range footprint {
				minX = min(minX, cell.X)
				minZ = min(minZ, cell.Z)
				maxX = max(maxX, cell.X)
				maxZ = max(maxZ, cell.Z)
			}
			held, err := source.PlanningWindow(call, boundary.Identity(snapshot), policy.Rectangle{X: minX, Z: minZ, Width: maxX - minX + 1, Height: maxZ - minZ + 1})
			if err != nil {
				return RoutineResourceResult{}, true, err
			}
			if !held.Complete || held.Stale.Any() || !domain.Tick(held.AsOf).FreshFor(f.Identity.Tick) {
				return RoutineResourceResult{}, true, ErrControl
			}
			cells = held.Value.Cells
		}
		if !deepDrillFootprint(cells, footprint, site.Centre) {
			continue
		}
		plan, err := domain.NewPlan(planID, 1, []domain.Action{action})
		if err != nil {
			return RoutineResourceResult{}, true, err
		}
		player := r.reviewer.player
		if err = player.current(call, epoch); err != nil {
			return RoutineResourceResult{}, true, err
		}
		elapsed := r.reviewer.clock.Now().Sub(started)
		if player.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
			return RoutineResourceResult{}, true, ErrControl
		}
		decision, err := player.journal.AdmitBuildingMethod(call, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: method, Plan: plan, Current: snapshot, Tick: f.Identity.Tick, Bounds: domain.Known(f.Bounds), Stock: preview.Stock, Rules: r.reviewer.rules, Previews: []policy.Preview{p}, Purpose: policy.Routine})
		if err != nil {
			return RoutineResourceResult{}, true, err
		}
		if !decision.Admitted {
			return RoutineResourceResult{Reason: BuildingMethodRefused, NativeWorkTicks: stockRefusalWait(decision)}, true, nil
		}
		return RoutineResourceResult{Reason: BuildingMethodAdmitted, Plan: planID}, true, nil
	}
	return RoutineResourceResult{}, false, nil
}
