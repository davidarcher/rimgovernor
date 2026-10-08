package buildingruntime

import (
	"context"
	"encoding/json"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type commsTradeRequestRecord struct {
	Request domain.CommsTradeRequest `json:"comms_trade_request"`
}

func requestRecord(record string) (commsTradeRequestRecord, error) {
	var out commsTradeRequestRecord
	if err := json.Unmarshal([]byte(record), &out); err != nil {
		return out, err
	}
	if err := out.Request.Validate(); err != nil {
		return out, err
	}
	return out, nil
}
func requestRecordPending(record commsTradeRequestRecord, read executor.CommsTradeInspection) bool {
	if !read.RequestKnown {
		return false
	}
	if read.MatchingWork || read.MatchingArrival {
		return true
	}
	if read.LastRequestTick == record.Request.ExpectedLastRequestTick || read.MatchingSeller {
		return false
	}
	expiry := int64(360000)
	if record.Request.Kind == domain.TradeRequestOrbital {
		expiry = 5001
	}
	return int64(read.Tick) <= read.LastRequestTick+expiry
}

// Requests are methods of the supply concern that selected the attempt. A
// native arrival later appears in the ordinary trader census and is browsed
// by the existing shared trade phases; request intent never supplies stock.
func (r *RoundsTradePlanner) request(call, epoch context.Context, state ControlState, review store.Rounds) (RoundsTradeResult, bool, error) {
	owners := []struct {
		name    string
		concern policy.ConcernID
	}{{"food", policy.EnsureFoodSupply}, {"resources", policy.MaintainResource}}
	// Recover outstanding attempts from the owning saved Concern's linked
	// shared method, before considering proposals from either supply owner.
	// This also fences a restart before native's queued job becomes visible.
	var settled []struct{ owner, id string }
	for _, owner := range owners {
		goal, _, err := r.reviewer.player.journal.Workable(call, review, owner.concern)
		if err != nil {
			return RoundsTradeResult{}, true, err
		}
		for _, method := range goal.History {
			plan, err := r.reviewer.player.journal.LoadPlan(call, method.Plan)
			if err != nil {
				return RoundsTradeResult{}, true, err
			}
			for _, progress := range plan.Progress {
				request, ok := progress.Action().CommsTradeRequest()
				if !ok {
					continue
				}
				if store.PlanOpen(plan) {
					return RoundsTradeResult{}, false, nil
				}
				kind := "TRADE_REQUEST_KIND_CARAVAN"
				if request.Kind == domain.TradeRequestOrbital {
					kind = "TRADE_REQUEST_KIND_ORBITAL"
				}
				settled = append(settled, struct{ owner, id string }{owner.name, request.Faction + "/" + kind + "/" + request.TraderKind})
			}
		}
		if goal.Standard.Record != "" {
			record, err := requestRecord(goal.Standard.Record)
			if err != nil {
				return RoundsTradeResult{}, true, err
			}
			source, ok := r.native.(CommsTradeRequestNative)
			if !ok {
				return RoundsTradeResult{}, true, ErrControl
			}
			action, err := domain.NewCommsTradeRequestAction("request-recovery", record.Request)
			if err != nil {
				return RoundsTradeResult{}, true, err
			}
			reader := tradeBoundary{Boundary: &boundary.Boundary{Clock: r.reviewer.clock}, trade: TradeCapabilities{Requests: source}}
			read, err := reader.InspectCommsTradeRequest(call, executor.Target{Action: action, Snapshot: state.Snapshot})
			if err != nil {
				return RoundsTradeResult{}, true, err
			}
			if requestRecordPending(record, read) {
				return RoundsTradeResult{}, false, nil
			}
			if _, err = r.reviewer.player.journal.RecordStandard(call, goal.Standard.ID, goal.Revision, ""); err != nil {
				return RoundsTradeResult{}, true, err
			}
		}
	}
	for _, request := range settled {
		r.reviewer.ReleaseTradeAcquisition(request.owner, request.id)
	}
	for _, owner := range owners {
		proposal := r.reviewer.TradeAcquisitionPlan(owner.name).Proposal
		if proposal == nil || proposal.Option.Kind == policy.TradeAcquireSettlement {
			continue
		}
		goal, workable, err := r.reviewer.player.journal.Workable(call, review, owner.concern)
		if err != nil {
			return RoundsTradeResult{}, true, err
		}
		if !workable {
			continue
		}
		open := false
		for _, method := range goal.Methods {
			plan, err := r.reviewer.player.journal.LoadPlan(call, method.Plan)
			if err != nil {
				return RoundsTradeResult{}, true, err
			}
			if store.PlanOpen(plan) {
				open = true
			}
		}
		if open {
			continue
		}
		option := proposal.Option
		if !r.reviewer.ClaimTradeAcquisition(owner.name, option.ID) {
			continue
		}
		release := func() { r.reviewer.ReleaseTradeAcquisition(owner.name, option.ID) }
		kind := domain.TradeRequestCaravan
		if option.Kind == policy.TradeAcquireOrbital {
			kind = domain.TradeRequestOrbital
		}
		request := domain.CommsTradeRequest{Kind: kind, Faction: option.Faction, TraderKind: option.TraderKind, Console: option.Console, Negotiator: option.Negotiator, ExpectedLastRequestTick: option.LastRequestTick}
		id := domain.MintPlanID()
		action, err := domain.NewCommsTradeRequestAction(domain.ActionID(string(id)+"-request"), request)
		if err != nil {
			release()
			return RoundsTradeResult{}, true, err
		}
		plan, err := domain.NewPlan(id, 1, []domain.Action{action})
		if err != nil {
			release()
			return RoundsTradeResult{}, true, err
		}
		if err = r.reviewer.player.current(call, epoch); err != nil {
			release()
			return RoundsTradeResult{}, true, err
		}
		if r.reviewer.player.session.State() != state {
			release()
			return RoundsTradeResult{}, true, ErrControl
		}
		method := domain.MethodID(fmt.Sprintf("trade-request-%d", goal.Admitted))
		record, err := json.Marshal(commsTradeRequestRecord{Request: request})
		if err != nil {
			release()
			return RoundsTradeResult{}, true, err
		}
		if _, err = r.reviewer.player.journal.CommitMethodRecord(call, goal.Standard.ID, goal.Revision, method, "", plan, string(record)); err != nil {
			release()
			return RoundsTradeResult{}, true, err
		}
		// Keep the derived admission claim until this shared method reconciles.
		// The saved method and native facts own recovery after restart.
		return RoundsTradeResult{Verdict: BuildingReasonAdmitted, Plan: id, NativeWorkTicks: tradeWalkTicks}, true, nil
	}
	return RoundsTradeResult{}, false, nil
}
