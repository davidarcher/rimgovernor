package work

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type WorkNative interface {
	ReadRoutinePawns(context.Context, *c.Identity, []string) (*o.ListPawnsReply, bridge.Result, error)
	PreviewWorkAssignment(context.Context, *c.Identity, domain.WorkAssignment) (*op.PreviewReply, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	LookupWorkAssignment(context.Context, bridge.WorkAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveWorkAssignment(context.Context, bridge.WorkAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type WorkWriter interface {
	AssignWork(context.Context, *a.WritePrecondition, domain.WorkAssignment) (*op.ExecuteReply, bridge.Result, error)
}
type WorkCapabilities struct {
	Native WorkNative
	Writer WorkWriter
}
type WorkBoundary struct {
	*boundary.Boundary
	work WorkCapabilities
}

func NewWorkBoundary(base *boundary.Boundary, work WorkCapabilities) *WorkBoundary {
	return &WorkBoundary{Boundary: base, work: work}
}

func (b *WorkBoundary) readWork(ctx context.Context, w domain.WorkAssignment, s domain.GenerationSnapshot) (*o.PawnSettings, domain.Tick, error) {
	reply, _, err := b.work.Native.ReadRoutinePawns(ctx, boundary.Identity(s), []string{string(w.Pawn())})
	if err != nil {
		return nil, 0, err
	}
	v := reply.GetObserved()
	if err = bridge.ValidateRoutinePawnSnapshot(v, boundary.Identity(s), []string{string(w.Pawn())}); err != nil {
		return nil, 0, err
	}
	if _, err = boundary.Context(v.Context, s); err != nil {
		return nil, 0, err
	}
	if len(v.Pawns) != 1 || v.Pawns[0].Pawn.GetId() != string(w.Pawn()) || v.Pawns[0].Settings == nil {
		return nil, 0, executor.ErrHeld
	}
	settings := v.Pawns[0].Settings
	if settings.Snapshot == nil || settings.ManualWorkPriorities == nil || settings.GetManualWorkPriorities() != w.Manual() {
		return nil, 0, executor.ErrHeld
	}
	return settings, domain.Tick(v.Context.GetTick()), nil
}
func (b *WorkBoundary) InspectWork(ctx context.Context, t executor.Target) (executor.WorkInspection, error) {
	out := executor.WorkInspection{StartedAt: b.Clock.Now()}
	w, ok := t.Action.WorkAssignment()
	if !ok {
		return out, executor.ErrEvidence
	}
	settings, tick, err := b.readWork(ctx, w, t.Snapshot)
	if err != nil {
		return out, err
	}
	if settings.Snapshot.GetToken() != w.BeforeToken() {
		return out, executor.ErrHeld
	}
	preview, _, err := b.work.Native.PreviewWorkAssignment(ctx, boundary.Identity(t.Snapshot), w)
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
	emergency, _, err := b.work.Native.ReadEmergency(ctx, boundary.Identity(t.Snapshot))
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(emergency.Context, t.Snapshot); err != nil {
		return out, err
	}
	if domain.Tick(v.Context.GetTick()) != tick || domain.Tick(emergency.Context.GetTick()) != tick {
		return out, executor.ErrHeld
	}
	out.Current, out.Tick, out.Work, out.SnapshotToken, out.Accepted = t.Snapshot, tick, w, w.BeforeToken(), true
	out.Emergency, err = policy.NewEmergencySnapshot(t.Snapshot, tick, emergency.Facts)
	out.ObservedAt = b.Clock.Now()
	return out, err
}
func (b *WorkBoundary) workAttempt(p executor.Placement) bridge.WorkAttempt {
	w, _ := p.Action.WorkAssignment()
	return bridge.WorkAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: b.Attempt(p), Owner: &a.Owner{ControllerSessionId: proto.String(b.Session), PlayerDirection: proto.Uint64(uint64(p.Snapshot.Direction))}, Generation: uint64(p.Snapshot.Native), Work: w}
}
func (b *WorkBoundary) AssignWork(ctx context.Context, d executor.WorkDispatch) (executor.Receipt, error) {
	p := d.Attempt
	w, ok := p.Action.WorkAssignment()
	return b.DispatchWrite(ctx, p,
		func() error {
			if !ok || d.SnapshotToken != w.BeforeToken() {
				return executor.ErrEvidence
			}
			return nil
		},
		func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			return b.work.Writer.AssignWork(ctx, pre, w)
		},
	)
}
func (b *WorkBoundary) ObserveWork(ctx context.Context, p executor.Placement, current domain.GenerationSnapshot) (executor.WorkEvidence, error) {
	out := executor.WorkEvidence{StartedAt: b.Clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundary.World(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	wanted := b.workAttempt(p)
	lookup, _, err := b.work.Native.LookupWorkAssignment(ctx, wanted)
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
	reply, _, err := b.work.Native.ObserveWorkAssignment(ctx, wanted, admitted)
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
	out.Work = wanted.Work
	if v.GetUnknown() != nil {
		out.ObservedAt = b.Clock.Now()
		return out, nil
	}
	settings, tick, err := b.readWork(ctx, wanted.Work, current)
	if err != nil {
		return out, err
	}
	if tick != domain.Tick(v.Context.GetTick()) || !v.GetCompleteInspection() {
		return out, executor.ErrEvidence
	}
	values := map[string]int32{}
	for _, setting := range settings.Work {
		if setting.Priority == nil {
			return out, executor.ErrHeld
		}
		values[setting.GetDefName()] = setting.GetPriority()
	}
	matches := true
	for _, desired := range wanted.Work.Settings() {
		actual, known := values[desired.Definition]
		matches = matches && known && actual == desired.Priority
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
	if snapshot == nil || snapshot.GetEntityId() != string(wanted.Work.Pawn()) || snapshot.GetBeforeToken() != wanted.Work.BeforeToken() || snapshot.GetAfterToken() != settings.Snapshot.GetToken() {
		return out, executor.ErrEvidence
	}
	out.Complete, out.Matches, out.ObservedAt = true, domain.Known(matches), b.Clock.Now()
	return out, nil
}
