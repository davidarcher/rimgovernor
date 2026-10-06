package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

type RoundsSupplySource interface {
	ReadAllowSupplies(context.Context, *c.Identity, domain.Cell) (bridge.SupplyRead, bridge.Result, error)
}
type RoundsSupplyPlanner struct {
	reviewer *Rounder
	native   RoundsSupplySource
}
type RoundsSupplyResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoundsSupplyPlanner(reviewer *Rounder, native RoundsSupplySource) (*RoundsSupplyPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsSupplyPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoundsSupplyPlanner{reviewer, native}, nil
}
func (r *RoundsSupplyPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsSupplyResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsSupplyResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsSupplyResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsSupplyResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsSupplyResult{Verdict: BuildingReasonNoReview}, nil
	}
	var goal store.WorkOwner
	var cohort []policy.StartingSupply
	for _, binding := range review.Standards {
		if binding.Concern != policy.ManageSupplySafety {
			continue
		}
		safety, err := p.journal.LoadStandard(call, binding.Standard)
		if err != nil {
			return RoundsSupplyResult{}, err
		}
		goal = safety
		if safety.OwnerDeficit() {
			cohort = review.EventLoot.Pending
			break
		}
	}
	if goal == nil || !goal.OwnerDeficit() || review.VetoOwner(goal) != "" {
		return RoundsSupplyResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.OwnerMethods() {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsSupplyResult{}, err
		}
		for i, progress := range plan.Progress {
			v := progress.View()
			if v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
				continue
			}
			target, ok := progress.Action().SupplyAllow()
			if !ok {
				continue
			}
			keep := false
			for _, row := range cohort {
				if row.Thing == target.Thing() && row.Definition == target.Definition() && row.Cell == target.Cell() && row.Forbid == target.Forbidden() {
					keep = true
					break
				}
			}
			if !keep {
				cancelled, cancelErr := p.journal.Cancel(call, plan.Spec.ID(), progress.Action().ID())
				if cancelErr != nil {
					return RoundsSupplyResult{}, cancelErr
				}
				plan.Progress[i] = cancelled
			}
		}
		if store.PlanOpen(plan) {
			return RoundsSupplyResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	started := r.reviewer.clock.Now()
	// Each pending stack is read at the cell the review's census last saw
	// it, one read per cell; a stack that moved since is simply absent and
	// waits for the next census to report its new cell.
	pending := map[string]policy.StartingSupply{}
	var cells []domain.Cell
	for _, row := range cohort {
		pending[row.Thing] = row
		cells = append(cells, row.Cell)
	}
	sort.Slice(cells, func(i, j int) bool {
		return cells[i].Z < cells[j].Z || cells[i].Z == cells[j].Z && cells[i].X < cells[j].X
	})
	cells = slices.Compact(cells)
	// Forbid unsafe items before releasing safe items.
	forbidBatch := false
	for _, row := range cohort {
		if row.Forbid {
			forbidBatch = true
		}
	}
	readSupply := r.native.ReadAllowSupplies
	if forbidBatch {
		native, ok := r.native.(interface {
			ReadForbidSupplies(context.Context, *c.Identity, domain.Cell) (bridge.SupplyRead, bridge.Result, error)
		})
		if !ok {
			return RoundsSupplyResult{}, fmt.Errorf("%w: step: !ok", ErrControl)
		}
		readSupply = native.ReadForbidSupplies
	}
	var targets []domain.SupplyAllow
	for _, cell := range cells {
		read, _, err := readSupply(call, boundary.Identity(state.Snapshot), cell)
		if err != nil {
			return RoundsSupplyResult{}, err
		}
		if _, err = boundary.Context(read.Context, state.Snapshot); err != nil || read.Context.GetTick() < int64(review.Tick) {
			return RoundsSupplyResult{}, fmt.Errorf("%w: step: err != nil || read.Context.GetTick() < int64(review.Tick)", ErrControl)
		}
		for _, target := range read.Targets {
			if target.Supply.Cell() != cell {
				return RoundsSupplyResult{}, fmt.Errorf("%w: step: target.Supply.Cell() != cell", ErrControl)
			}
			row, listed := pending[target.Supply.Thing()]
			if listed && row.Forbid == forbidBatch && row.Definition == target.Supply.Definition() && row.Cell == cell {
				targets = append(targets, target.Supply)
			}
		}
		if len(targets) >= 8 {
			break
		}
	}
	if len(targets) == 0 {
		return RoundsSupplyResult{Verdict: waitFor(WaitMethodUsed, "supply_targets")}, nil
	}
	sort.Slice(targets, func(i, j int) bool { return targets[i].Thing() < targets[j].Thing() })
	if len(targets) > 8 {
		targets = targets[:8]
	}
	hash := sha256.New()
	for _, supply := range targets {
		fmt.Fprintf(hash, "%s/%s/%d/%d/%t\n", supply.Thing(), supply.Definition(), supply.Cell().X, supply.Cell().Z, supply.Forbidden())
	}
	method := domain.MethodID(fmt.Sprintf("supply-%x-%d", hash.Sum(nil)[:16], review.Revision))
	id := domain.MintPlanID()
	var actions []domain.Action
	for i, supply := range targets {
		action, err := domain.NewSupplyAllowAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), supply)
		if err != nil {
			return RoundsSupplyResult{}, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoundsSupplyResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsSupplyResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsSupplyResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if err = p.journal.CommitOwnerMethod(call, goal, method, "", plan); err != nil {
		return RoundsSupplyResult{}, err
	}
	return RoundsSupplyResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
