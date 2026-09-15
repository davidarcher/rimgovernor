package buildingruntime

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
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// HomeCoverageNative reuses bridge.ReadHomeCoverageTarget for a fresh shape
// token/revision/deficit read: unlike BedAssign, Home coverage has no exact
// per-entity CAS lookup RPC, so refreshing before dispatch means re-reading
// the same general upkeep census the routine review pass already consumes.
type HomeCoverageNative interface {
	ReadHomeCoverageTarget(context.Context, *c.Identity, string) (bridge.HomeCoverageTarget, bridge.Result, error)
	PreviewHomeCoverage(context.Context, *c.Identity, string, string, int64) (*o.PreviewReply, bridge.Result, error)
	LookupHomeCoverage(context.Context, bridge.HomeCoverageAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveHomeCoverageProgress(context.Context, bridge.HomeCoverageAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type HomeCoverageWriter interface {
	ApplyHomeCoverage(context.Context, *a.WritePrecondition, string, string, int64) (*o.ExecuteReply, bridge.Result, error)
}
type HomeCoverageCapabilities struct {
	Native HomeCoverageNative
	Writer HomeCoverageWriter
}
type HomeCoverageBoundary struct {
	native  HomeCoverageNative
	writer  HomeCoverageWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
}

func NewHomeCoverageBoundary(native HomeCoverageNative, writer HomeCoverageWriter, leases boundary.LeaseSource, clock executor.Clock, session string) (*HomeCoverageBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid home coverage boundary dependencies")
	}
	return &HomeCoverageBoundary{native, writer, leases, clock, session}, nil
}

func (b *HomeCoverageBoundary) InspectHomeCoverage(ctx context.Context, target executor.Target) (executor.HomeCoverageInspection, error) {
	out := executor.HomeCoverageInspection{StartedAt: b.clock.Now()}
	coverage, ok := target.Action.HomeCoverage()
	if !ok {
		return out, executor.ErrEvidence
	}
	row, _, err := b.native.ReadHomeCoverageTarget(ctx, boundary.Identity(target.Snapshot), coverage.Target())
	if err != nil {
		return out, err
	}
	current, err := boundary.Context(row.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	preview, _, err := b.native.PreviewHomeCoverage(ctx, boundary.Identity(current), coverage.Target(), row.Shape, row.Revision)
	if err != nil {
		return out, err
	}
	evaluated := preview.GetEvaluated()
	if evaluated == nil {
		return out, executor.ErrHeld
	}
	if _, err = boundary.Context(evaluated.Context, current); err != nil {
		return out, err
	}
	effect := evaluated.GetProjected().GetHome()
	if evaluated.Context.GetTick() < row.Context.GetTick() || evaluated.Accepted == nil || effect == nil || effect.Snapshot.GetEntityId() != coverage.Target() || effect.GetShapeToken() != row.Shape {
		return out, executor.ErrEvidence
	}
	facts := policy.HomeCoverageFacts{
		Snapshot:         current,
		ObservationTick:  domain.Tick(row.Context.GetTick()),
		PreviewTick:      domain.Tick(evaluated.Context.GetTick()),
		CurrentShape:     domain.Known(row.Shape),
		Revision:         domain.Known(row.Revision),
		Missing:          domain.Known(row.Missing),
		Excluded:         domain.Known(row.Excluded),
		NativeCanTry:     boundary.FactBool(evaluated.Accepted),
	}
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func (b *HomeCoverageBoundary) attempt(dispatch executor.HomeCoverageDispatch) (bridge.HomeCoverageAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	coverage, ok := p.Action.HomeCoverage()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Target != coverage.Target() || admission.Shape != coverage.Shape() || admission.Tick > p.Tick {
		return bridge.HomeCoverageAttempt{}, executor.ErrEvidence
	}
	return bridge.HomeCoverageAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))}, Generation: uint64(p.Snapshot.Native), Target: coverage.Target(), Shape: coverage.Shape(), Revision: admission.Revision}, nil
}

func (b *HomeCoverageBoundary) ExtendHomeCoverage(ctx context.Context, dispatch executor.HomeCoverageDispatch) (executor.Receipt, error) {
	p := dispatch.Attempt
	out := executor.Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptUnknown}
	attempt, err := b.attempt(dispatch)
	if err != nil {
		return out, err
	}
	lease, err := b.leases.Lease(p.Snapshot)
	if err != nil {
		return out, err
	}
	if !boundary.ValidID(lease) {
		return out, executor.ErrAuthority
	}
	if err = ctx.Err(); err != nil {
		return out, err
	}
	pre := &a.WritePrecondition{Identity: attempt.Identity, Attempt: attempt.Attempt, ExpectedGeneration: proto.Uint64(attempt.Generation),}
	reply, _, err := b.writer.ApplyHomeCoverage(ctx, pre, attempt.Target, attempt.Shape, attempt.Revision)
	var refused *bridge.NativeFailure
	if errors.As(err, &refused) && refused.Value != nil && refused.Value.GetCode() != c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT {
		out.Kind = domain.ReceiptRefused
		return out, nil
	}
	if err != nil {
		return out, err
	}
	receipt := reply.GetReceipt()
	if err = b.checkReceipt(receipt, dispatch); err != nil {
		return out, err
	}
	switch receipt.Outcome.(type) {
	case *r.Receipt_Applied:
		out.Kind = domain.ReceiptAccepted
	case *r.Receipt_Uncertain:
	default:
		return out, executor.ErrEvidence
	}
	return out, ctx.Err()
}

func (b *HomeCoverageBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.HomeCoverageDispatch) error {
	p := dispatch.Attempt
	if err := boundary.Admission(receipt, p, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return executor.ErrEvidence
	}
	return nil
}

func (b *HomeCoverageBoundary) ObserveHomeCoverage(ctx context.Context, dispatch executor.HomeCoverageDispatch, current domain.GenerationSnapshot) (executor.HomeCoverageEvidence, error) {
	out := executor.HomeCoverageEvidence{StartedAt: b.clock.Now()}
	attempt, err := b.attempt(dispatch)
	if err != nil {
		return out, err
	}
	reply, _, err := b.native.LookupHomeCoverage(ctx, attempt)
	if err != nil {
		return out, err
	}
	p := dispatch.Attempt
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Unknown:
		if _, err = boundary.Context(v.Unknown.GetContext(), current); err != nil {
			return out, err
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: domain.Tick(v.Unknown.GetContext().GetTick()), Effect: domain.EffectUnknown, Causality: domain.AfterDispatch}
		out.ObservedAt = b.clock.Now()
		return out, ctx.Err()
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, attempt.Attempt) {
			return out, executor.ErrEvidence
		}
		if err = boundary.Admission(&r.Receipt{Attempt: v.InFlight.Attempt, AdmittedContext: v.InFlight.AdmittedContext}, p, b.session); err != nil {
			return out, err
		}
	case *r.LookupReply_Receipt:
		if err = b.checkReceipt(v.Receipt, dispatch); err != nil {
			return out, err
		}
	default:
		return out, executor.ErrEvidence
	}
	progress, _, err := b.native.ObserveHomeCoverageProgress(ctx, attempt, nil)
	if err != nil {
		return out, err
	}
	prog := progress.GetProgress()
	if prog == nil {
		return out, executor.ErrHeld
	}
	if !proto.Equal(prog.Attempt, attempt.Attempt) {
		return out, executor.ErrEvidence
	}
	tickCtx, err := boundary.Context(prog.Context, current)
	if err != nil {
		return out, err
	}
	tick := domain.Tick(prog.Context.GetTick())
	out.ObservedAt = b.clock.Now()
	coverage := mustHomeCoverage(dispatch)
	switch outcome := prog.Effect.(type) {
	case *r.Progress_Unknown:
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectUnknown, Causality: domain.AfterDispatch}
		return out, ctx.Err()
	case *r.Progress_Pending:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectPending, Causality: domain.AfterDispatch}
		out.Target = coverage.Target()
		return out, ctx.Err()
	case *r.Progress_Completed:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}
		out.Complete, out.Target = true, coverage.Target()
		return out, ctx.Err()
	case *r.Progress_Absent:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectAbsent, Causality: domain.AfterDispatch}
		out.Complete, out.Target = true, coverage.Target()
		return out, ctx.Err()
	case *r.Progress_Unsuccessful:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectUnsuccessful, UnsuccessfulReason: domain.NativeFailure, Causality: domain.AfterDispatch}
		out.Complete, out.Target = true, coverage.Target()
		return out, ctx.Err()
	default:
		_ = outcome
		return out, executor.ErrEvidence
	}
}

func mustHomeCoverage(dispatch executor.HomeCoverageDispatch) domain.HomeCoverage {
	coverage, _ := dispatch.Attempt.Action.HomeCoverage()
	return coverage
}

var _ executor.HomeCoverageBoundary = (*HomeCoverageBoundary)(nil)
