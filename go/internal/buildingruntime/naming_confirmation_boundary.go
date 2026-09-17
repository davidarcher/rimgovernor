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

// ConfirmColonyNamesNative and ConfirmColonyNamesWriter narrow *bridge.Client
// and *bridge.NamingControl to what confirmColonyNamesBoundary consumes, the
// same split ResearchSelect uses.
type ConfirmColonyNamesNative interface {
	PreviewConfirmColonyNames(context.Context, *c.Identity, int32, string, string) (*op.PreviewReply, bridge.Result, error)
	LookupConfirmColonyNames(context.Context, bridge.NamingAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveConfirmColonyNamesProgress(context.Context, bridge.NamingAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type ConfirmColonyNamesWriter interface {
	ConfirmColonyNames(context.Context, *a.WritePrecondition, int32, string, string) (*op.ExecuteReply, bridge.Result, error)
}
type ConfirmColonyNamesCapabilities struct {
	Native ConfirmColonyNamesNative
	Writer ConfirmColonyNamesWriter
}
type confirmColonyNamesBoundary struct {
	*boundary.Boundary
	naming ConfirmColonyNamesCapabilities
}

// InspectConfirmColonyNames re-runs the native validators against the exact
// targeted window/suggestions immediately before dispatch: unlike
// researchSelectBoundary's own separate native read, PreviewConfirmColonyNames
// alone both re-observes freshness (native refuses on any drift from the
// exact observed window/suggestions, see NativeColonyNamingOperations.cs's
// PrepareStale) and the validators' current verdict.
func (b *confirmColonyNamesBoundary) InspectConfirmColonyNames(ctx context.Context, target executor.Target) (executor.ConfirmColonyNamesInspection, error) {
	out := executor.ConfirmColonyNamesInspection{StartedAt: b.Clock.Now()}
	value, ok := target.Action.NamingConfirmation()
	if !ok {
		return out, executor.ErrEvidence
	}
	preview, _, err := b.naming.Native.PreviewConfirmColonyNames(ctx, boundary.Identity(target.Snapshot), value.WindowID(), value.FactionName(), value.SettlementName())
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
	out.Facts = policy.ConfirmColonyNamesFacts{Snapshot: current, Tick: domain.Tick(v.Context.GetTick()), Accepted: optionalBool(v.Accepted)}
	out.ObservedAt = b.Clock.Now()
	return out, nil
}
func (b *confirmColonyNamesBoundary) namingAttempt(p executor.Placement, windowID int32, factionName, settlementName string) bridge.NamingAttempt {
	return bridge.NamingAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: b.Attempt(p), Generation: uint64(p.Snapshot.Native), WindowID: windowID, FactionName: factionName, SettlementName: settlementName}
}
func (b *confirmColonyNamesBoundary) ConfirmColonyNames(ctx context.Context, d executor.ConfirmColonyNamesDispatch) (executor.Receipt, error) {
	p := d.Attempt
	_, ok := p.Action.NamingConfirmation()
	return b.DispatchWrite(ctx, p,
		func() error {
			if !ok {
				return executor.ErrEvidence
			}
			return nil
		},
		func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			return b.naming.Writer.ConfirmColonyNames(ctx, pre, d.WindowID, d.FactionName, d.SettlementName)
		},
	)
}
func (b *confirmColonyNamesBoundary) ObserveConfirmColonyNames(ctx context.Context, p executor.Placement, current domain.GenerationSnapshot) (executor.ConfirmColonyNamesEvidence, error) {
	out := executor.ConfirmColonyNamesEvidence{StartedAt: b.Clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundary.World(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	value, ok := p.Action.NamingConfirmation()
	if !ok {
		return out, executor.ErrEvidence
	}
	w := b.namingAttempt(p, value.WindowID(), value.FactionName(), value.SettlementName())
	lookup, _, err := b.naming.Native.LookupConfirmColonyNames(ctx, w)
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
	reply, _, err := b.naming.Native.ObserveConfirmColonyNamesProgress(ctx, w, admitted)
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
