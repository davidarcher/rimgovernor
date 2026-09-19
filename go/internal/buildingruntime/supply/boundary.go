package supply

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type SupplyNative interface {
	ReadAllowSupplies(context.Context, *c.Identity, domain.Cell) (bridge.SupplyRead, bridge.Result, error)
	PreviewSupplyAllow(context.Context, *c.Identity, bridge.SupplyTarget) (*op.PreviewReply, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	LookupSupplyAllow(context.Context, bridge.SupplyAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveSupplyAllow(context.Context, bridge.SupplyAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type SupplyWriter interface {
	AllowSupply(context.Context, *a.WritePrecondition, bridge.SupplyTarget) (*op.ExecuteReply, bridge.Result, error)
}
type SupplyCapabilities struct {
	Native SupplyNative
	Writer SupplyWriter
}
type SupplyBoundary struct {
	*boundary.Boundary
	supply SupplyCapabilities
}

func NewSupplyBoundary(base *boundary.Boundary, supply SupplyCapabilities) *SupplyBoundary {
	return &SupplyBoundary{Boundary: base, supply: supply}
}

func (b *SupplyBoundary) InspectSupply(ctx context.Context, target executor.Target) (executor.SupplyInspection, error) {
	out := executor.SupplyInspection{StartedAt: b.Clock.Now()}
	supply, ok := target.Action.SupplyAllow()
	if !ok {
		return out, executor.ErrEvidence
	}
	read, _, err := b.supply.Native.ReadAllowSupplies(ctx, boundary.Identity(target.Snapshot), supply.Cell())
	if err != nil {
		return out, err
	}
	current, err := boundary.Context(read.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	var selected bridge.SupplyTarget
	for _, item := range read.Targets {
		if item.Supply == supply {
			if selected.Token != "" {
				return out, executor.ErrEvidence
			}
			selected = item
		}
	}
	if selected.Supply != supply || selected.Token == "" {
		// The read is fresh and bound to this colony/load/map: the exact
		// item is simply not at its cell any more.
		return out, executor.ErrSupplyAbsent
	}
	preview, _, err := b.supply.Native.PreviewSupplyAllow(ctx, boundary.Identity(current), selected)
	if err != nil {
		return out, err
	}
	v := preview.GetEvaluated()
	if v == nil || !v.GetAccepted() {
		return out, executor.ErrHeld
	}
	if !boundarySupplyEffect(v.Projected, supply, true) {
		return out, executor.ErrEvidence
	}
	if _, err = boundary.Context(v.Context, current); err != nil {
		return out, err
	}
	emergency, _, err := b.supply.Native.ReadEmergency(ctx, boundary.Identity(current))
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(emergency.Context, current); err != nil {
		return out, err
	}
	// The three reads need only be ordered, not simultaneous: the item's
	// snapshot token already binds the Allow to the stack the read listed,
	// so a tick that advanced between them is a running clock, not stale
	// evidence. Demanding one tick held every Allow until the game paused,
	// and a stack the executor only reached mid-window was never allowed
	// (#120); the building family accepts the same monotonic order.
	if v.Context.GetTick() < read.Context.GetTick() || !domain.Tick(emergency.Context.GetTick()).Covers(domain.Tick(read.Context.GetTick())) {
		return out, executor.ErrHeld
	}
	out.Current, out.Tick, out.Supply, out.SnapshotToken, out.Accepted = current, domain.Tick(v.Context.GetTick()), supply, selected.Token, true
	out.Emergency, err = policy.NewEmergencySnapshot(current, out.Tick, emergency.Facts)
	out.ObservedAt = b.Clock.Now()
	return out, err
}
func (b *SupplyBoundary) supplyAttempt(p executor.Placement) bridge.SupplyAttempt {
	supply, _ := p.Action.SupplyAllow()
	return bridge.SupplyAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: b.Attempt(p), Generation: uint64(p.Snapshot.Native), Supply: supply}
}
func (b *SupplyBoundary) AllowSupply(ctx context.Context, request executor.SupplyDispatch) (executor.Receipt, error) {
	p := request.Attempt
	supply, ok := p.Action.SupplyAllow()
	return b.DispatchWrite(ctx, p,
		func() error {
			if !ok {
				return executor.ErrEvidence
			}
			return nil
		},
		func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			return b.supply.Writer.AllowSupply(ctx, pre, bridge.SupplyTarget{Supply: supply, Token: request.SnapshotToken})
		},
	)
}
func (b *SupplyBoundary) ObserveSupply(ctx context.Context, p executor.Placement, current domain.GenerationSnapshot) (executor.SupplyEvidence, error) {
	out := executor.SupplyEvidence{StartedAt: b.Clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundary.World(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	w := b.supplyAttempt(p)
	lookup, _, err := b.supply.Native.LookupSupplyAllow(ctx, w)
	if err != nil {
		return out, err
	}
	admitted := lookup.GetReceipt()
	if admitted == nil {
		absent, err := boundary.Unadmitted(lookup, p, current)
		if err != nil {
			return out, err
		}
		out.Observation, out.Complete, out.ObservedAt = absent, true, b.Clock.Now()
		return out, nil
	}
	if err = boundary.Admission(admitted, p, b.Session); err != nil {
		return out, err
	}
	reply, _, err := b.supply.Native.ObserveSupplyAllow(ctx, w, admitted)
	if err != nil {
		return out, err
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) {
		return out, executor.ErrEvidence
	}
	out.Observation.Snapshot, err = boundary.Context(v.Context, current)
	if err != nil {
		return out, err
	}
	if v.Context.GetTick() < int64(p.Tick) {
		return out, executor.ErrEvidence
	}
	out.Observation.Tick, out.Observation.Causality = domain.Tick(v.Context.GetTick()), domain.AfterDispatch
	out.Complete, out.Supply = v.GetCompleteInspection(), w.Supply
	switch effect := v.Effect.(type) {
	case *r.Progress_Completed:
		if !v.GetCompleteInspection() || !boundarySupplyEffect(effect.Completed.GetEvidence(), w.Supply, true) {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect, out.Allowed = domain.EffectCompleted, domain.Known(effect.Completed.GetEvidence().GetDesignation().GetPresent())
	case *r.Progress_Unsuccessful:
		if !v.GetCompleteInspection() || effect.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED || !boundarySupplyEffect(effect.Unsuccessful.GetEvidence(), w.Supply, false) {
			return out, executor.ErrEvidence
		}
		out.Allowed = domain.Known(false)
		out.Observation.Effect, out.Observation.UnsuccessfulReason = domain.EffectUnsuccessful, domain.OutcomeNotAchieved
	case *r.Progress_Unknown:
	default:
		return out, executor.ErrEvidence
	}
	out.ObservedAt = b.Clock.Now()
	return out, nil
}

func boundarySupplyEffect(v *r.EffectEvidence, supply domain.SupplyAllow, allowed bool) bool {
	d := v.GetDesignation()
	return d != nil && d.Present != nil && d.GetPresent() == allowed && d.GetThingId() == supply.Thing() && d.GetResourceDef() == supply.Definition() && d.GetDesignationDef() == "Allow" && d.Cell != nil && d.Cell.X != nil && d.Cell.Z != nil && d.Cell.GetX() == supply.Cell().X && d.Cell.GetZ() == supply.Cell().Z
}
