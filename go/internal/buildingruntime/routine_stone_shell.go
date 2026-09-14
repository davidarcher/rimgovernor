package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// stoneShellCandidateBound mirrors wall_upgrade.py's method(): at most this
// many undedup'd flammable owned walls are considered per tick.
const stoneShellCandidateBound = 8

// RoutineStoneShellSource previews each backup/replacement Wall placement
// and lists current wall-upgrade candidate sites. General colony facts
// (owned construction, the Upkeep stone-structure census, map bounds) come
// from the shared RoutineReviewer.native read instead, the same split
// RoutineFieldPlanner uses between its own FieldNative and the reviewer's
// observation.RoutineSource.
type RoutineStoneShellSource interface {
	ReadWallUpgradeSites(context.Context, *c.Identity, string) (bridge.WallUpgradeSites, bridge.Result, error)
	PreviewBuilding(context.Context, domain.Action, domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error)
}
type RoutineStoneShellPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineStoneShellSource
}
type RoutineStoneShellResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineStoneShellPlanner(reviewer *RoutineReviewer, native RoutineStoneShellSource) (*RoutineStoneShellPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	if _, ok := reviewer.native.(observation.RoutineSource); !ok {
		return nil, ErrControl
	}
	return &RoutineStoneShellPlanner{reviewer, native}, nil
}
func (r *RoutineStoneShellPlanner) Step(ctx context.Context) (RoutineStoneShellResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineStoneShellResult{}, err
	}
	defer done()
	return r.step(call, epoch)
}

func stoneShellMethodID(wall string) domain.MethodID {
	sum := sha256.Sum256([]byte(wall))
	return domain.MethodID(fmt.Sprintf("wall-%x", sum[:16]))
}

func (r *RoutineStoneShellPlanner) step(call, epoch context.Context) (RoutineStoneShellResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineStoneShellResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoutineStoneShellResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineStoneShellResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineStoneShellResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainStoneShell {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineStoneShellResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineStoneShellResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineStoneShellResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineStoneShellResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	identity, _, err := r.reviewer.native.Identity(call)
	if err != nil {
		return RoutineStoneShellResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoutineStoneShellResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineStoneShellResult{}, ErrControl
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineStoneShellResult{}, err
	}
	read, err := observation.ObserveRoutineOwned(call, r.reviewer.native, r.reviewer.clock, expected, r.reviewer.maxAge, claims)
	if err != nil {
		return RoutineStoneShellResult{}, err
	}
	projection := read.Projection
	owned, err := policy.OwnedConstructions(claims, projection.Facts.CurrentConstruction)
	if err != nil {
		return RoutineStoneShellResult{}, err
	}
	targets, err := policy.ReviewStoneShell(owned, projection.Facts.StoneStructures)
	if err != nil {
		return RoutineStoneShellResult{}, err
	}
	walls, known := targets.Value()
	if !known {
		return RoutineStoneShellResult{Reason: BuildingMethodUnknown}, nil
	}
	seen := map[domain.MethodID]bool{}
	for _, method := range goal.Methods {
		seen[method.Method] = true
	}
	var candidates []string
	for _, wall := range walls {
		if seen[stoneShellMethodID(wall)] {
			continue
		}
		candidates = append(candidates, wall)
		if len(candidates) == stoneShellCandidateBound {
			break
		}
	}
	for _, wall := range candidates {
		result, ok, err := r.propose(call, epoch, goal, state, read, wall)
		if err != nil {
			return RoutineStoneShellResult{}, err
		}
		if ok {
			return result, nil
		}
	}
	return RoutineStoneShellResult{Reason: BuildingMethodUnknown}, nil
}

// propose builds and admits one candidate wall's bundle. ok is false only for
// a structural reason to move on to the next candidate (no site, no material,
// unsupported backup geometry); any other outcome, admitted or refused, is
// this tick's final result exactly as wall_upgrade.py's method() returns on
// the first candidate whose bundle it fully builds.
func (r *RoutineStoneShellPlanner) propose(call, epoch context.Context, goal store.GoalState, state ControlState, read observation.RoutineReading, wall string) (RoutineStoneShellResult, bool, error) {
	p := r.reviewer.player
	projection := read.Projection
	sites, _, err := r.native.ReadWallUpgradeSites(call, boundary.Identity(state.Snapshot), wall)
	if err != nil {
		return RoutineStoneShellResult{}, false, err
	}
	current, err := boundary.Context(sites.Context, state.Snapshot)
	if err != nil || current.Native != state.Snapshot.Native || domain.Tick(sites.Context.GetTick()) != projection.Identity.Tick {
		return RoutineStoneShellResult{}, false, ErrControl
	}
	if len(sites.Sites) == 0 {
		return RoutineStoneShellResult{}, false, nil
	}
	site := sites.Sites[0]
	if !site.Eligible() || len(site.ReplacementMaterials) == 0 {
		return RoutineStoneShellResult{}, false, nil
	}
	backupCount := len(site.BackupCells)
	if backupCount != 0 && backupCount != 3 {
		return RoutineStoneShellResult{}, false, nil
	}
	material := site.ReplacementMaterials[0]
	costs := make([]policy.Amount, len(material.Costs))
	for i, c := range material.Costs {
		costs[i] = policy.Amount{Resource: policy.Resource(c.Resource), Count: c.Units}
	}
	// wall_upgrade.py's guard only records the replacement material once no
	// backup wall's own construction already carries it.
	removalMaterial := ""
	if backupCount == 0 {
		removalMaterial = material.Stuff
	}
	key := stoneShellMethodID(wall)
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, key)))
	id := domain.PlanID(fmt.Sprintf("routine-stone-shell-%x", digest[:16]))
	snapshot := state.Snapshot
	snapshot.Plan, snapshot.Revision = id, 1
	var actions []domain.Action
	var previews []policy.Preview
	var stock policy.StockObservation
	first := true
	backupIDs := make([]domain.ActionID, 0, backupCount)
	for i, cell := range site.BackupCells {
		building, err := domain.NewBuilding("Wall", cell, domain.North, material.Stuff)
		if err != nil {
			return RoutineStoneShellResult{}, false, err
		}
		actionID := domain.ActionID(fmt.Sprintf("%s-backup-%d", id, i))
		action, err := domain.NewBuildingAction(actionID, building)
		if err != nil {
			return RoutineStoneShellResult{}, false, err
		}
		preview, ok, err := r.previewWall(call, action, snapshot, projection.Identity.Tick, cell)
		if err != nil {
			return RoutineStoneShellResult{}, false, err
		}
		if !ok {
			return RoutineStoneShellResult{}, false, nil
		}
		actions = append(actions, action)
		previews = append(previews, preview.Preview)
		if err = mergeRoutineStock(&stock, preview.Stock, first); err != nil {
			return RoutineStoneShellResult{}, false, err
		}
		first = false
		backupIDs = append(backupIDs, actionID)
	}
	removal, err := domain.NewWallRemoval(site.TargetID, "", site.X, site.Z, site.NX, site.NZ, site.LeftSupport, site.RightSupport, removalMaterial)
	if err != nil {
		return RoutineStoneShellResult{}, false, err
	}
	demolishID := domain.ActionID(fmt.Sprintf("%s-demolish", id))
	demolish, err := domain.NewWallRemovalAction(demolishID, removal)
	if err != nil {
		return RoutineStoneShellResult{}, false, err
	}
	actions = append(actions, demolish)
	permanentBuilding, err := domain.NewBuilding("Wall", domain.Cell{X: site.X, Z: site.Z}, domain.North, material.Stuff)
	if err != nil {
		return RoutineStoneShellResult{}, false, err
	}
	permanentID := domain.ActionID(fmt.Sprintf("%s-replace", id))
	permanent, err := domain.NewBuildingAction(permanentID, permanentBuilding)
	if err != nil {
		return RoutineStoneShellResult{}, false, err
	}
	permanentPreview, ok, err := r.previewWall(call, permanent, snapshot, projection.Identity.Tick, domain.Cell{X: site.X, Z: site.Z})
	if err != nil {
		return RoutineStoneShellResult{}, false, err
	}
	if !ok {
		return RoutineStoneShellResult{}, false, nil
	}
	actions = append(actions, permanent)
	previews = append(previews, permanentPreview.Preview)
	if err = mergeRoutineStock(&stock, permanentPreview.Stock, first); err != nil {
		return RoutineStoneShellResult{}, false, err
	}
	dependencies := []domain.ActionDependency{{Action: permanentID, Requires: demolishID}}
	for i, backupID := range backupIDs {
		backupRemoval, err := domain.NewWallRemoval("", backupID, site.X, site.Z, site.NX, site.NZ, site.LeftSupport, site.RightSupport, removalMaterial)
		if err != nil {
			return RoutineStoneShellResult{}, false, err
		}
		backupRemoveID := domain.ActionID(fmt.Sprintf("%s-backup-remove-%d", id, i))
		backupRemoveAction, err := domain.NewWallRemovalAction(backupRemoveID, backupRemoval)
		if err != nil {
			return RoutineStoneShellResult{}, false, err
		}
		actions = append(actions, backupRemoveAction)
		dependencies = append(dependencies, domain.ActionDependency{Action: backupRemoveID, Requires: permanentID})
	}
	plan, err := domain.NewPlan(id, 1, actions, dependencies...)
	if err != nil {
		return RoutineStoneShellResult{}, false, err
	}
	// The chosen wall's per-unit cost applies to every Wall this bundle
	// places (each backup and the permanent replacement), so admission's
	// stock check runs against the combined bundle, not a per-action share.
	for i := range previews {
		previews[i].Costs = domain.Known(costs)
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineStoneShellResult{}, false, err
	}
	if p.session.State() != state {
		return RoutineStoneShellResult{}, false, ErrControl
	}
	last, _, err := r.reviewer.native.Identity(call)
	if err != nil {
		return RoutineStoneShellResult{}, false, err
	}
	actual, err := observation.DecodeIdentity(last)
	if err != nil || !routineBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick) {
		return RoutineStoneShellResult{}, false, ErrControl
	}
	now := r.reviewer.clock.Now()
	if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoutineStoneShellResult{}, false, observation.ErrStale
	}
	decision, err := p.journal.AdmitBuildingMethod(call, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: key, Plan: plan, Current: snapshot, Tick: projection.Identity.Tick, Bounds: domain.Known(projection.Bounds), Stock: stock, Rules: r.reviewer.rules, Previews: previews, Purpose: policy.Routine})
	if err != nil {
		return RoutineStoneShellResult{}, false, err
	}
	reason := BuildingMethodRefused
	if decision.Admitted {
		reason = BuildingMethodAdmitted
	}
	return RoutineStoneShellResult{Reason: reason, Plan: id}, true, nil
}

func (r *RoutineStoneShellPlanner) previewWall(ctx context.Context, action domain.Action, snapshot domain.GenerationSnapshot, tick domain.Tick, cell domain.Cell) (bridge.BuildingPreview, bool, error) {
	preview, _, err := r.native.PreviewBuilding(ctx, action, snapshot)
	if err != nil {
		return bridge.BuildingPreview{}, false, err
	}
	v := preview.Preview
	if v.Action != action || !v.Snapshot.Matches(snapshot) || v.Tick != tick || !preview.Stock.Snapshot.Matches(snapshot) || preview.Stock.Tick != tick {
		return bridge.BuildingPreview{}, false, ErrControl
	}
	footprint, fk := v.Footprint.Value()
	made, mk := v.MadeFromStuff.Value()
	legal, lk := v.CanPlace.Value()
	safe, sk := v.SafeToPlace.Value()
	if !fk || !mk || !lk || !sk {
		return bridge.BuildingPreview{}, false, nil
	}
	if made || len(footprint) != 1 || footprint[0] != cell || !legal || !safe {
		return bridge.BuildingPreview{}, false, nil
	}
	return preview, true, nil
}
