// Package buildingtemperature wires the one-shot building target-temperature
// patch, mirroring internal/buildingruntime/work's Settings-style boundary
// shape (fresh CAS read, native preview, emergency check, then a direct
// write/lookup/observe) instead of the live-dispatch Owner/Attempt Job
// pattern used by pawn-order boundaries such as Tend/Rescue.
package buildingtemperature

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
	ReadBuildingTemperatureTarget(context.Context, *c.Identity, string) (bridge.BuildingTemperatureTarget, bridge.Result, error)
	PreviewBuildingTemperature(context.Context, *c.Identity, domain.BuildingTemperature) (*op.PreviewReply, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	LookupBuildingTemperature(context.Context, bridge.BuildingTemperatureAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveBuildingTemperature(context.Context, bridge.BuildingTemperatureAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type Writer interface {
	ApplyBuildingTemperature(context.Context, *a.WritePrecondition, domain.BuildingTemperature) (*op.ExecuteReply, bridge.Result, error)
}
type Capabilities struct {
	Native Native
	Writer Writer
}
type Boundary struct {
	*boundary.Boundary
	temperature Capabilities
}

func NewBoundary(base *boundary.Boundary, temperature Capabilities) *Boundary {
	return &Boundary{Boundary: base, temperature: temperature}
}

func (b *Boundary) readTemperature(ctx context.Context, t domain.BuildingTemperature, s domain.GenerationSnapshot) (bridge.BuildingTemperatureTarget, domain.Tick, error) {
	target, _, err := b.temperature.Native.ReadBuildingTemperatureTarget(ctx, boundary.Identity(s), t.Thing())
	if err != nil {
		return bridge.BuildingTemperatureTarget{}, 0, err
	}
	if _, err = boundary.Context(target.Context, s); err != nil {
		return bridge.BuildingTemperatureTarget{}, 0, err
	}
	if target.Thing != t.Thing() {
		return bridge.BuildingTemperatureTarget{}, 0, executor.ErrHeld
	}
	return target, domain.Tick(target.Context.GetTick()), nil
}
func (b *Boundary) InspectBuildingTemperature(ctx context.Context, t executor.Target) (executor.BuildingTemperatureInspection, error) {
	out := executor.BuildingTemperatureInspection{StartedAt: b.Clock.Now()}
	temperature, ok := t.Action.BuildingTemperature()
	if !ok {
		return out, executor.ErrEvidence
	}
	target, tick, err := b.readTemperature(ctx, temperature, t.Snapshot)
	if err != nil {
		return out, err
	}
	if target.Token != temperature.BeforeToken() {
		return out, executor.ErrHeld
	}
	preview, _, err := b.temperature.Native.PreviewBuildingTemperature(ctx, boundary.Identity(t.Snapshot), temperature)
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
	emergency, _, err := b.temperature.Native.ReadEmergency(ctx, boundary.Identity(t.Snapshot))
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
	if v.Context.GetTick() < int64(tick) || !domain.Tick(emergency.Context.GetTick()).Covers(tick) {
		return out, executor.ErrHeld
	}
	tick = domain.Tick(v.Context.GetTick())
	out.Current, out.Tick, out.Temperature, out.SnapshotToken, out.Accepted = t.Snapshot, tick, temperature, temperature.BeforeToken(), true
	out.Emergency, err = policy.NewEmergencySnapshot(t.Snapshot, tick, emergency.Facts)
	out.ObservedAt = b.Clock.Now()
	return out, err
}
func (b *Boundary) attempt(p executor.Placement) bridge.BuildingTemperatureAttempt {
	t, _ := p.Action.BuildingTemperature()
	return bridge.BuildingTemperatureAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: b.Attempt(p), Generation: uint64(p.Snapshot.Native), Temperature: t}
}
func (b *Boundary) ApplyBuildingTemperature(ctx context.Context, d executor.BuildingTemperatureDispatch) (executor.Receipt, error) {
	p := d.Attempt
	t, ok := p.Action.BuildingTemperature()
	return b.DispatchWrite(ctx, p,
		func() error {
			if !ok || d.SnapshotToken != t.BeforeToken() {
				return executor.ErrEvidence
			}
			return nil
		},
		func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			return b.temperature.Writer.ApplyBuildingTemperature(ctx, pre, t)
		},
	)
}
func (b *Boundary) ObserveBuildingTemperature(ctx context.Context, p executor.Placement, current domain.GenerationSnapshot) (executor.BuildingTemperatureEvidence, error) {
	out := executor.BuildingTemperatureEvidence{StartedAt: b.Clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundary.World(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	wanted := b.attempt(p)
	lookup, _, err := b.temperature.Native.LookupBuildingTemperature(ctx, wanted)
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
	reply, _, err := b.temperature.Native.ObserveBuildingTemperature(ctx, wanted, admitted)
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
	out.Temperature = wanted.Temperature
	if v.GetUnknown() != nil {
		out.ObservedAt = b.Clock.Now()
		return out, nil
	}
	target, tick, err := b.readTemperature(ctx, wanted.Temperature, current)
	if err != nil {
		return out, err
	}
	if tick != domain.Tick(v.Context.GetTick()) || !v.GetCompleteInspection() {
		return out, executor.ErrEvidence
	}
	matches := target.Temperature == wanted.Temperature.Celsius()
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
	if snapshot == nil || snapshot.GetEntityId() != wanted.Temperature.Thing() || snapshot.GetBeforeToken() != wanted.Temperature.BeforeToken() || snapshot.GetAfterToken() != target.Token {
		return out, executor.ErrEvidence
	}
	out.Complete, out.Matches, out.ObservedAt = true, domain.Known(matches), b.Clock.Now()
	return out, nil
}

var _ executor.BuildingTemperatureBoundary = (*Boundary)(nil)
