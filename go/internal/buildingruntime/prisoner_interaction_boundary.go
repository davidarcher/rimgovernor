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

func prisonerInteractionModeWire(mode domain.PrisonerInteractionMode) bridge.PrisonerInteractionMode {
	switch mode {
	case domain.PrisonerInteractionRecruit:
		return bridge.PrisonerInteractionRecruit
	case domain.PrisonerInteractionMaintain:
		return bridge.PrisonerInteractionMaintain
	default:
		return bridge.PrisonerInteractionModeUnspecified
	}
}

func prisonerInteractionModeDomain(mode bridge.PrisonerInteractionMode) (domain.PrisonerInteractionMode, bool) {
	switch mode {
	case bridge.PrisonerInteractionRecruit:
		return domain.PrisonerInteractionRecruit, true
	case bridge.PrisonerInteractionMaintain:
		return domain.PrisonerInteractionMaintain, true
	default:
		return "", false
	}
}

// PrisonerInteractionNative reuses bridge.ReadPrisonerInteractionTarget for
// the prisoner's fresh settings-snapshot CAS token and eligibility facts, the
// same as HusbandryNative: no dedicated candidate search is needed to
// dispatch one already-selected prisoner/interaction pair.
type PrisonerInteractionNative interface {
	ReadPrisonerInteractionTarget(context.Context, *c.Identity, string) (bridge.PrisonerTarget, bridge.Result, error)
	PreviewPrisonerInteraction(context.Context, *c.Identity, string, string, bridge.PrisonerInteractionMode) (*o.PreviewReply, bridge.Result, error)
	LookupPrisonerInteraction(context.Context, bridge.PrisonerInteractionAttempt) (*r.LookupReply, bridge.Result, error)
	ObservePrisonerInteractionProgress(context.Context, bridge.PrisonerInteractionAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type PrisonerInteractionWriter interface {
	ApplyPrisonerInteraction(context.Context, *a.WritePrecondition, string, string, bridge.PrisonerInteractionMode) (*o.ExecuteReply, bridge.Result, error)
}
type PrisonerInteractionCapabilities struct {
	Native PrisonerInteractionNative
	Writer PrisonerInteractionWriter
}
type PrisonerInteractionBoundary struct {
	native  PrisonerInteractionNative
	writer  PrisonerInteractionWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
}

func NewPrisonerInteractionBoundary(native PrisonerInteractionNative, writer PrisonerInteractionWriter, leases boundary.LeaseSource, clock executor.Clock, session string) (*PrisonerInteractionBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid prisoner interaction boundary dependencies")
	}
	return &PrisonerInteractionBoundary{native, writer, leases, clock, session}, nil
}

func (b *PrisonerInteractionBoundary) InspectPrisonerInteraction(ctx context.Context, target executor.Target) (executor.PrisonerInteractionInspection, error) {
	out := executor.PrisonerInteractionInspection{StartedAt: b.clock.Now()}
	interaction, ok := target.Action.PrisonerInteraction()
	if !ok {
		return out, executor.ErrEvidence
	}
	read, _, err := b.native.ReadPrisonerInteractionTarget(ctx, boundary.Identity(target.Snapshot), string(interaction.Pawn()))
	if err != nil {
		return out, err
	}
	if read.Pawn != string(interaction.Pawn()) {
		return out, executor.ErrEvidence
	}
	observedContext, err := boundary.Context(read.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	preview, _, err := b.native.PreviewPrisonerInteraction(ctx, boundary.Identity(observedContext), string(interaction.Pawn()), read.SnapshotToken, prisonerInteractionModeWire(interaction.Interaction()))
	if err != nil {
		return out, err
	}
	evaluated := preview.GetEvaluated()
	if evaluated == nil {
		return out, executor.ErrHeld
	}
	if _, err = boundary.Context(evaluated.Context, observedContext); err != nil {
		return out, err
	}
	effect := evaluated.GetProjected().GetPrisoner()
	if evaluated.Context.GetTick() < read.Context.GetTick() || evaluated.Accepted == nil || effect == nil || effect.Pawn.GetEntityId() != string(interaction.Pawn()) {
		return out, executor.ErrEvidence
	}
	facts := policy.PrisonerInteractionFacts{Snapshot: observedContext, PawnTick: domain.Tick(read.Context.GetTick()), PreviewTick: domain.Tick(evaluated.Context.GetTick()), NativeCanTry: domain.Known(evaluated.GetAccepted())}
	facts.Pawn = policy.PrisonerFacts{Pawn: interaction.Pawn(), SnapshotToken: read.SnapshotToken}
	if read.DeadKnown {
		facts.Pawn.Dead = domain.Known(read.Dead)
	}
	if read.PrisonerKnown {
		facts.Pawn.Prisoner = domain.Known(read.Prisoner)
	}
	if read.RecruitableKnown {
		facts.Pawn.Recruitable = domain.Known(read.Recruitable)
	}
	if read.CurrentInteractionKnown {
		if mode, ok := prisonerInteractionModeDomain(read.CurrentInteraction); ok {
			facts.Pawn.CurrentInteraction = domain.Known(mode)
		}
	}
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func (b *PrisonerInteractionBoundary) attempt(dispatch executor.PrisonerInteractionDispatch) (bridge.PrisonerInteractionAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	interaction, ok := p.Action.PrisonerInteraction()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Pawn != interaction.Pawn() || admission.Interaction != interaction.Interaction() || admission.Tick > p.Tick || !boundary.ValidID(admission.PawnSnapshotToken) {
		return bridge.PrisonerInteractionAttempt{}, executor.ErrEvidence
	}
	return bridge.PrisonerInteractionAttempt{
		Identity:    boundary.Identity(p.Snapshot),
		Attempt:     &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))},
		Generation:  uint64(p.Snapshot.Native),
		Pawn:        string(interaction.Pawn()),
		PawnToken:   admission.PawnSnapshotToken,
		Interaction: prisonerInteractionModeWire(interaction.Interaction()),
	}, nil
}

func (b *PrisonerInteractionBoundary) WritePrisonerInteraction(ctx context.Context, dispatch executor.PrisonerInteractionDispatch) (executor.Receipt, error) {
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
	pre := &a.WritePrecondition{Identity: attempt.Identity, Attempt: attempt.Attempt, ExpectedGeneration: proto.Uint64(attempt.Generation)}
	reply, _, err := b.writer.ApplyPrisonerInteraction(ctx, pre, attempt.Pawn, attempt.PawnToken, attempt.Interaction)
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

func (b *PrisonerInteractionBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.PrisonerInteractionDispatch) error {
	p := dispatch.Attempt
	if err := boundary.Admission(receipt, p, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return executor.ErrEvidence
	}
	return nil
}

func (b *PrisonerInteractionBoundary) ObservePrisonerInteraction(ctx context.Context, dispatch executor.PrisonerInteractionDispatch, current domain.GenerationSnapshot) (executor.PrisonerInteractionEvidence, error) {
	out := executor.PrisonerInteractionEvidence{StartedAt: b.clock.Now(), Pawn: dispatch.Admission.Pawn}
	attempt, err := b.attempt(dispatch)
	if err != nil {
		return out, err
	}
	p := dispatch.Attempt
	reply, _, err := b.native.LookupPrisonerInteraction(ctx, attempt)
	if err != nil {
		return out, err
	}
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
	progress, _, err := b.native.ObservePrisonerInteractionProgress(ctx, attempt, nil)
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
	switch outcome := prog.Effect.(type) {
	case *r.Progress_Unknown:
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectUnknown, Causality: domain.AfterDispatch}
		return out, ctx.Err()
	case *r.Progress_Completed:
		if !prog.GetCompleteInspection() || outcome.Completed == nil {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}
		out.Complete = true
		return out, ctx.Err()
	case *r.Progress_Unsuccessful:
		if !prog.GetCompleteInspection() || outcome.Unsuccessful == nil {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectUnsuccessful, UnsuccessfulReason: domain.NativeFailure, Causality: domain.AfterDispatch}
		out.Complete = true
		return out, ctx.Err()
	default:
		return out, executor.ErrEvidence
	}
}

var _ executor.PrisonerInteractionBoundary = (*PrisonerInteractionBoundary)(nil)
