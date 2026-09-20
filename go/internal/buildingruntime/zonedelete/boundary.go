// Package zonedelete wires the one-shot deletion of one managed zone the
// layout tidy re-sited (#611), the same Settings-style boundary shape as
// internal/buildingruntime/claimbuilding (fresh per-zone CAS read, native
// preview, emergency check, then a direct write/lookup/observe). The
// post-write readback is the zone listing: a completed deletion is a zone
// the census no longer lists.
package zonedelete

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
	ReadZoneDeleteTarget(context.Context, *c.Identity, string) (bridge.ZoneDeleteTarget, bridge.Result, error)
	PreviewZoneDelete(context.Context, *c.Identity, domain.ZoneDelete) (*op.PreviewReply, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	LookupZoneDelete(context.Context, bridge.ZoneDeleteAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveZoneDelete(context.Context, bridge.ZoneDeleteAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type Writer interface {
	ApplyZoneDelete(context.Context, *a.WritePrecondition, domain.ZoneDelete) (*op.ExecuteReply, bridge.Result, error)
}
type Capabilities struct {
	Native Native
	Writer Writer
}
type Boundary struct {
	*boundary.Boundary
	del Capabilities
}

func NewBoundary(base *boundary.Boundary, del Capabilities) *Boundary {
	return &Boundary{Boundary: base, del: del}
}

func (b *Boundary) readTarget(ctx context.Context, t domain.ZoneDelete, s domain.GenerationSnapshot) (bridge.ZoneDeleteTarget, domain.Tick, error) {
	target, _, err := b.del.Native.ReadZoneDeleteTarget(ctx, boundary.Identity(s), t.Zone())
	if err != nil {
		return bridge.ZoneDeleteTarget{}, 0, err
	}
	if _, err = boundary.Context(target.Context, s); err != nil {
		return bridge.ZoneDeleteTarget{}, 0, err
	}
	if target.Zone != t.Zone() {
		return bridge.ZoneDeleteTarget{}, 0, executor.ErrHeld
	}
	return target, domain.Tick(target.Context.GetTick()), nil
}
func (b *Boundary) InspectZoneDelete(ctx context.Context, t executor.Target) (executor.ZoneDeleteInspection, error) {
	out := executor.ZoneDeleteInspection{StartedAt: b.Clock.Now()}
	del, ok := t.Action.ZoneDelete()
	if !ok {
		return out, executor.ErrEvidence
	}
	target, tick, err := b.readTarget(ctx, del, t.Snapshot)
	if err != nil {
		return out, err
	}
	if !target.Present || target.Token != del.BeforeToken() {
		return out, executor.ErrHeld
	}
	preview, _, err := b.del.Native.PreviewZoneDelete(ctx, boundary.Identity(t.Snapshot), del)
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
	emergency, _, err := b.del.Native.ReadEmergency(ctx, boundary.Identity(t.Snapshot))
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
	// paused (#195); bills, zones and supplies accept the same order. The
	// emergency read may come from the fact cache a bounded advance behind
	// the target read, the step's first (domain.Tick.Covers, #244).
	if v.Context.GetTick() < int64(tick) || !domain.Tick(emergency.Context.GetTick()).Covers(tick) {
		return out, executor.ErrHeld
	}
	tick = domain.Tick(v.Context.GetTick())
	out.Current, out.Tick, out.Delete, out.SnapshotToken, out.Accepted = t.Snapshot, tick, del, del.BeforeToken(), true
	out.Emergency, err = policy.NewEmergencySnapshot(t.Snapshot, tick, emergency.Facts)
	out.ObservedAt = b.Clock.Now()
	return out, err
}
func (b *Boundary) attempt(p executor.Placement) bridge.ZoneDeleteAttempt {
	t, _ := p.Action.ZoneDelete()
	return bridge.ZoneDeleteAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: b.Attempt(p), Generation: uint64(p.Snapshot.Native), Delete: t}
}
func (b *Boundary) ApplyZoneDelete(ctx context.Context, d executor.ZoneDeleteDispatch) (executor.Receipt, error) {
	p := d.Attempt
	t, ok := p.Action.ZoneDelete()
	return b.DispatchWrite(ctx, p,
		func() error {
			if !ok || d.SnapshotToken != t.BeforeToken() {
				return executor.ErrEvidence
			}
			return nil
		},
		func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			return b.del.Writer.ApplyZoneDelete(ctx, pre, t)
		},
	)
}
func (b *Boundary) ObserveZoneDelete(ctx context.Context, p executor.Placement, current domain.GenerationSnapshot) (executor.ZoneDeleteEvidence, error) {
	out := executor.ZoneDeleteEvidence{StartedAt: b.Clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundary.World(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	wanted := b.attempt(p)
	lookup, _, err := b.del.Native.LookupZoneDelete(ctx, wanted)
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
	reply, _, err := b.del.Native.ObserveZoneDelete(ctx, wanted, admitted)
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
	out.Delete = wanted.Delete
	if v.GetUnknown() != nil {
		out.ObservedAt = b.Clock.Now()
		return out, nil
	}
	target, tick, err := b.readTarget(ctx, wanted.Delete, current)
	if err != nil {
		return out, err
	}
	if tick != domain.Tick(v.Context.GetTick()) || !v.GetCompleteInspection() {
		return out, executor.ErrEvidence
	}
	matches := !target.Present
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
	if gone, err := bridge.ZoneDeleted(evidence, wanted.Delete); err != nil || gone != matches {
		return out, executor.ErrEvidence
	}
	out.Complete, out.Matches, out.ObservedAt = true, domain.Known(matches), b.Clock.Now()
	return out, nil
}

var _ executor.ZoneDeleteBoundary = (*Boundary)(nil)
