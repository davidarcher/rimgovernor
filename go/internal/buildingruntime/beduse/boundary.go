// Package beduse wires the one-shot patch of a humanlike bed's use (its medical
// flag, or set for prisoners, #880), the same Settings-style boundary shape as
// internal/buildingruntime/buildingtemperature (fresh CAS read, native
// preview, emergency check, then a direct write/lookup/observe) rather than
// the live-dispatch Owner/Attempt Job pattern of pawn-order boundaries.
package beduse

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
	ReadBedUseTarget(context.Context, *c.Identity, string) (bridge.BedUseTarget, bridge.Result, error)
	PreviewBedUse(context.Context, *c.Identity, domain.BedUse) (*op.PreviewReply, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	LookupBedUse(context.Context, bridge.BedUseAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveBedUse(context.Context, bridge.BedUseAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type Writer interface {
	ApplyBedUse(context.Context, *a.WritePrecondition, domain.BedUse) (*op.ExecuteReply, bridge.Result, error)
}
type Capabilities struct {
	Native Native
	Writer Writer
}
type Boundary struct {
	*boundary.Boundary
	medical Capabilities
}

func NewBoundary(base *boundary.Boundary, medical Capabilities) *Boundary {
	return &Boundary{Boundary: base, medical: medical}
}

func (b *Boundary) readBed(ctx context.Context, t domain.BedUse, s domain.GenerationSnapshot) (bridge.BedUseTarget, domain.Tick, error) {
	target, _, err := b.medical.Native.ReadBedUseTarget(ctx, boundary.Identity(s), t.Thing())
	if err != nil {
		return bridge.BedUseTarget{}, 0, err
	}
	if _, err = boundary.Context(target.Context, s); err != nil {
		return bridge.BedUseTarget{}, 0, err
	}
	if target.Thing != t.Thing() {
		return bridge.BedUseTarget{}, 0, executor.ErrHeld
	}
	return target, domain.Tick(target.Context.GetTick()), nil
}
func (b *Boundary) InspectBedUse(ctx context.Context, t executor.Target) (executor.BedUseInspection, error) {
	out := executor.BedUseInspection{StartedAt: b.Clock.Now()}
	medical, ok := t.Action.BedUse()
	if !ok {
		return out, executor.ErrEvidence
	}
	target, tick, err := b.readBed(ctx, medical, t.Snapshot)
	if err != nil {
		return out, err
	}
	if target.Token != medical.BeforeToken() {
		return out, executor.ErrHeld
	}
	preview, _, err := b.medical.Native.PreviewBedUse(ctx, boundary.Identity(t.Snapshot), medical)
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
	emergency, _, err := b.medical.Native.ReadEmergency(ctx, boundary.Identity(t.Snapshot))
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(emergency.Context, t.Snapshot); err != nil {
		return out, err
	}
	// The three reads need only be ordered, not simultaneous: the target's
	// before-token already binds the write to the settings the read listed,
	// so a tick that advanced between them is a running clock, not stale
	// evidence. Demanding one tick held every dispatch until the game
	// paused (#195); bills, zones and supplies accept the same order.
	if v.Context.GetTick() < int64(tick) {
		return out, executor.ErrHeld
	}
	tick = domain.Tick(v.Context.GetTick())
	out.Current, out.Tick, out.Medical, out.SnapshotToken, out.Accepted = t.Snapshot, tick, medical, medical.BeforeToken(), true
	out.Emergency, err = policy.NewEmergencySnapshot(t.Snapshot, tick, emergency.Facts)
	out.ObservedAt = b.Clock.Now()
	return out, err
}
func (b *Boundary) attempt(p executor.Placement) bridge.BedUseAttempt {
	t, _ := p.Action.BedUse()
	return bridge.BedUseAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: b.Attempt(p), Generation: uint64(p.Snapshot.Native), Medical: t}
}
func (b *Boundary) ApplyBedUse(ctx context.Context, d executor.BedUseDispatch) (executor.Receipt, error) {
	p := d.Attempt
	t, ok := p.Action.BedUse()
	return b.DispatchWrite(ctx, p,
		func() error {
			if !ok || d.SnapshotToken != t.BeforeToken() {
				return executor.ErrEvidence
			}
			return nil
		},
		func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			return b.medical.Writer.ApplyBedUse(ctx, pre, t)
		},
	)
}
func (b *Boundary) ObserveBedUse(ctx context.Context, p executor.Placement, current domain.GenerationSnapshot) (executor.BedUseEvidence, error) {
	out := executor.BedUseEvidence{StartedAt: b.Clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundary.World(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	wanted := b.attempt(p)
	lookup, _, err := b.medical.Native.LookupBedUse(ctx, wanted)
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
	reply, _, err := b.medical.Native.ObserveBedUse(ctx, wanted, admitted)
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
	out.Medical = wanted.Medical
	if v.GetUnknown() != nil {
		out.ObservedAt = b.Clock.Now()
		return out, nil
	}
	target, tick, err := b.readBed(ctx, wanted.Medical, current)
	if err != nil {
		return out, err
	}
	if tick != domain.Tick(v.Context.GetTick()) || !v.GetCompleteInspection() {
		return out, executor.ErrEvidence
	}
	matches := target.Medical == wanted.Medical.Medical()
	if wanted.Medical.Prisoners() {
		matches = target.Prisoners
	}
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
	if snapshot == nil || snapshot.GetEntityId() != wanted.Medical.Thing() || snapshot.GetBeforeToken() != wanted.Medical.BeforeToken() || snapshot.GetAfterToken() != target.Token {
		return out, executor.ErrEvidence
	}
	out.Complete, out.Matches, out.ObservedAt = true, domain.Known(matches), b.Clock.Now()
	return out, nil
}

var _ executor.BedUseBoundary = (*Boundary)(nil)
