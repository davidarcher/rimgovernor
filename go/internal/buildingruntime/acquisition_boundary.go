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

type AcquisitionNative interface {
	ReadAcquisition(context.Context, *c.Identity, domain.Cell) (bridge.AcquisitionRead, bridge.Result, error)
	PreviewAcquisition(context.Context, *c.Identity, bridge.AcquisitionTarget) (*op.PreviewReply, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	LookupAcquisition(context.Context, bridge.AcquisitionAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveAcquisition(context.Context, bridge.AcquisitionAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type AcquisitionWriter interface {
	Acquire(context.Context, *a.WritePrecondition, bridge.AcquisitionTarget) (*op.ExecuteReply, bridge.Result, error)
}
type AcquisitionCapabilities struct {
	Native AcquisitionNative
	Writer AcquisitionWriter
}
type acquisitionBoundary struct {
	*Boundary
	acquisition AcquisitionCapabilities
}

func (b *acquisitionBoundary) InspectAcquisition(ctx context.Context, target executor.Target) (executor.AcquisitionInspection, error) {
	out := executor.AcquisitionInspection{StartedAt: b.clock.Now()}
	acquisition, ok := target.Action.Acquisition()
	if !ok {
		return out, executor.ErrEvidence
	}
	read, _, err := b.acquisition.Native.ReadAcquisition(ctx, boundaryIdentity(target.Snapshot), acquisition.Cell())
	if err != nil {
		return out, err
	}
	current, err := boundaryContext(read.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	var selected bridge.AcquisitionTarget
	for _, item := range read.Targets {
		if item.Acquisition == acquisition {
			if selected.Token != "" {
				return out, executor.ErrEvidence
			}
			selected = item
		}
	}
	if selected.Acquisition != acquisition || selected.Token == "" {
		return out, executor.ErrHeld
	}
	preview, _, err := b.acquisition.Native.PreviewAcquisition(ctx, boundaryIdentity(current), selected)
	if err != nil {
		return out, err
	}
	v := preview.GetEvaluated()
	if v == nil || !v.GetAccepted() {
		return out, executor.ErrHeld
	}
	if v.Projected != nil {
		return out, executor.ErrEvidence
	}
	if _, err = boundaryContext(v.Context, current); err != nil {
		return out, err
	}
	emergency, _, err := b.acquisition.Native.ReadEmergency(ctx, boundaryIdentity(current))
	if err != nil {
		return out, err
	}
	if _, err = boundaryContext(emergency.Context, current); err != nil {
		return out, err
	}
	if read.Context.GetTick() != v.Context.GetTick() || v.Context.GetTick() != emergency.Context.GetTick() {
		return out, executor.ErrHeld
	}
	out.Current, out.Tick, out.Acquisition, out.SnapshotToken, out.Accepted = current, domain.Tick(v.Context.GetTick()), acquisition, selected.Token, true
	out.Emergency, err = policy.NewEmergencySnapshot(current, out.Tick, emergency.Facts)
	out.ObservedAt = b.clock.Now()
	return out, err
}
func (b *acquisitionBoundary) acquisitionAttempt(p executor.Placement) bridge.AcquisitionAttempt {
	acquisition, _ := p.Action.Acquisition()
	return bridge.AcquisitionAttempt{Identity: boundaryIdentity(p.Snapshot), Attempt: b.attempt(p), Owner: &a.Owner{ControllerSessionId: proto.String(b.session), PlayerDirection: proto.Uint64(uint64(p.Snapshot.Direction))}, Generation: uint64(p.Snapshot.Native), Acquisition: acquisition}
}
func (b *acquisitionBoundary) Acquire(ctx context.Context, request executor.AcquisitionDispatch) (executor.Receipt, error) {
	p := request.Attempt
	out := executor.Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptUnknown}
	acquisition, ok := p.Action.Acquisition()
	if !ok {
		return out, executor.ErrEvidence
	}
	lease, err := b.leases.Lease(p.Snapshot)
	if err != nil {
		return out, err
	}
	reply, _, err := b.acquisition.Writer.Acquire(ctx, &a.WritePrecondition{Identity: boundaryIdentity(p.Snapshot), Attempt: b.attempt(p), LeaseId: proto.String(lease), ExpectedGeneration: proto.Uint64(uint64(p.Snapshot.Native))}, bridge.AcquisitionTarget{Acquisition: acquisition, Token: request.SnapshotToken})
	if err != nil {
		return out, err
	} // Refusals remain uncertain until correlated inspection.
	if err = boundaryAdmission(reply.GetReceipt(), p, b.session); err != nil {
		return out, err
	}
	if reply.GetReceipt().GetApplied() != nil {
		out.Kind = domain.ReceiptAccepted
	}
	return out, nil
}
func (b *acquisitionBoundary) ObserveAcquisition(ctx context.Context, p executor.Placement, current domain.GenerationSnapshot) (executor.AcquisitionEvidence, error) {
	out := executor.AcquisitionEvidence{StartedAt: b.clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundaryWorld(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	w := b.acquisitionAttempt(p)
	lookup, _, err := b.acquisition.Native.LookupAcquisition(ctx, w)
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
	reply, _, err := b.acquisition.Native.ObserveAcquisition(ctx, w, admitted)
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
	out.Complete, out.Acquisition = v.GetCompleteInspection(), w.Acquisition
	var evidence *r.EffectEvidence
	switch effect := v.Effect.(type) {
	case *r.Progress_Completed:
		evidence = effect.Completed.GetEvidence()
		out.Observation.Effect = domain.EffectCompleted
	case *r.Progress_Unsuccessful:
		if effect.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return out, executor.ErrEvidence
		}
		evidence = effect.Unsuccessful.GetEvidence()
		out.Observation.Effect = domain.EffectUnsuccessful
		out.Observation.UnsuccessfulReason = domain.OutcomeNotAchieved
	case *r.Progress_Pending:
		evidence = effect.Pending.GetEvidence()
		out.Observation.Effect = domain.EffectPending
	case *r.Progress_Unknown:
	default:
		return out, executor.ErrEvidence
	}
	if evidence != nil {
		if bridge.ValidateAcquisitionEffect(evidence, w.Acquisition) != nil {
			return out, executor.ErrEvidence
		}
		d := evidence.GetAcquisition()
		out.LaborFinished, out.OutputComplete, out.OutputObserved, out.ProducedUnits = d.GetLaborFinished(), d.GetOutputComplete(), d.GetOutputObserved(), d.GetProducedUnits()
		if out.Observation.Effect == domain.EffectPending && (!d.GetDesignated() || d.GetLaborFinished()) {
			return out, executor.ErrEvidence
		}
	}
	out.ObservedAt = b.clock.Now()
	return out, nil
}

// Composition preserves only the capabilities actually configured for this session.
type acquisitionExecutionBoundary struct {
	executor.Boundary
	executor.AcquisitionBoundary
}
type acquisitionSupplyBoundary struct {
	*acquisitionExecutionBoundary
	executor.SupplyBoundary
}
type acquisitionWorkBoundary struct {
	*acquisitionExecutionBoundary
	executor.WorkBoundary
}
type acquisitionWorkSupplyBoundary struct {
	*acquisitionExecutionBoundary
	executor.WorkBoundary
	executor.SupplyBoundary
}

func withAcquisition(base executor.Boundary, acquisition executor.AcquisitionBoundary) executor.Boundary {
	b := &acquisitionExecutionBoundary{base, acquisition}
	supply, hasSupply := base.(executor.SupplyBoundary)
	work, hasWork := base.(executor.WorkBoundary)
	if hasSupply && hasWork {
		return &acquisitionWorkSupplyBoundary{b, work, supply}
	}
	if hasSupply {
		return &acquisitionSupplyBoundary{b, supply}
	}
	if hasWork {
		return &acquisitionWorkBoundary{b, work}
	}
	return b
}
