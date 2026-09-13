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

// ResearchSelectNative and ResearchSelectWriter narrow *bridge.Client and
// *bridge.ResearchSelectControl to what researchSelectBoundary consumes, the
// same split GearReplace/Work/Acquisition use.
type ResearchSelectNative interface {
	ReadResearch(context.Context, *c.Identity) (bridge.ResearchRead, bridge.Result, error)
	PreviewResearchSelect(context.Context, *c.Identity, string, string) (*op.PreviewReply, bridge.Result, error)
	LookupResearchSelect(context.Context, bridge.ResearchSelectAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveResearchSelectProgress(context.Context, bridge.ResearchSelectAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type ResearchSelectWriter interface {
	SelectResearch(context.Context, *a.WritePrecondition, string, string) (*op.ExecuteReply, bridge.Result, error)
}
type ResearchSelectCapabilities struct {
	Native ResearchSelectNative
	Writer ResearchSelectWriter
}
type researchSelectBoundary struct {
	*Boundary
	research ResearchSelectCapabilities
}

// InspectResearchSelect reads the native research snapshot's fresh CAS token
// and currently-selected project (if any), the same shape acquisitionBoundary
// uses for its own CAS-token target: no held-map/emergency review is needed
// here since EnsureResearch dispatches at most one immediate write per
// routine step. See ResearchRead's doc comment for the disclosed Hidden-field
// approximation this inspection inherits.
func (b *researchSelectBoundary) InspectResearchSelect(ctx context.Context, target executor.Target) (executor.ResearchSelectInspection, error) {
	out := executor.ResearchSelectInspection{StartedAt: b.clock.Now()}
	value, ok := target.Action.ResearchSelect()
	if !ok {
		return out, executor.ErrEvidence
	}
	read, _, err := b.research.Native.ReadResearch(ctx, boundaryIdentity(target.Snapshot))
	if err != nil {
		return out, err
	}
	current, err := boundaryContext(read.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	preview, _, err := b.research.Native.PreviewResearchSelect(ctx, boundaryIdentity(current), value.Project(), read.SnapshotToken)
	if err != nil {
		return out, err
	}
	v := preview.GetEvaluated()
	if v == nil || !v.GetAccepted() || v.Projected != nil {
		return out, executor.ErrHeld
	}
	if _, err = boundaryContext(v.Context, current); err != nil {
		return out, err
	}
	if read.Context.GetTick() != v.Context.GetTick() {
		return out, executor.ErrHeld
	}
	out.Facts = policy.ResearchSelectFacts{Snapshot: current, Tick: domain.Tick(v.Context.GetTick()), Current: domain.Known(read.CurrentProject)}
	out.Token = read.SnapshotToken
	out.ObservedAt = b.clock.Now()
	return out, nil
}
func (b *researchSelectBoundary) researchAttempt(p executor.Placement, project string) bridge.ResearchSelectAttempt {
	return bridge.ResearchSelectAttempt{Identity: boundaryIdentity(p.Snapshot), Attempt: b.attempt(p), Owner: &a.Owner{ControllerSessionId: proto.String(b.session), PlayerDirection: proto.Uint64(uint64(p.Snapshot.Direction))}, Generation: uint64(p.Snapshot.Native), Project: project, Token: project}
}
func (b *researchSelectBoundary) SelectResearch(ctx context.Context, d executor.ResearchSelectDispatch) (executor.Receipt, error) {
	p := d.Attempt
	_, ok := p.Action.ResearchSelect()
	return b.dispatchWrite(ctx, p,
		func() error {
			if !ok {
				return executor.ErrEvidence
			}
			return nil
		},
		func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			return b.research.Writer.SelectResearch(ctx, pre, d.Project, d.Token)
		},
	)
}
func (b *researchSelectBoundary) ObserveResearchSelect(ctx context.Context, p executor.Placement, current domain.GenerationSnapshot) (executor.ResearchSelectEvidence, error) {
	out := executor.ResearchSelectEvidence{StartedAt: b.clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundaryWorld(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	value, ok := p.Action.ResearchSelect()
	if !ok {
		return out, executor.ErrEvidence
	}
	w := b.researchAttempt(p, value.Project())
	lookup, _, err := b.research.Native.LookupResearchSelect(ctx, w)
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
	reply, _, err := b.research.Native.ObserveResearchSelectProgress(ctx, w, admitted)
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
	out.Project = value.Project()
	switch effect := v.Effect.(type) {
	case *r.Progress_Completed:
		if !v.GetCompleteInspection() {
			return out, executor.ErrEvidence
		}
		if err := bridge.ValidateResearchSelectEffect(effect.Completed.GetEvidence(), value.Project(), true); err != nil {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect = domain.EffectCompleted
		out.Complete, out.Matches = true, domain.Known(true)
	case *r.Progress_Unsuccessful:
		if !v.GetCompleteInspection() || effect.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return out, executor.ErrEvidence
		}
		if err := bridge.ValidateResearchSelectEffect(effect.Unsuccessful.GetEvidence(), value.Project(), false); err != nil {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect = domain.EffectUnsuccessful
		out.Observation.UnsuccessfulReason = domain.OutcomeNotAchieved
		out.Complete, out.Matches = true, domain.Known(false)
	case *r.Progress_Unknown:
	default:
		return out, executor.ErrEvidence
	}
	out.ObservedAt = b.clock.Now()
	return out, nil
}
