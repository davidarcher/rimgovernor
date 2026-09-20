package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

type shrineHeatSource interface {
	RoutineBuildingSource
	ReadBuildingTemperatureTarget(context.Context, *c.Identity, string) (bridge.BuildingTemperatureTarget, bridge.Result, error)
}

func (r *RoutineShrinePlanner) heat(call, epoch context.Context, state ControlState, goal store.GoalState, shrine policy.AncientShrine, caskets []policy.ShrineCasket, squad []policy.ShrineDefenderFacts, projection observation.ColonyProjection, started time.Time, arbiter *stepArbiter) (RoutineShrineResult, error) {
	p := r.reviewer.player
	proposal := policy.SelectShrineHeat(shrine, caskets, squad)
	held := RoutineShrineResult{Reason: BuildingMethodHeld, Shrine: shrine.ID, Hold: proposal.Phase}
	native, ok := r.native.(shrineHeatSource)
	if !ok {
		held.Hold = "heat_unknown"
		return held, nil
	}
	check := func() error {
		if err := p.current(call, epoch); err != nil {
			return err
		}
		elapsed := r.reviewer.clock.Now().Sub(started)
		if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
			return ErrControl
		}
		return nil
	}
	phase := proposal.Phase
	f, _ := shrine.Heat.Value()
	var patch *domain.BuildingTemperature
	// A setpoint is a normal CAS-gated building patch. Never heat with a
	// colonist inside; placement completes before any high setpoint is applied.
	if phase == "heat_warming" || phase == "heat_open" {
		for _, heater := range f.Heaters {
			target, _, err := native.ReadBuildingTemperatureTarget(call, boundary.Identity(state.Snapshot), heater.ID)
			if err != nil {
				return RoutineShrineResult{}, err
			}
			if target.Temperature == policy.ShrineHeatTargetC {
				continue
			}
			if _, err = boundary.Context(target.Context, state.Snapshot); err != nil {
				return RoutineShrineResult{}, err
			}
			value, err := domain.NewBuildingTemperature(heater.ID, policy.ShrineHeatTargetC, target.Token)
			if err != nil {
				return RoutineShrineResult{}, err
			}
			patch = &value
			phase = "heat_target-" + heater.ID
			break
		}
	}
	if phase == "heat_warming" || phase == "heat_colonists_inside" {
		// Budget starts at the first completed setpoint in this goal epoch, so
		// a powerless heater cannot lend an endless series of clock windows.
		var first, built domain.Tick
		for _, method := range goal.Methods {
			if method.Epoch != goal.Goal.Epoch {
				continue
			}
			plan, err := p.journal.LoadPlan(call, method.Plan)
			if err != nil {
				return RoutineShrineResult{}, err
			}
			for _, progress := range plan.Progress {
				if _, ok := progress.Action().Building(); ok && progress.View().Stage == domain.Completed {
					built = max(built, progress.View().Tick)
				}
				setting, ok := progress.Action().BuildingTemperature()
				if !ok || setting.Celsius() != policy.ShrineHeatTargetC {
					continue
				}
				v := progress.View()
				if v.Stage == domain.Completed && v.Tick > 0 && (first == 0 || v.Tick < first) {
					first = v.Tick
				}
			}
		}
		if first == 0 {
			first = built
		}
		if first > 0 && projection.Identity.Tick >= first && projection.Identity.Tick-first < 120000 {
			held.NativeWorkTicks = min(2500, uint32(120000-(projection.Identity.Tick-first)))
		} else {
			held.Hold = "heat_timeout"
		}
		return held, nil
	}
	if phase != "heat_door" && phase != "heat_heater" && phase != "heat_open" && patch == nil {
		return held, nil
	}
	prefix := fmt.Sprintf("%s-%s-%d-%d-", phase, shrine.ID, proposal.Cell.X, proposal.Cell.Z)
	attempt := medicalAttemptCount(goal.Methods, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		held.Hold = "heat_attempts_exhausted"
		return held, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-shrine-%x", digest[:16]))
	snapshot := state.Snapshot
	snapshot.Plan = id
	snapshot.Revision = 1
	var actions []domain.Action
	var deps []domain.ActionDependency
	if phase == "heat_door" || phase == "heat_heater" {
		definition, stuff := "Heater", ""
		if phase == "heat_door" {
			definition, stuff = "Door", "WoodLog"
		}
		if !comfortBuilderAvailable(projection, definition, nil) {
			held.Hold = "heat_builder_unavailable"
			return held, nil
		}
		value, err := domain.NewBuilding(definition, proposal.Cell, domain.North, stuff)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		action, err := domain.NewBuildingAction(domain.ActionID(string(id)+"-build"), value)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		preview, _, err := native.PreviewBuilding(call, action, snapshot)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		if err = check(); err != nil {
			return RoutineShrineResult{}, err
		}
		plan, err := domain.NewPlan(id, 1, []domain.Action{action})
		if err != nil {
			return RoutineShrineResult{}, err
		}
		decision, err := p.journal.AdmitBuildingMethod(call, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: method, Plan: plan, Current: snapshot, Tick: projection.Identity.Tick, Bounds: domain.Known(projection.Bounds), Stock: preview.Stock, Rules: r.reviewer.rules, Previews: []policy.Preview{preview.Preview}, Purpose: policy.Routine})
		if err != nil {
			return RoutineShrineResult{}, err
		}
		if !decision.Admitted {
			held.Hold = "heat_build_refused"
			return held, nil
		}
		return RoutineShrineResult{Reason: BuildingMethodAdmitted, Plan: id, Shrine: shrine.ID}, nil
	}
	if patch != nil {
		action, err := domain.NewBuildingTemperatureAction(domain.ActionID(string(id)+"-target"), *patch)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		actions = append(actions, action)
	} else {
		if !arbiter.tryClaim([]domain.PawnID{proposal.Pawn}) {
			return RoutineShrineResult{Reason: BuildingMethodUsed}, nil
		}
		draftID := domain.ActionID(string(id) + "-draft")
		draft, err := domain.NewOwnedDraft(proposal.Pawn)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		da, err := domain.NewOwnedDraftAction(draftID, draft)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		move, err := domain.NewMovement(proposal.Pawn, proposal.Cell, draftID)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		ma, err := domain.NewMovementAction(domain.ActionID(string(id)+"-move"), move)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		open, err := domain.NewHeatOpenCasket(proposal.Pawn, proposal.Casket.EntityID, proposal.Casket.Cell)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		oa, err := domain.NewOpenCasketAction(domain.ActionID(string(id)+"-open"), open)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		retreat, err := domain.NewMovement(proposal.Pawn, proposal.Retreat, draftID)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		ra, err := domain.NewMovementAction(domain.ActionID(string(id)+"-retreat"), retreat)
		if err != nil {
			return RoutineShrineResult{}, err
		}
		actions = []domain.Action{da, ma, oa, ra}
		deps = []domain.ActionDependency{{Action: ma.ID(), Requires: da.ID()}, {Action: oa.ID(), Requires: da.ID()}, {Action: oa.ID(), Requires: ma.ID()}, {Action: ra.ID(), Requires: oa.ID()}, {Action: ra.ID(), Requires: da.ID()}}
	}
	plan, err := domain.NewPlan(id, 1, actions, deps...)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	if err = check(); err != nil {
		return RoutineShrineResult{}, err
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineShrineResult{}, err
	}
	return RoutineShrineResult{Reason: BuildingMethodAdmitted, Plan: id, Shrine: shrine.ID}, nil
}
