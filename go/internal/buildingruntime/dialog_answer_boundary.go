package buildingruntime

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

// DialogAnswerNative and DialogAnswerWriter narrow *bridge.Client and
// *bridge.DialogControl to what dialogAnswerBoundary consumes, the same
// split ConfirmColonyNames uses (#156).
type DialogAnswerNative interface {
	PreviewAnswerDialog(context.Context, *c.Identity, int32, int32, string) (*op.PreviewReply, bridge.Result, error)
	LookupAnswerDialog(context.Context, bridge.DialogAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveAnswerDialogProgress(context.Context, bridge.DialogAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type DialogAnswerWriter interface {
	AnswerDialog(context.Context, *a.WritePrecondition, int32, int32, string) (*op.ExecuteReply, bridge.Result, error)
}
type DialogAnswerCapabilities struct {
	Native DialogAnswerNative
	Writer DialogAnswerWriter
}
type dialogAnswerBoundary struct {
	*boundary.Boundary
	dialog DialogAnswerCapabilities
}

// InspectDialogAnswer re-runs the native validators against the exact
// targeted window/option immediately before dispatch: PreviewAnswerDialog
// alone both re-observes freshness (native refuses on any drift from the
// exact observed window, option position and label, see
// NativeChoiceDialogOperations.cs) and whether the option is selectable now.
func (b *dialogAnswerBoundary) InspectDialogAnswer(ctx context.Context, target executor.Target) (executor.DialogAnswerInspection, error) {
	out := executor.DialogAnswerInspection{StartedAt: b.Clock.Now()}
	value, ok := target.Action.DialogAnswer()
	if !ok {
		return out, executor.ErrEvidence
	}
	preview, _, err := b.dialog.Native.PreviewAnswerDialog(ctx, boundary.Identity(target.Snapshot), value.WindowID(), value.OptionIndex(), value.OptionLabel())
	if err != nil {
		return out, err
	}
	v := preview.GetEvaluated()
	if v == nil || v.Projected != nil {
		return out, executor.ErrHeld
	}
	current, err := boundary.Context(v.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	out.Facts = policy.DialogAnswerFacts{Snapshot: current, Tick: domain.Tick(v.Context.GetTick()), Accepted: optionalBool(v.Accepted)}
	out.ObservedAt = b.Clock.Now()
	return out, nil
}
func (b *dialogAnswerBoundary) dialogAttempt(p executor.Placement, windowID, optionIndex int32, label string) bridge.DialogAttempt {
	return bridge.DialogAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: b.Attempt(p), Generation: uint64(p.Snapshot.Native), WindowID: windowID, OptionIndex: optionIndex, OptionLabel: label}
}
func (b *dialogAnswerBoundary) AnswerDialog(ctx context.Context, d executor.DialogAnswerDispatch) (executor.Receipt, error) {
	p := d.Attempt
	_, ok := p.Action.DialogAnswer()
	return b.DispatchWrite(ctx, p,
		func() error {
			if !ok {
				return executor.ErrEvidence
			}
			return nil
		},
		func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			return b.dialog.Writer.AnswerDialog(ctx, pre, d.WindowID, d.OptionIndex, d.OptionLabel)
		},
	)
}
func (b *dialogAnswerBoundary) ObserveDialogAnswer(ctx context.Context, p executor.Placement, current domain.GenerationSnapshot) (executor.DialogAnswerEvidence, error) {
	out := executor.DialogAnswerEvidence{StartedAt: b.Clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundary.World(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	value, ok := p.Action.DialogAnswer()
	if !ok {
		return out, executor.ErrEvidence
	}
	w := b.dialogAttempt(p, value.WindowID(), value.OptionIndex(), value.OptionLabel())
	lookup, _, err := b.dialog.Native.LookupAnswerDialog(ctx, w)
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
	reply, _, err := b.dialog.Native.ObserveAnswerDialogProgress(ctx, w, admitted)
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
	out.WindowID = value.WindowID()
	switch effect := v.Effect.(type) {
	case *r.Progress_Completed:
		if !v.GetCompleteInspection() {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect = domain.EffectCompleted
		out.Complete, out.Matches = true, domain.Known(true)
	case *r.Progress_Unsuccessful:
		if !v.GetCompleteInspection() || effect.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect = domain.EffectUnsuccessful
		out.Observation.UnsuccessfulReason = domain.OutcomeNotAchieved
		out.Complete, out.Matches = true, domain.Known(false)
	case *r.Progress_Unknown:
	default:
		return out, executor.ErrEvidence
	}
	out.ObservedAt = b.Clock.Now()
	return out, nil
}
