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

// stoneShellCandidateBound: at most this
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
	Verdict
	Plan domain.PlanID
}

func NewRoutineStoneShellPlanner(reviewer *RoutineReviewer, native RoutineStoneShellSource) (*RoutineStoneShellPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineStoneShellPlanner: reviewer == nil || native == nil", ErrControl)
	}
	if reviewer.native == nil {
		return nil, fmt.Errorf("%w: NewRoutineStoneShellPlanner: reviewer.native == nil", ErrControl)
	}
	return &RoutineStoneShellPlanner{reviewer, native}, nil
}

func stoneShellMethodID(wall string) domain.MethodID {
	sum := sha256.Sum256([]byte(wall))
	return domain.MethodID(fmt.Sprintf("wall-%x", sum[:16]))
}

func (r *RoutineStoneShellPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineStoneShellResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineStoneShellResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoutineStoneShellResult{}, fmt.Errorf("%w: step: !state.ObservationKnown", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineStoneShellResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineStoneShellResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainStoneShell)
	if err != nil {
		return RoutineStoneShellResult{}, err
	}
	if !workable {
		return RoutineStoneShellResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineStoneShellResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineStoneShellResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil {
		return RoutineStoneShellResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineStoneShellResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineStoneShellResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims)
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
		field := "stone_structures"
		if _, owned := owned.Value(); !owned {
			field = "current_construction"
		}
		return RoutineStoneShellResult{Verdict: fieldUnavailable(field)}, nil
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
	unstocked := false
	for _, wall := range candidates {
		result, ok, err := r.propose(call, epoch, goal, state, read, wall)
		if err != nil {
			return RoutineStoneShellResult{}, err
		}
		if ok {
			return result, nil
		}
		unstocked = unstocked || result.Verdict == stoneShellUnstocked
	}
	// A site with no replacement material while Stonecutting is unfinished
	// waits on that research: nothing cuts the blocks a stone wall needs.
	if unstocked {
		if gate := policy.ResearchGate([]string{policy.StoneShellResearch}, projection.Facts.Research); gate != "" {
			return RoutineStoneShellResult{Verdict: researchWait(gate)}, nil
		}
	}
	return RoutineStoneShellResult{Verdict: noSpace("stone_wall_upgrade_site")}, nil
}

// propose builds and admits one candidate wall's bundle. ok is false only for
// a structural reason to move on to the next candidate (no site, no material,
// unsupported backup geometry); any other outcome, admitted or refused, is
// this tick's final result: the first candidate whose bundle fully builds
// wins.
// stoneShellFunded reports whether budget covers walls walls of material.
func stoneShellFunded(budget map[policy.Resource]int64, material bridge.WallMaterial, walls int64) bool {
	for _, c := range material.Costs {
		if budget[policy.Resource(c.Resource)] < c.Units*walls {
			return false
		}
	}
	return true
}

func (r *RoutineStoneShellPlanner) propose(call, epoch context.Context, goal store.GoalState, state ControlState, read observation.RoutineReading, wall string) (RoutineStoneShellResult, bool, error) {
	p := r.reviewer.player
	projection := read.Projection
	sites, _, err := r.native.ReadWallUpgradeSites(call, boundary.Identity(state.Snapshot), wall)
	if err != nil {
		return RoutineStoneShellResult{}, false, err
	}
	current, err := boundary.Context(sites.Context, state.Snapshot)
	if err != nil || current.Native != state.Snapshot.Native || domain.Tick(sites.Context.GetTick()) < projection.Identity.Tick {
		return RoutineStoneShellResult{}, false, fmt.Errorf("%w: propose: err != nil || current.Native != state.Snapshot.Native || domain.Tick(sites.Context.GetTick()) < projection", ErrControl)
	}
	if len(sites.Sites) == 0 {
		return RoutineStoneShellResult{}, false, nil
	}
	site := sites.Sites[0]
	if !site.Eligible() {
		return RoutineStoneShellResult{}, false, nil
	}
	if len(site.ReplacementMaterials) == 0 {
		return RoutineStoneShellResult{Verdict: stoneShellUnstocked}, false, nil
	}
	backupCount := len(site.BackupCells)
	if backupCount != 0 && backupCount != 3 {
		return RoutineStoneShellResult{}, false, nil
	}
	// The backups and the permanent wall are funded from free stock, after
	// construction and live bill jobs (#1354): the first stone the budget
	// covers. Without a stock census the first stone is proposed.
	material, funded := site.ReplacementMaterials[0], true
	if budget, known := policy.MaterialBudget(projection.Facts.Resources, projection.Facts.ConstructionDeficit, projection.Facts.BillReservations, "").Value(); known {
		funded = false
		for _, option := range site.ReplacementMaterials {
			if stoneShellFunded(budget, option, int64(backupCount+1)) {
				material, funded = option, true
				break
			}
		}
	}
	if !funded {
		return RoutineStoneShellResult{Verdict: stoneShellUnstocked}, false, nil
	}
	costs := make([]policy.Amount, len(material.Costs))
	for i, c := range material.Costs {
		costs[i] = policy.Amount{Resource: policy.Resource(c.Resource), Count: c.Units}
	}
	key := stoneShellMethodID(wall)
	id := domain.MintPlanID()
	snapshot := state.Snapshot
	snapshot.Plan, snapshot.Revision = id, 1
	var actions []domain.Action
	var previews []policy.Preview
	stock := policy.StockObservation{Snapshot: snapshot, Tick: projection.Identity.Tick}
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
	removal, err := domain.NewWallRemoval(site.TargetID, "", domain.Cell{X: site.X, Z: site.Z})
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
	// Native lists a straight site as demolition-ready only once every
	// backup stands; in between it is no site at all.
	for _, backupID := range backupIDs {
		dependencies = append(dependencies, domain.ActionDependency{Action: demolishID, Requires: backupID})
	}
	for i, backupID := range backupIDs {
		backupRemoval, err := domain.NewWallRemoval("", backupID, site.BackupCells[i])
		if err != nil {
			return RoutineStoneShellResult{}, false, err
		}
		backupRemoveID := domain.ActionID(fmt.Sprintf("%s-backup-remove-%d", id, i))
		backupRemoveAction, err := domain.NewWallRemovalAction(backupRemoveID, backupRemoval)
		if err != nil {
			return RoutineStoneShellResult{}, false, err
		}
		actions = append(actions, backupRemoveAction)
		dependencies = append(dependencies, domain.ActionDependency{Action: backupRemoveID, Requires: permanentID}, domain.ActionDependency{Action: backupRemoveID, Requires: backupID})
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
		return RoutineStoneShellResult{}, false, fmt.Errorf("%w: propose: p.session.State() != state", ErrControl)
	}
	actual, err := routineScope(call, r.reviewer.native)
	if err != nil || !routineBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick) {
		return RoutineStoneShellResult{}, false, fmt.Errorf("%w: propose: err != nil || !routineBuildingBoundary(actual, state.Snapshot, projection.Identity.Tick)", ErrControl)
	}
	now := r.reviewer.clock.Now()
	if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoutineStoneShellResult{}, false, observation.ErrStale
	}
	decision, err := admitMethod(call, p.journal, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: key, Plan: plan, Current: snapshot, Tick: projection.Identity.Tick, Bounds: domain.Known(projection.Bounds), Stock: stock, Previews: previews, Purpose: policy.Routine})
	if err != nil {
		return RoutineStoneShellResult{}, false, err
	}
	reason := BuildingReasonRefused
	if decision.Admitted {
		reason = BuildingReasonAdmitted
	}
	return RoutineStoneShellResult{Verdict: reason, Plan: id}, true, nil
}

func (r *RoutineStoneShellPlanner) previewWall(ctx context.Context, action domain.Action, snapshot domain.GenerationSnapshot, tick domain.Tick, cell domain.Cell) (bridge.BuildingPreview, bool, error) {
	preview, _, err := r.native.PreviewBuilding(ctx, action, snapshot)
	if err != nil {
		return bridge.BuildingPreview{}, false, err
	}
	v := preview.Preview
	footprint, fk := v.Footprint.Value()
	made, mk := v.MadeFromStuff.Value()
	legal, lk := v.CanPlace.Value()
	safe, sk := v.SafeToPlace.Value()
	if !fk || !mk || !lk || !sk {
		return bridge.BuildingPreview{}, false, nil
	}
	// A Wall is a stuffed building: the preview of one that is not made
	// from stuff is a contract mismatch, not a placeable site (#293).
	if !made || len(footprint) != 1 || footprint[0] != cell || !legal || !safe {
		return bridge.BuildingPreview{}, false, nil
	}
	return preview, true, nil
}
