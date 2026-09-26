// Package movebuilding is the move family's executor boundary (#808): the
// cut-plant boundary's shape over the native Reinstall preview and the
// InstallBuilding write on one exact installed building.
package movebuilding

import (
	"context"
	"errors"

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
	PreviewMoveBuilding(context.Context, *c.Identity, domain.MoveBuilding) (*op.PreviewReply, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	LookupMoveBuilding(context.Context, bridge.MoveBuildingAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveMoveBuilding(context.Context, bridge.MoveBuildingAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type Writer interface {
	ApplyMoveBuilding(context.Context, *a.WritePrecondition, domain.MoveBuilding) (*op.ExecuteReply, bridge.Result, error)
}
type Capabilities struct {
	Native Native
	Writer Writer
}
type Boundary struct {
	*boundary.Boundary
	move Capabilities
}

func NewBoundary(base *boundary.Boundary, move Capabilities) *Boundary {
	return &Boundary{Boundary: base, move: move}
}

// InspectMoveBuilding previews the exact move natively: the preview is the
// whole target read (building present, installed, the player's, the
// destination placeable). NotFound is the building gone for good; any other
// refusal holds the action for the next inspection.
func (b *Boundary) InspectMoveBuilding(ctx context.Context, target executor.Target) (executor.MoveBuildingInspection, error) {
	out := executor.MoveBuildingInspection{StartedAt: b.Clock.Now()}
	move, ok := target.Action.MoveBuilding()
	if !ok {
		return out, executor.ErrEvidence
	}
	preview, _, err := b.move.Native.PreviewMoveBuilding(ctx, boundary.Identity(target.Snapshot), move)
	var refused *bridge.NativeFailure
	if errors.As(err, &refused) && refused.Value != nil {
		if refused.Value.GetCode() == c.FailureCode_FAILURE_CODE_NOT_FOUND {
			return out, executor.ErrMoveBuildingAbsent
		}
		return out, executor.ErrHeld
	}
	if err != nil {
		return out, err
	}
	v := preview.GetEvaluated()
	if v == nil || !v.GetAccepted() {
		return out, executor.ErrHeld
	}
	current, err := boundary.Context(v.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	emergency, _, err := b.move.Native.ReadEmergency(ctx, boundary.Identity(current))
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(emergency.Context, current); err != nil {
		return out, err
	}
	if emergency.Context.GetTick() < v.Context.GetTick() {
		return out, executor.ErrHeld
	}
	out.Current, out.Tick, out.Move, out.Accepted = current, domain.Tick(v.Context.GetTick()), move, true
	out.Emergency, err = policy.NewEmergencySnapshot(current, out.Tick, emergency.Facts)
	out.ObservedAt = b.Clock.Now()
	return out, err
}
func (b *Boundary) attempt(p executor.Placement) bridge.MoveBuildingAttempt {
	move, _ := p.Action.MoveBuilding()
	return bridge.MoveBuildingAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: b.Attempt(p), Generation: uint64(p.Snapshot.Native), Move: move}
}
func (b *Boundary) ApplyMoveBuilding(ctx context.Context, request executor.MoveBuildingDispatch) (executor.Receipt, error) {
	p := request.Attempt
	move, ok := p.Action.MoveBuilding()
	return b.DispatchWrite(ctx, p,
		func() error {
			if !ok {
				return executor.ErrEvidence
			}
			return nil
		},
		func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			return b.move.Writer.ApplyMoveBuilding(ctx, pre, move)
		},
	)
}
func (b *Boundary) ObserveMoveBuilding(ctx context.Context, p executor.Placement, current domain.GenerationSnapshot) (executor.MoveBuildingEvidence, error) {
	out := executor.MoveBuildingEvidence{StartedAt: b.Clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundary.World(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	w := b.attempt(p)
	lookup, _, err := b.move.Native.LookupMoveBuilding(ctx, w)
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
	reply, _, err := b.move.Native.ObserveMoveBuilding(ctx, w, admitted)
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
	out.Complete, out.Move = v.GetCompleteInspection(), w.Move
	// The bridge has already matched each effect to this exact move and the
	// stage its position requires.
	switch v.Effect.(type) {
	case *r.Progress_Completed:
		out.Observation.Effect, out.Installed = domain.EffectCompleted, domain.Known(true)
	case *r.Progress_Unsuccessful:
		out.Observation.Effect, out.Observation.UnsuccessfulReason, out.Installed = domain.EffectUnsuccessful, domain.OutcomeNotAchieved, domain.Known(false)
	case *r.Progress_Pending:
		out.Observation.Effect, out.Installed = domain.EffectPending, domain.Known(false)
	case *r.Progress_Unknown:
	default:
		return out, executor.ErrEvidence
	}
	out.ObservedAt = b.Clock.Now()
	return out, nil
}
