package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// The stockpile helper: the mechanics every stockpile-owning concern shares.
// A concern reads its owned zones (ownedStockpileZones), decides its edits,
// and applies them through stockpileZones: create admits previewed zones,
// editTargetPresent and stockpileEditAction retarget or delete standing ones,
// and openEditPlan gates a concern on its own plan still in flight.

// ownedStockpileZones is the owned-zone view: each journal zone claim of kind
// stockpile that the projection's planning cells still name, with the zone's
// cells (a cell whose storage-empty flag is false holds things) and its
// settings superseded by the latest patch of each.
func ownedStockpileZones(projection *observation.ColonyProjection, owned []store.OwnedZone, patches map[string]store.AppliedStockpile) []policy.StockpileZone {
	type cells struct{ all, stored []domain.Cell }
	byZone := map[string]*cells{}
	for _, cell := range projection.Cells {
		id, known := cell.ZoneID.Value()
		if !known || id == "" {
			continue
		}
		entry := byZone[id]
		if entry == nil {
			entry = &cells{}
			byZone[id] = entry
		}
		entry.all = append(entry.all, cell.Cell)
		if empty, ek := cell.StorageEmpty.Value(); ek && !empty {
			entry.stored = append(entry.stored, cell.Cell)
		}
	}
	var zones []policy.StockpileZone
	for _, z := range owned {
		entry := byZone[z.ID]
		if z.Kind != domain.StockpileZone || entry == nil {
			continue
		}
		zone := policy.StockpileZone{ID: z.ID, Role: z.Role, Cells: entry.all, Stored: entry.stored, Filter: z.Filter, Priority: z.Priority}
		if patch, ok := patches[z.ID]; ok && patch.Kind == domain.StorageZoneTarget {
			zone.Filter, zone.Priority = patch.Filter, patch.Priority
			if patch.Role != "" {
				zone.Role = patch.Role
			}
		}
		zones = append(zones, zone)
	}
	return zones
}

// isStockpileEditPlan reports a zone-edit plan (a create, patch, cell edit or
// delete batch), not a room shell: the plan's own actions decide, so any owner's
// zone method counts, whatever its method id.
func isStockpileEditPlan(plan domain.PlanSpec) bool {
	for _, a := range plan.Actions() {
		switch a.Kind() {
		case domain.ZoneCreateAction, domain.ZoneDeleteAction, domain.ZoneCellEditAction, domain.StockpilePatchAction:
			return true
		}
	}
	return false
}

// openEditPlan reports whether one of the owner's methods holds a zone-edit
// plan still open. A room shell is building work that takes days; zoning is
// instant, so only an open zone-edit plan holds the next edits.
func openEditPlan(ctx context.Context, journal *store.Store, owner store.StandardState) (bool, error) {
	for _, method := range owner.Methods {
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return false, err
		}
		if isStockpileEditPlan(plan.Spec) && store.PlanOpen(plan) {
			return true, nil
		}
	}
	return false, nil
}

// RoundsStockpileSource refreshes one zone's presence and CAS token for
// each edit.
type RoundsStockpileSource interface {
	ReadZoneDeleteTarget(context.Context, *c.Identity, string) (bridge.ZoneDeleteTarget, bridge.Result, error)
}

// shelfTargetSource reads a shelf's storage settings CAS token.
type shelfTargetSource interface {
	ReadStorageBuildingTarget(context.Context, *c.Identity, string) (bridge.StorageBuildingTarget, bridge.Result, error)
}

// stockpileZones applies zone edits for any owning concern.
type stockpileZones struct {
	reviewer *Rounder
	native   RoundsStockpileSource
}

// editTargetPresent reports whether the edit's target (the stockpile zone,
// or the shelf for a shelf patch) is still there; false when the source
// cannot read it.
func (z stockpileZones) editTargetPresent(ctx context.Context, identity *c.Identity, e policy.StockpileEdit) (bool, error) {
	if e.Kind == policy.StockpileShelfPatch {
		source, ok := z.native.(shelfTargetSource)
		if !ok {
			return false, nil
		}
		target, _, err := source.ReadStorageBuildingTarget(ctx, identity, e.Zone)
		return err == nil && target.Present, err
	}
	target, _, err := z.native.ReadZoneDeleteTarget(ctx, identity, e.Zone)
	return err == nil && target.Present && target.Type == "stockpile", err
}

// stockpileEditAction is one edit's action.
func stockpileEditAction(id domain.ActionID, e policy.StockpileEdit) (domain.Action, error) {
	switch e.Kind {
	case policy.StockpileRetarget:
		patch, err := domain.NewStockpilePatch(domain.StorageZoneTarget, e.Zone, e.Filter, e.Priority, e.Role)
		if err != nil {
			return domain.Action{}, err
		}
		return domain.NewStockpilePatchAction(id, patch)
	case policy.StockpileShelfPatch:
		patch, err := domain.NewStockpilePatch(domain.StorageBuildingTarget, e.Zone, e.Filter, e.Priority, e.Role)
		if err != nil {
			return domain.Action{}, err
		}
		return domain.NewStockpilePatchAction(id, patch)
	case policy.StockpileDelete:
		del, err := domain.NewZoneDelete(e.Zone)
		if err != nil {
			return domain.Action{}, err
		}
		return domain.NewZoneDeleteAction(id, del)
	}
	return domain.Action{}, fmt.Errorf("unknown stockpile edit %q", e.Kind)
}

// create admits the missing zones as one method of owner: each zone is
// previewed natively on the review's zone map token, a refused one is dropped,
// and the accepted ones are admitted together with their footprints reserved,
// like every routine zone. Zoning is instant and needs no worker, so a fresh
// colony's zones land in one step.
func (z stockpileZones) create(call, epoch context.Context, state ControlState, owner store.StandardState, projection observation.ColonyProjection, started time.Time, edits []policy.StockpileEdit) (RoundsStockpileResult, error) {
	p := z.reviewer.player
	native, ok := z.native.(interface {
		PreviewZone(context.Context, *c.Identity, domain.ZoneCreate) (*op.ZonePreviewReply, bridge.Result, error)
	})
	if !ok {
		return RoundsStockpileResult{Verdict: fieldUnavailable("zone_preview")}, nil
	}
	tick := projection.Identity.Tick
	roles := make([]string, len(edits))
	for i, e := range edits {
		roles[i] = e.Role
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/create/%s/%d", owner.Standard.ID, owner.Standard.Episode, strings.Join(roles, ","), tick)))
	id := domain.MintPlanID()
	method := domain.MethodID(fmt.Sprintf("stockpile-create-%x", digest[:8]))
	if _, err := p.journal.LoadMethod(call, owner.Standard.ID, owner.Standard.Episode, method); err == nil {
		return RoundsStockpileResult{Verdict: waitFor(WaitMethodUsed, "stockpile_create")}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoundsStockpileResult{}, err
	}
	snapshot := state.Snapshot
	snapshot.Plan = id
	snapshot.Revision = 1
	var actions []domain.Action
	var previews []policy.Preview
	var admitted []policy.StockpileEdit
	for _, e := range edits {
		value, err := domain.NewFilteredStockpileZone(e.Filter, e.Priority, e.Cells)
		if err == nil {
			value, err = value.WithRole(e.Role)
		}
		if err != nil {
			return RoundsStockpileResult{}, err
		}
		reply, _, err := native.PreviewZone(call, boundary.Identity(snapshot), value)
		var refused *bridge.NativeFailure
		if errors.As(err, &refused) {
			telemetry.Decide(call, stockpileEditDecision("refused", refused.Value.GetCode().String(), e.Role, map[string]any{"kind": "create", "detail": refused.Value.GetDetail()}))
			continue
		}
		if err != nil {
			return RoundsStockpileResult{}, err
		}
		v := reply.GetEvaluated()
		if v == nil || !v.GetAccepted() {
			telemetry.Decide(call, stockpileEditDecision("refused", "preview_not_accepted", e.Role, map[string]any{"kind": "create", "detail": v.GetReason()}))
			continue
		}
		if _, err = boundary.Context(v.Context, snapshot); err != nil || domain.Tick(v.Context.GetTick()) < tick {
			return RoundsStockpileResult{}, fmt.Errorf("%w: create: err != nil || domain.Tick(v.Context.GetTick()) < tick", ErrControl)
		}
		action, err := domain.NewZoneCreateAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), value)
		if err != nil {
			return RoundsStockpileResult{}, err
		}
		actions = append(actions, action)
		previews = append(previews, policy.Preview{Action: action, Snapshot: snapshot, Tick: tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(value.Cells()), Costs: domain.Known([]policy.Amount{})})
		admitted = append(admitted, e)
	}
	if len(actions) == 0 {
		return RoundsStockpileResult{Verdict: noSpace("stockpile_zone")}, nil
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoundsStockpileResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsStockpileResult{}, err
	}
	now := z.reviewer.clock.Now()
	if p.session.State() != state || now.Before(started) || now.Sub(started) > z.reviewer.maxAge {
		return RoundsStockpileResult{}, fmt.Errorf("%w: create: p.session.State() != state || now.Before(started) || now.Sub(started) > z.reviewer.maxAge", ErrControl)
	}
	decision, err := admitMethod(call, p.journal, store.BuildingMethodRequest{Owner: owner, Method: method, Plan: plan, Current: snapshot, Tick: tick, Bounds: domain.Known(projection.Bounds), Stock: policy.StockObservation{Snapshot: snapshot, Tick: tick}, Previews: previews, Purpose: policy.Rounds})
	if err != nil {
		return RoundsStockpileResult{}, err
	}
	if !decision.Admitted {
		return RoundsStockpileResult{Verdict: admissionRefused(decision)}, nil
	}
	for _, e := range admitted {
		telemetry.Decide(call, stockpileEditDecision("admitted", "", e.Role, map[string]any{"kind": string(e.Kind), "role": e.Role, "cells": len(e.Cells), "plan": string(id), "detail": e.Explanation}))
	}
	return RoundsStockpileResult{Verdict: BuildingReasonAdmitted, Plan: id, Edits: len(actions)}, nil
}
