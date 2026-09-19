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

// QuestAcceptNative reuses bridge.ReadQuestAcceptTarget for the quest's fresh
// CAS token and eligibility facts, the same as PrisonerInteractionNative: no
// dedicated candidate search is needed to dispatch one already-selected
// quest/accepter/reward-choice tuple.
type QuestAcceptNative interface {
	ReadQuestAcceptTarget(context.Context, *c.Identity, string) (bridge.QuestTarget, bridge.Result, error)
	PreviewQuestAccept(context.Context, *c.Identity, string, string, string, int32) (*o.PreviewReply, bridge.Result, error)
	LookupQuestAccept(context.Context, bridge.QuestAcceptAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveQuestAcceptProgress(context.Context, bridge.QuestAcceptAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type QuestAcceptWriter interface {
	ApplyQuestAccept(context.Context, *a.WritePrecondition, string, string, string, int32) (*o.ExecuteReply, bridge.Result, error)
}
type QuestAcceptCapabilities struct {
	Native QuestAcceptNative
	Writer QuestAcceptWriter
}
type QuestAcceptBoundary struct {
	native  QuestAcceptNative
	writer  QuestAcceptWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
}

func NewQuestAcceptBoundary(native QuestAcceptNative, writer QuestAcceptWriter, leases boundary.LeaseSource, clock executor.Clock, session string) (*QuestAcceptBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid quest accept boundary dependencies")
	}
	return &QuestAcceptBoundary{native, writer, leases, clock, session}, nil
}

func (b *QuestAcceptBoundary) InspectQuestAccept(ctx context.Context, target executor.Target) (executor.QuestAcceptInspection, error) {
	out := executor.QuestAcceptInspection{StartedAt: b.clock.Now()}
	accept, ok := target.Action.QuestAccept()
	if !ok {
		return out, executor.ErrEvidence
	}
	read, _, err := b.native.ReadQuestAcceptTarget(ctx, boundary.Identity(target.Snapshot), string(accept.Quest()))
	if err != nil {
		return out, err
	}
	if read.Quest != string(accept.Quest()) {
		return out, executor.ErrEvidence
	}
	observedContext, err := boundary.Context(read.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	accepterPawn := string(accept.AccepterPawn())
	preview, _, err := b.native.PreviewQuestAccept(ctx, boundary.Identity(observedContext), string(accept.Quest()), read.SnapshotToken, accepterPawn, accept.RewardChoice())
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
	effect := evaluated.GetProjected().GetQuest()
	if evaluated.Context.GetTick() < read.Context.GetTick() || evaluated.Accepted == nil || effect == nil || effect.GetQuestId() != string(accept.Quest()) {
		return out, executor.ErrEvidence
	}
	facts := policy.QuestAcceptFacts{Snapshot: observedContext, QuestTick: domain.Tick(read.Context.GetTick()), PreviewTick: domain.Tick(evaluated.Context.GetTick()), NativeCanTry: domain.Known(evaluated.GetAccepted())}
	facts.Quest = policy.QuestFacts{
		Quest: accept.Quest(), SnapshotToken: read.SnapshotToken,
		State: domain.Known(read.State), RequiresAccepter: domain.Known(read.RequiresAccepter), CanAccept: domain.Known(read.CanAccept),
		ChoiceCount: domain.Known(read.ChoiceCount), HasTradeRequest: domain.Known(read.HasTradeRequest),
	}
	for _, pawn := range read.EligiblePawnIDs {
		facts.Quest.EligibleAccepters = append(facts.Quest.EligibleAccepters, domain.PawnID(pawn))
	}
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func (b *QuestAcceptBoundary) attempt(dispatch executor.QuestAcceptDispatch) (bridge.QuestAcceptAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	accept, ok := p.Action.QuestAccept()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Quest != accept.Quest() || admission.AccepterPawn != accept.AccepterPawn() || admission.RewardChoice != accept.RewardChoice() || admission.Tick > p.Tick || !boundary.ValidID(admission.QuestSnapshotToken) {
		return bridge.QuestAcceptAttempt{}, executor.ErrEvidence
	}
	return bridge.QuestAcceptAttempt{
		Identity:     boundary.Identity(p.Snapshot),
		Attempt:      &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))},
		Generation:   uint64(p.Snapshot.Native),
		Quest:        string(accept.Quest()),
		QuestToken:   admission.QuestSnapshotToken,
		AccepterPawn: string(accept.AccepterPawn()),
		RewardChoice: accept.RewardChoice(),
	}, nil
}

func (b *QuestAcceptBoundary) WriteQuestAccept(ctx context.Context, dispatch executor.QuestAcceptDispatch) (executor.Receipt, error) {
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
	reply, _, err := b.writer.ApplyQuestAccept(ctx, pre, attempt.Quest, attempt.QuestToken, attempt.AccepterPawn, attempt.RewardChoice)
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

func (b *QuestAcceptBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.QuestAcceptDispatch) error {
	p := dispatch.Attempt
	if err := boundary.Admission(receipt, p, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return executor.ErrEvidence
	}
	return nil
}

func (b *QuestAcceptBoundary) ObserveQuestAccept(ctx context.Context, dispatch executor.QuestAcceptDispatch, current domain.GenerationSnapshot) (executor.QuestAcceptEvidence, error) {
	out := executor.QuestAcceptEvidence{StartedAt: b.clock.Now(), Quest: dispatch.Admission.Quest}
	attempt, err := b.attempt(dispatch)
	if err != nil {
		return out, err
	}
	p := dispatch.Attempt
	reply, _, err := b.native.LookupQuestAccept(ctx, attempt)
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
	progress, _, err := b.native.ObserveQuestAcceptProgress(ctx, attempt, nil)
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

var _ executor.QuestAcceptBoundary = (*QuestAcceptBoundary)(nil)
