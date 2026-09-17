// Package growercrop wires the one-shot patch of the crop a plant grower
// sows, the same Settings-style boundary shape as
// internal/buildingruntime/bedmedical and buildingtemperature (fresh CAS read, native
// preview, emergency check, then a direct write/lookup/observe) rather than
// the live-dispatch Owner/Attempt Job pattern of pawn-order boundaries.
package growercrop

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

type Native interface {
	ReadGrowerCropTarget(context.Context, *c.Identity, string) (bridge.GrowerCropTarget, bridge.Result, error)
	PreviewGrowerCrop(context.Context, *c.Identity, domain.GrowerCrop) (*op.PreviewReply, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	LookupGrowerCrop(context.Context, bridge.GrowerCropAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveGrowerCrop(context.Context, bridge.GrowerCropAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type Writer interface {
	ApplyGrowerCrop(context.Context, *a.WritePrecondition, domain.GrowerCrop) (*op.ExecuteReply, bridge.Result, error)
}
type Capabilities struct {
	Native Native
	Writer Writer
}
type Boundary struct {
	*boundary.Boundary
	crop Capabilities
}

func NewBoundary(base *boundary.Boundary, crop Capabilities) *Boundary {
	return &Boundary{Boundary: base, crop: crop}
}

func (b *Boundary) readGrower(ctx context.Context, t domain.GrowerCrop, s domain.GenerationSnapshot) (bridge.GrowerCropTarget, domain.Tick, error) {
	target, _, err := b.crop.Native.ReadGrowerCropTarget(ctx, boundary.Identity(s), t.Thing())
	if err != nil {
		return bridge.GrowerCropTarget{}, 0, err
	}
	if _, err = boundary.Context(target.Context, s); err != nil {
		return bridge.GrowerCropTarget{}, 0, err
	}
	if target.Thing != t.Thing() {
		return bridge.GrowerCropTarget{}, 0, executor.ErrHeld
	}
	return target, domain.Tick(target.Context.GetTick()), nil
}
func (b *Boundary) InspectGrowerCrop(ctx context.Context, t executor.Target) (executor.GrowerCropInspection, error) {
	out := executor.GrowerCropInspection{StartedAt: b.Clock.Now()}
	crop, ok := t.Action.GrowerCrop()
	if !ok {
		return out, executor.ErrEvidence
	}
	target, tick, err := b.readGrower(ctx, crop, t.Snapshot)
	if err != nil {
		return out, err
	}
	if target.Token != crop.BeforeToken() {
		return out, executor.ErrHeld
	}
	preview, _, err := b.crop.Native.PreviewGrowerCrop(ctx, boundary.Identity(t.Snapshot), crop)
	if err != nil {
		return out, err
	}
	v := preview.GetEvaluated()
	if v == nil || !v.GetAccepted() {
		return out, executor.ErrHeld
	}
	if _, err = boundary.Context(v.Context, t.Snapshot); err != nil {
		return out, err
	}
	emergency, _, err := b.crop.Native.ReadEmergency(ctx, boundary.Identity(t.Snapshot))
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(emergency.Context, t.Snapshot); err != nil {
		return out, err
	}
	if domain.Tick(v.Context.GetTick()) != tick || domain.Tick(emergency.Context.GetTick()) != tick {
		return out, executor.ErrHeld
	}
	out.Current, out.Tick, out.Crop, out.SnapshotToken, out.Accepted = t.Snapshot, tick, crop, crop.BeforeToken(), true
	out.Emergency, err = policy.NewEmergencySnapshot(t.Snapshot, tick, emergency.Facts)
	out.ObservedAt = b.Clock.Now()
	return out, err
}
func (b *Boundary) attempt(p executor.Placement) bridge.GrowerCropAttempt {
	t, _ := p.Action.GrowerCrop()
	return bridge.GrowerCropAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: b.Attempt(p), Generation: uint64(p.Snapshot.Native), Crop: t}
}
func (b *Boundary) ApplyGrowerCrop(ctx context.Context, d executor.GrowerCropDispatch) (executor.Receipt, error) {
	p := d.Attempt
	t, ok := p.Action.GrowerCrop()
	return b.DispatchWrite(ctx, p,
		func() error {
			if !ok || d.SnapshotToken != t.BeforeToken() {
				return executor.ErrEvidence
			}
			return nil
		},
		func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			return b.crop.Writer.ApplyGrowerCrop(ctx, pre, t)
		},
	)
}
func (b *Boundary) ObserveGrowerCrop(ctx context.Context, p executor.Placement, current domain.GenerationSnapshot) (executor.GrowerCropEvidence, error) {
	out := executor.GrowerCropEvidence{StartedAt: b.Clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundary.World(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	wanted := b.attempt(p)
	lookup, _, err := b.crop.Native.LookupGrowerCrop(ctx, wanted)
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
	reply, _, err := b.crop.Native.ObserveGrowerCrop(ctx, wanted, admitted)
	if err != nil {
		return out, err
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, wanted.Attempt) {
		return out, executor.ErrEvidence
	}
	if _, err = boundary.Context(v.Context, current); err != nil {
		return out, err
	}
	if v.Context.GetTick() < int64(p.Tick) {
		return out, executor.ErrEvidence
	}
	out.Observation.Tick, out.Observation.Causality = domain.Tick(v.Context.GetTick()), domain.AfterDispatch
	out.Crop = wanted.Crop
	if v.GetUnknown() != nil {
		out.ObservedAt = b.Clock.Now()
		return out, nil
	}
	target, tick, err := b.readGrower(ctx, wanted.Crop, current)
	if err != nil {
		return out, err
	}
	if tick != domain.Tick(v.Context.GetTick()) || !v.GetCompleteInspection() {
		return out, executor.ErrEvidence
	}
	matches := target.Crop == wanted.Crop.Crop()
	var evidence *r.EffectEvidence
	if completed := v.GetCompleted(); completed != nil && matches {
		out.Observation.Effect = domain.EffectCompleted
		evidence = completed.Evidence
	} else if failed := v.GetUnsuccessful(); failed != nil && !matches && failed.GetReason() == r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
		out.Observation.Effect = domain.EffectUnsuccessful
		out.Observation.UnsuccessfulReason = domain.OutcomeNotAchieved
		evidence = failed.Evidence
	} else {
		return out, executor.ErrEvidence
	}
	snapshot := evidence.GetSettings().GetSnapshot()
	if snapshot == nil || snapshot.GetEntityId() != wanted.Crop.Thing() || snapshot.GetBeforeToken() != wanted.Crop.BeforeToken() || snapshot.GetAfterToken() != target.Token {
		return out, executor.ErrEvidence
	}
	out.Complete, out.Matches, out.ObservedAt = true, domain.Known(matches), b.Clock.Now()
	return out, nil
}

var _ executor.GrowerCropBoundary = (*Boundary)(nil)
