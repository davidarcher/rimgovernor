package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

func (r *RoutineFoodStorageUpkeepPlanner) admitCorpseLarder(ctx, epoch context.Context, arbiter *stepArbiter, goal store.GoalState, observed *o.ColonyFactsSnapshot, choice policy.CorpseLarderMethod) (RoutineFoodStorageUpkeepResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	started := r.reviewer.clock.Now()
	method := domain.MethodID(fmt.Sprintf("corpse-larder-%s-%s-%d", choice.Kind, choice.Stock.ID, goal.Revision))
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-corpse-%x", digest[:16]))
	actionID := domain.ActionID(fmt.Sprintf("%s-0", id))
	var action domain.Action
	var err error
	switch choice.Kind {
	case "allow", "forbid":
		var supply domain.SupplyAllow
		if choice.Kind == "allow" {
			supply, err = domain.NewSupplyAllow(choice.Stock.ID, string(choice.Stock.DefName), choice.Handling.Cell)
		} else {
			supply, err = domain.NewSupplyForbid(choice.Stock.ID, string(choice.Stock.DefName), choice.Handling.Cell)
		}
		if err == nil {
			action, err = domain.NewSupplyAllowAction(actionID, supply)
		}
	case "haul":
		preferences, loadErr := p.journal.LoadWorkPreferences(ctx, state.Snapshot.Plan)
		if loadErr != nil && !errors.Is(loadErr, store.ErrNotFound) {
			return RoutineFoodStorageUpkeepResult{}, loadErr
		}
		if loadErr == nil {
			for _, row := range preferences.Overrides {
				if string(row.Pawn) == string(choice.Handling.Hauler) && row.Work == "Hauling" && row.Priority == 0 {
					return RoutineFoodStorageUpkeepResult{Reason: BuildingMethodUsed}, nil
				}
			}
		}
		if !arbiter.tryClaim([]domain.PawnID{choice.Handling.Hauler}, "haul-item:"+choice.Stock.ID) {
			return RoutineFoodStorageUpkeepResult{Reason: BuildingMethodUsed}, nil
		}
		var haul domain.Haul
		haul, err = domain.NewHaul(choice.Handling.Hauler, choice.Stock.ID, string(choice.Stock.DefName), choice.Handling.Cell)
		if err == nil {
			action, err = domain.NewHaulAction(actionID, haul)
		}
	case "zone":
		return r.admitCorpseZone(ctx, epoch, goal, observed, choice.Cell, method, id, actionID)
	default:
		return RoutineFoodStorageUpkeepResult{}, ErrControl
	}
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	if err = p.current(ctx, epoch); err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineFoodStorageUpkeepResult{}, ErrControl
	}
	_, err = p.journal.CommitGoalMethod(ctx, goal.Goal.ID, goal.Revision, method, plan)
	return RoutineFoodStorageUpkeepResult{Reason: BuildingMethodAdmitted, Plan: id}, err
}

func (r *RoutineFoodStorageUpkeepPlanner) admitCorpseZone(ctx, epoch context.Context, goal store.GoalState, observed *o.ColonyFactsSnapshot, cell domain.Cell, method domain.MethodID, id domain.PlanID, actionID domain.ActionID) (RoutineFoodStorageUpkeepResult, error) {
	native, ok := r.native.(interface {
		PreviewZone(context.Context, *c.Identity, bridge.ZoneTarget) (*op.PreviewReply, bridge.Result, error)
	})
	if !ok {
		return RoutineFoodStorageUpkeepResult{Reason: BuildingMethodUnknown}, nil
	}
	p := r.reviewer.player
	state := p.session.State()
	started := r.reviewer.clock.Now()
	zone, err := domain.NewStockpileZone(domain.CorpseLarderPreset, domain.ImportantPriority, []domain.Cell{cell})
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	fresh, _, err := r.native.ReadColonyFacts(ctx, boundary.Identity(state.Snapshot), true, nil)
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	v := fresh.GetObserved()
	if v == nil || v.GetPlanning().GetObserved().GetZoneMapSnapshot() == nil {
		return RoutineFoodStorageUpkeepResult{Reason: BuildingMethodUnknown}, nil
	}
	if err = bridge.ValidateColonyFacts(v, boundary.Identity(state.Snapshot)); err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	// The new planning read can observe a thaw or a newly occupied site.
	currentChoice, choiceErr := policy.SelectCorpseLarder(foodStorageObservationFacts(v))
	if choiceErr != nil {
		return RoutineFoodStorageUpkeepResult{}, choiceErr
	}
	if currentChoice.Kind != "zone" || currentChoice.Cell != cell {
		return RoutineFoodStorageUpkeepResult{Reason: BuildingMethodUsed}, nil
	}
	if _, err = boundary.Context(v.Context, state.Snapshot); err != nil || v.Context.GetTick() < observed.Context.GetTick() {
		return RoutineFoodStorageUpkeepResult{}, ErrControl
	}
	reply, _, err := native.PreviewZone(ctx, boundary.Identity(state.Snapshot), bridge.ZoneTarget{Zone: zone, Token: v.GetPlanning().GetObserved().GetZoneMapSnapshot().GetToken()})
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	evaluated := reply.GetEvaluated()
	if evaluated == nil || !evaluated.GetAccepted() {
		return RoutineFoodStorageUpkeepResult{Reason: BuildingMethodRefused}, nil
	}
	if _, err = boundary.Context(evaluated.Context, state.Snapshot); err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	action, err := domain.NewZoneCreateAction(actionID, zone)
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	snapshot := state.Snapshot
	snapshot.Plan = id
	snapshot.Revision = 1
	tick := domain.Tick(v.Context.GetTick())
	preview := policy.Preview{Action: action, Snapshot: snapshot, Tick: tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(zone.Cells()), Costs: domain.Known([]policy.Amount{})}
	if err = p.current(ctx, epoch); err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineFoodStorageUpkeepResult{}, ErrControl
	}
	decision, err := p.journal.AdmitBuildingMethod(ctx, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: method, Plan: plan, Current: snapshot, Tick: tick, Bounds: domain.Known(policy.Bounds{Width: int32(v.MapSize.GetWidth()), Height: int32(v.MapSize.GetHeight())}), Stock: policy.StockObservation{Snapshot: snapshot, Tick: tick}, Rules: r.reviewer.rules, Previews: []policy.Preview{preview}, Purpose: policy.Routine})
	reason := BuildingMethodRefused
	if decision.Admitted {
		reason = BuildingMethodAdmitted
	}
	return RoutineFoodStorageUpkeepResult{Reason: reason, Plan: id}, err
}
