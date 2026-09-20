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

type ApparelPolicyNative interface {
	PreviewSetApparelPolicy(context.Context, *c.Identity, domain.ApparelPolicy) (*op.PreviewReply, bridge.Result, error)
	LookupSetApparelPolicy(context.Context, bridge.ApparelPolicyAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveSetApparelPolicyProgress(context.Context, bridge.ApparelPolicyAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type ApparelPolicyWriter interface {
	SetApparelPolicy(context.Context, *a.WritePrecondition, domain.ApparelPolicy) (*op.ExecuteReply, bridge.Result, error)
}
type ApparelPolicyCapabilities struct {
	Native ApparelPolicyNative
	Writer ApparelPolicyWriter
}
type apparelPolicyBoundary struct {
	*boundary.Boundary
	apparelPolicy ApparelPolicyCapabilities
}

func (b *apparelPolicyBoundary) InspectApparelPolicy(ctx context.Context, target executor.Target) (executor.ApparelPolicyInspection, error) {
	out := executor.ApparelPolicyInspection{StartedAt: b.Clock.Now()}
	value, ok := target.Action.ApparelPolicy()
	if !ok {
		return out, executor.ErrEvidence
	}
	preview, _, err := b.apparelPolicy.Native.PreviewSetApparelPolicy(ctx, boundary.Identity(target.Snapshot), value)
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
	out.Facts = policy.ApparelPolicyFacts{Snapshot: current, Tick: domain.Tick(v.Context.GetTick()), Accepted: optionalBool(v.Accepted)}
	out.ObservedAt = b.Clock.Now()
	return out, nil
}
func (b *apparelPolicyBoundary) apparelPolicyAttempt(p executor.Placement, value domain.ApparelPolicy) bridge.ApparelPolicyAttempt {
	return bridge.ApparelPolicyAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: b.Attempt(p), Generation: uint64(p.Snapshot.Native), Value: value}
}
func (b *apparelPolicyBoundary) SetApparelPolicy(ctx context.Context, d executor.ApparelPolicyDispatch) (executor.Receipt, error) {
	p := d.Attempt
	value, ok := p.Action.ApparelPolicy()
	return b.DispatchWrite(ctx, p,
		func() error {
			if !ok {
				return executor.ErrEvidence
			}
			return nil
		},
		func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			return b.apparelPolicy.Writer.SetApparelPolicy(ctx, pre, value)
		},
	)
}
func (b *apparelPolicyBoundary) ObserveApparelPolicy(ctx context.Context, p executor.Placement, current domain.GenerationSnapshot) (executor.ApparelPolicyEvidence, error) {
	out := executor.ApparelPolicyEvidence{StartedAt: b.Clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundary.World(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	value, ok := p.Action.ApparelPolicy()
	if !ok {
		return out, executor.ErrEvidence
	}
	w := b.apparelPolicyAttempt(p, value)
	lookup, _, err := b.apparelPolicy.Native.LookupSetApparelPolicy(ctx, w)
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
	reply, _, err := b.apparelPolicy.Native.ObserveSetApparelPolicyProgress(ctx, w, admitted)
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
	out.Pawn = value.Pawn()
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
