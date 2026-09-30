// Package haul sends the haul action family through Actions/Apply (#856).
package haul

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// HaulNative narrows *bridge.Client to the one read a haul dispatch makes:
// the hauler's row, for the tick the dispatch is journaled at.
type HaulNative interface {
	ReadPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
}
type HaulCapabilities struct {
	Native HaulNative
	Writer boundary.ActionsWriter
}

// HaulBoundary sends a haul as an idempotent intent naming the pawn and the
// item. Native checks both against live state when it applies and refuses
// with a reason; an applied haul is ordered, and where the item went is read
// by the next planner review.
type HaulBoundary struct {
	native HaulNative
	writer boundary.ActionsWriter
	leases boundary.LeaseSource
	clock  executor.Clock
}

func NewHaulBoundary(native HaulNative, writer boundary.ActionsWriter, leases boundary.LeaseSource, clock executor.Clock) (*HaulBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil {
		return nil, errors.New("invalid haul boundary dependencies")
	}
	return &HaulBoundary{native, writer, leases, clock}, nil
}

// InspectHaul anchors the dispatch to a fresh native read. It does not
// preview: native judges the intent when it applies.
func (b *HaulBoundary) InspectHaul(ctx context.Context, target executor.Target) (executor.HaulInspection, error) {
	out := executor.HaulInspection{StartedAt: b.clock.Now()}
	if _, err := bridge.IntentAction("inspect", target.Action); err != nil {
		return out, errors.Join(executor.ErrEvidence, err)
	}
	haul, _ := target.Action.Haul()
	reply, _, err := b.native.ReadPawns(bridge.WithAnyFrame(ctx), boundary.Identity(target.Snapshot), []string{string(haul.Pawn())})
	if err != nil {
		return out, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return out, executor.ErrHeld
	}
	current, err := boundary.Context(observed.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	out.Snapshot, out.Tick, out.ObservedAt = current, domain.Tick(observed.Context.GetTick()), b.clock.Now()
	return out, ctx.Err()
}

// WriteHaul sends the intent through Actions/Apply.
func (b *HaulBoundary) WriteHaul(ctx context.Context, p executor.Placement) (executor.Receipt, error) {
	return boundary.DispatchIntent(ctx, b.leases, p, b.writer)
}

var _ executor.HaulBoundary = (*HaulBoundary)(nil)
