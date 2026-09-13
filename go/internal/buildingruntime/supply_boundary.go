package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
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
type supplyBoundary struct {
	*Boundary
	supply SupplyCapabilities
}

func (b *supplyBoundary) InspectSupply(ctx context.Context, target executor.Target) (executor.SupplyInspection, error) {
	out := executor.SupplyInspection{StartedAt: b.clock.Now()}
	supply, ok := target.Action.SupplyAllow()
	if !ok {
		return out, executor.ErrEvidence
	}
	read, _, err := b.supply.Native.ReadAllowSupplies(ctx, boundaryIdentity(target.Snapshot), supply.Cell())
	if err != nil {
		return out, err
	}
	current, err := boundaryContext(read.Context, target.Snapshot)
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
		return out, executor.ErrHeld
	}
	preview, _, err := b.supply.Native.PreviewSupplyAllow(ctx, boundaryIdentity(current), selected)
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
	if _, err = boundaryContext(v.Context, current); err != nil {
		return out, err
	}
	emergency, _, err := b.supply.Native.ReadEmergency(ctx, boundaryIdentity(current))
	if err != nil {
		return out, err
	}
	if _, err = boundaryContext(emergency.Context, current); err != nil {
		return out, err
	}
	if read.Context.GetTick() != v.Context.GetTick() || v.Context.GetTick() != emergency.Context.GetTick() {
		return out, executor.ErrHeld
	}
	out.Current, out.Tick, out.Supply, out.SnapshotToken, out.Accepted = current, domain.Tick(v.Context.GetTick()), supply, selected.Token, true
	out.Emergency, err = policy.NewEmergencySnapshot(current, out.Tick, emergency.Facts)
	out.ObservedAt = b.clock.Now()
	return out, err
}
func (b *supplyBoundary) supplyAttempt(p executor.Placement) bridge.SupplyAttempt {
	supply, _ := p.Action.SupplyAllow()
	return bridge.SupplyAttempt{Identity: boundaryIdentity(p.Snapshot), Attempt: b.attempt(p), Owner: &a.Owner{ControllerSessionId: proto.String(b.session), PlayerDirection: proto.Uint64(uint64(p.Snapshot.Direction))}, Generation: uint64(p.Snapshot.Native), Supply: supply}
}
func (b *supplyBoundary) AllowSupply(ctx context.Context, request executor.SupplyDispatch) (executor.Receipt, error) {
	p := request.Attempt
	supply, ok := p.Action.SupplyAllow()
	return b.dispatchWrite(ctx, p,
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
func (b *supplyBoundary) ObserveSupply(ctx context.Context, p executor.Placement, current domain.GenerationSnapshot) (executor.SupplyEvidence, error) {
	out := executor.SupplyEvidence{StartedAt: b.clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundaryWorld(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	w := b.supplyAttempt(p)
	lookup, _, err := b.supply.Native.LookupSupplyAllow(ctx, w)
	if err != nil {
		return out, err
	}
	admitted := lookup.GetReceipt()
	if admitted == nil {
		return out, executor.ErrHeld
	}
	if err = boundaryAdmission(admitted, p, b.session); err != nil {
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
	out.Observation.Snapshot, err = boundaryContext(v.Context, current)
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
	out.ObservedAt = b.clock.Now()
	return out, nil
}

func boundarySupplyEffect(v *r.EffectEvidence, supply domain.SupplyAllow, allowed bool) bool {
	d := v.GetDesignation()
	return d != nil && d.Present != nil && d.GetPresent() == allowed && d.GetThingId() == supply.Thing() && d.GetResourceDef() == supply.Definition() && d.GetDesignationDef() == "Allow" && d.Cell != nil && d.Cell.X != nil && d.Cell.Z != nil && d.Cell.GetX() == supply.Cell().X && d.Cell.GetZ() == supply.Cell().Z
}
