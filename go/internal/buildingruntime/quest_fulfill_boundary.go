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

// QuestFulfillNative reuses bridge.ReadQuestFulfillTarget for the quest's
// fresh CAS token/trade-request destination and the caravan's self-computed
// CAS token/position/crew, the same as SettlementGiftNative: no dedicated
// candidate search is needed to dispatch one already-selected quest/caravan
// pair.
type QuestFulfillNative interface {
	ReadQuestFulfillTarget(context.Context, *c.Identity, string, string) (bridge.QuestFulfillTarget, bridge.Result, error)
	PreviewQuestFulfill(context.Context, *c.Identity, string, string, string, string, []string) (*o.PreviewReply, bridge.Result, error)
	LookupQuestFulfill(context.Context, bridge.QuestFulfillAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveQuestFulfillProgress(context.Context, bridge.QuestFulfillAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type QuestFulfillWriter interface {
	ApplyQuestFulfill(context.Context, *a.WritePrecondition, *a.Owner, string, string, string, string, []string) (*o.ExecuteReply, bridge.Result, error)
}
type QuestFulfillCapabilities struct {
	Native QuestFulfillNative
	Writer QuestFulfillWriter
}
type QuestFulfillBoundary struct {
	native  QuestFulfillNative
	writer  QuestFulfillWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
}

func NewQuestFulfillBoundary(native QuestFulfillNative, writer QuestFulfillWriter, leases boundary.LeaseSource, clock executor.Clock, session string) (*QuestFulfillBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid quest fulfill boundary dependencies")
	}
	return &QuestFulfillBoundary{native, writer, leases, clock, session}, nil
}

func (b *QuestFulfillBoundary) InspectQuestFulfill(ctx context.Context, target executor.Target) (executor.QuestFulfillInspection, error) {
	out := executor.QuestFulfillInspection{StartedAt: b.clock.Now()}
	fulfill, ok := target.Action.QuestFulfill()
	if !ok {
		return out, executor.ErrEvidence
	}
	read, _, err := b.native.ReadQuestFulfillTarget(ctx, boundary.Identity(target.Snapshot), string(fulfill.Quest()), string(fulfill.Caravan()))
	if err != nil {
		return out, err
	}
	if read.Quest != string(fulfill.Quest()) || read.Caravan != string(fulfill.Caravan()) {
		return out, executor.ErrEvidence
	}
	observedContext, err := boundary.Context(read.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	expectedPawnIDs := make([]string, 0, len(fulfill.CrewIDs()))
	for _, pawn := range fulfill.CrewIDs() {
		expectedPawnIDs = append(expectedPawnIDs, string(pawn))
	}
	preview, _, err := b.native.PreviewQuestFulfill(ctx, boundary.Identity(observedContext), string(fulfill.Quest()), read.QuestToken, string(fulfill.Caravan()), read.CaravanToken, expectedPawnIDs)
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
	if evaluated.Context.GetTick() < read.Context.GetTick() || evaluated.Accepted == nil || effect == nil || effect.GetQuestId() != string(fulfill.Quest()) {
		return out, executor.ErrEvidence
	}
	tick := domain.Tick(read.Context.GetTick())
	facts := policy.QuestFulfillAdmissionFacts{Snapshot: observedContext, QuestTick: tick, CaravanTick: tick, PreviewTick: domain.Tick(evaluated.Context.GetTick()), NativeCanTry: domain.Known(evaluated.GetAccepted())}
	crew := make([]domain.PawnID, 0, len(read.CrewIDs))
	for _, pawn := range read.CrewIDs {
		crew = append(crew, domain.PawnID(pawn))
	}
	facts.Fulfill = policy.QuestFulfillFacts{
		Quest: fulfill.Quest(), QuestToken: read.QuestToken, State: domain.Known(read.State), HasTradeRequest: domain.Known(read.HasTradeRequest),
		Caravan: fulfill.Caravan(), CaravanToken: read.CaravanToken, Moving: domain.Known(read.CaravanMoving), CrewIDs: domain.Known(crew),
		AtTarget: domain.Known(read.AtTarget),
	}
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func (b *QuestFulfillBoundary) attempt(dispatch executor.QuestFulfillDispatch) (bridge.QuestFulfillAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	fulfill, ok := p.Action.QuestFulfill()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Quest != fulfill.Quest() || admission.Caravan != fulfill.Caravan() ||
		admission.Tick > p.Tick || !boundary.ValidID(admission.QuestSnapshotToken) || !boundary.ValidID(admission.CaravanSnapshotToken) {
		return bridge.QuestFulfillAttempt{}, executor.ErrEvidence
	}
	expected := fulfill.CrewIDs()
	if len(expected) != len(admission.CrewIDs) {
		return bridge.QuestFulfillAttempt{}, executor.ErrEvidence
	}
	for i := range expected {
		if expected[i] != admission.CrewIDs[i] {
			return bridge.QuestFulfillAttempt{}, executor.ErrEvidence
		}
	}
	expectedPawnIDs := make([]string, 0, len(expected))
	for _, pawn := range expected {
		expectedPawnIDs = append(expectedPawnIDs, string(pawn))
	}
	return bridge.QuestFulfillAttempt{
		Identity:        boundary.Identity(p.Snapshot),
		Attempt:         &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))},
		Owner:           &a.Owner{ControllerSessionId: proto.String(b.session), PlayerDirection: proto.Uint64(uint64(p.Snapshot.Direction))},
		Generation:      uint64(p.Snapshot.Native),
		Quest:           string(fulfill.Quest()),
		QuestToken:      admission.QuestSnapshotToken,
		Caravan:         string(fulfill.Caravan()),
		CaravanToken:    admission.CaravanSnapshotToken,
		ExpectedPawnIDs: expectedPawnIDs,
	}, nil
}

func (b *QuestFulfillBoundary) WriteQuestFulfill(ctx context.Context, dispatch executor.QuestFulfillDispatch) (executor.Receipt, error) {
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
	pre := &a.WritePrecondition{Identity: attempt.Identity, Attempt: attempt.Attempt, ExpectedGeneration: proto.Uint64(attempt.Generation), LeaseId: proto.String(lease)}
	reply, _, err := b.writer.ApplyQuestFulfill(ctx, pre, attempt.Owner, attempt.Quest, attempt.QuestToken, attempt.Caravan, attempt.CaravanToken, attempt.ExpectedPawnIDs)
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

func (b *QuestFulfillBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.QuestFulfillDispatch) error {
	p := dispatch.Attempt
	if err := boundary.Admission(receipt, p, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return executor.ErrEvidence
	}
	return nil
}

func (b *QuestFulfillBoundary) ObserveQuestFulfill(ctx context.Context, dispatch executor.QuestFulfillDispatch, current domain.GenerationSnapshot) (executor.QuestFulfillEvidence, error) {
	out := executor.QuestFulfillEvidence{StartedAt: b.clock.Now(), Quest: dispatch.Admission.Quest}
	attempt, err := b.attempt(dispatch)
	if err != nil {
		return out, err
	}
	p := dispatch.Attempt
	reply, _, err := b.native.LookupQuestFulfill(ctx, attempt)
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
		if err = boundary.Admission(&r.Receipt{Attempt: v.InFlight.Attempt, AdmittedContext: v.InFlight.AdmittedContext, AuthorizingOwner: attempt.Owner}, p, b.session); err != nil {
			return out, err
		}
	case *r.LookupReply_Receipt:
		if err = b.checkReceipt(v.Receipt, dispatch); err != nil {
			return out, err
		}
	default:
		return out, executor.ErrEvidence
	}
	progress, _, err := b.native.ObserveQuestFulfillProgress(ctx, attempt, nil)
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

var _ executor.QuestFulfillBoundary = (*QuestFulfillBoundary)(nil)
