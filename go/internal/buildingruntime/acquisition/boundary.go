package acquisition

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
type AcquisitionBoundary struct {
	*boundary.Boundary
	acquisition AcquisitionCapabilities
}

func NewAcquisitionBoundary(base *boundary.Boundary, acquisition AcquisitionCapabilities) *AcquisitionBoundary {
	return &AcquisitionBoundary{Boundary: base, acquisition: acquisition}
}

func (b *AcquisitionBoundary) InspectAcquisition(ctx context.Context, target executor.Target) (executor.AcquisitionInspection, error) {
	out := executor.AcquisitionInspection{StartedAt: b.Clock.Now()}
	acquisition, ok := target.Action.Acquisition()
	if !ok {
		return out, executor.ErrEvidence
	}
	read, _, err := b.acquisition.Native.ReadAcquisition(ctx, boundary.Identity(target.Snapshot), acquisition.Cell())
	if err != nil {
		return out, err
	}
	current, err := boundary.Context(read.Context, target.Snapshot)
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
	preview, _, err := b.acquisition.Native.PreviewAcquisition(ctx, boundary.Identity(current), selected)
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
	if _, err = boundary.Context(v.Context, current); err != nil {
		return out, err
	}
	emergency, _, err := b.acquisition.Native.ReadEmergency(ctx, boundary.Identity(current))
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(emergency.Context, current); err != nil {
		return out, err
	}
	if read.Context.GetTick() != v.Context.GetTick() || v.Context.GetTick() != emergency.Context.GetTick() {
		return out, executor.ErrHeld
	}
	out.Current, out.Tick, out.Acquisition, out.SnapshotToken, out.Accepted = current, domain.Tick(v.Context.GetTick()), acquisition, selected.Token, true
	out.Emergency, err = policy.NewEmergencySnapshot(current, out.Tick, emergency.Facts)
	out.ObservedAt = b.Clock.Now()
	return out, err
}
func (b *AcquisitionBoundary) acquisitionAttempt(p executor.Placement) bridge.AcquisitionAttempt {
	acquisition, _ := p.Action.Acquisition()
	return bridge.AcquisitionAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: b.Attempt(p), Generation: uint64(p.Snapshot.Native), Acquisition: acquisition}
}
func (b *AcquisitionBoundary) Acquire(ctx context.Context, request executor.AcquisitionDispatch) (executor.Receipt, error) {
	p := request.Attempt
	acquisition, ok := p.Action.Acquisition()
	return b.DispatchWrite(ctx, p,
		func() error {
			if !ok {
				return executor.ErrEvidence
			}
			return nil
		},
		func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			return b.acquisition.Writer.Acquire(ctx, pre, bridge.AcquisitionTarget{Acquisition: acquisition, Token: request.SnapshotToken})
		},
	)
}
func (b *AcquisitionBoundary) ObserveAcquisition(ctx context.Context, p executor.Placement, current domain.GenerationSnapshot) (executor.AcquisitionEvidence, error) {
	out := executor.AcquisitionEvidence{StartedAt: b.Clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundary.World(current, p.Snapshot) {
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
	if err = boundary.Admission(admitted, p, b.Session); err != nil {
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
	out.Observation.Snapshot, err = boundary.Context(v.Context, current)
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
	out.ObservedAt = b.Clock.Now()
	return out, nil
}
