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

// SettlementGiftNative reuses bridge.ReadSettlementGiftTarget for the
// caravan's self-computed CAS token and the settlement/faction's fresh
// native-tokened relation facts, the same as QuestAcceptNative: no dedicated
// candidate search is needed to dispatch one already-selected caravan
// visiting one already-arrived-at settlement.
type SettlementGiftNative interface {
	ReadSettlementGiftTarget(context.Context, *c.Identity, string) (bridge.SettlementGiftTarget, bridge.Result, error)
	PreviewSettlementGift(context.Context, *c.Identity, string, string, string, string, []string, int32) (*o.PreviewReply, bridge.Result, error)
	LookupSettlementGift(context.Context, bridge.SettlementGiftAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveSettlementGiftProgress(context.Context, bridge.SettlementGiftAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type SettlementGiftWriter interface {
	ApplySettlementGift(context.Context, *a.WritePrecondition, *a.Owner, string, string, string, string, []string, int32) (*o.ExecuteReply, bridge.Result, error)
}
type SettlementGiftCapabilities struct {
	Native SettlementGiftNative
	Writer SettlementGiftWriter
}
type SettlementGiftBoundary struct {
	native  SettlementGiftNative
	writer  SettlementGiftWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
}

func NewSettlementGiftBoundary(native SettlementGiftNative, writer SettlementGiftWriter, leases boundary.LeaseSource, clock executor.Clock, session string) (*SettlementGiftBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid settlement gift boundary dependencies")
	}
	return &SettlementGiftBoundary{native, writer, leases, clock, session}, nil
}

func (b *SettlementGiftBoundary) InspectSettlementGift(ctx context.Context, target executor.Target) (executor.SettlementGiftInspection, error) {
	out := executor.SettlementGiftInspection{StartedAt: b.clock.Now()}
	gift, ok := target.Action.SettlementGift()
	if !ok {
		return out, executor.ErrEvidence
	}
	read, _, err := b.native.ReadSettlementGiftTarget(ctx, boundary.Identity(target.Snapshot), string(gift.Caravan()))
	if err != nil {
		return out, err
	}
	if read.Caravan != string(gift.Caravan()) || read.Settlement != string(gift.Settlement()) || read.FactionID != string(gift.Faction()) {
		return out, executor.ErrEvidence
	}
	observedContext, err := boundary.Context(read.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	expectedPawnIDs := make([]string, 0, len(gift.CrewIDs()))
	for _, pawn := range gift.CrewIDs() {
		expectedPawnIDs = append(expectedPawnIDs, string(pawn))
	}
	preview, _, err := b.native.PreviewSettlementGift(ctx, boundary.Identity(observedContext), string(gift.Caravan()), read.CaravanToken, string(gift.Faction()), read.FactionToken, expectedPawnIDs, gift.Silver())
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
	effect := evaluated.GetProjected().GetTrade()
	if evaluated.Context.GetTick() < read.Context.GetTick() || evaluated.Accepted == nil || effect == nil || effect.GetFactionId() != string(gift.Faction()) {
		return out, executor.ErrEvidence
	}
	// read.Context is the single observation envelope both the caravan
	// journey census and the world census were read under (bridge composes
	// them into one target); there is no separate world-only tick to carry.
	tick := domain.Tick(read.Context.GetTick())
	facts := policy.SettlementGiftAdmissionFacts{Snapshot: observedContext, CaravanTick: tick, WorldTick: tick, PreviewTick: domain.Tick(evaluated.Context.GetTick()), NativeCanTry: domain.Known(evaluated.GetAccepted())}
	crew := make([]domain.PawnID, 0, len(read.CrewIDs))
	for _, pawn := range read.CrewIDs {
		crew = append(crew, domain.PawnID(pawn))
	}
	facts.Gift = policy.SettlementGiftFacts{
		Caravan: gift.Caravan(), CaravanToken: read.CaravanToken, Moving: domain.Known(read.CaravanMoving),
		CrewIDs: domain.Known(crew), Silver: domain.Known(read.Silver),
		Settlement: gift.Settlement(), AtTarget: domain.Known(true),
		Faction: gift.Faction(), FactionToken: read.FactionToken, Player: domain.Known(read.Player),
		Hostile: domain.Known(read.Relation == "Hostile"),
	}
	if read.GoodwillKnown {
		facts.Gift.Goodwill = domain.Known(read.Goodwill)
	}
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func (b *SettlementGiftBoundary) attempt(dispatch executor.SettlementGiftDispatch) (bridge.SettlementGiftAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	gift, ok := p.Action.SettlementGift()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Caravan != gift.Caravan() || admission.Settlement != gift.Settlement() ||
		admission.Faction != gift.Faction() || admission.Silver != gift.Silver() || admission.Tick > p.Tick ||
		!boundary.ValidID(admission.CaravanSnapshotToken) || !boundary.ValidID(admission.FactionSnapshotToken) {
		return bridge.SettlementGiftAttempt{}, executor.ErrEvidence
	}
	expected := gift.CrewIDs()
	if len(expected) != len(admission.CrewIDs) {
		return bridge.SettlementGiftAttempt{}, executor.ErrEvidence
	}
	for i := range expected {
		if expected[i] != admission.CrewIDs[i] {
			return bridge.SettlementGiftAttempt{}, executor.ErrEvidence
		}
	}
	expectedPawnIDs := make([]string, 0, len(expected))
	for _, pawn := range expected {
		expectedPawnIDs = append(expectedPawnIDs, string(pawn))
	}
	return bridge.SettlementGiftAttempt{
		Identity:        boundary.Identity(p.Snapshot),
		Attempt:         &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))},
		Owner:           &a.Owner{ControllerSessionId: proto.String(b.session), PlayerDirection: proto.Uint64(uint64(p.Snapshot.Direction))},
		Generation:      uint64(p.Snapshot.Native),
		Caravan:         string(gift.Caravan()),
		CaravanToken:    admission.CaravanSnapshotToken,
		Faction:         string(gift.Faction()),
		FactionToken:    admission.FactionSnapshotToken,
		ExpectedPawnIDs: expectedPawnIDs,
		Silver:          gift.Silver(),
	}, nil
}

func (b *SettlementGiftBoundary) WriteSettlementGift(ctx context.Context, dispatch executor.SettlementGiftDispatch) (executor.Receipt, error) {
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
	reply, _, err := b.writer.ApplySettlementGift(ctx, pre, attempt.Owner, attempt.Caravan, attempt.CaravanToken, attempt.Faction, attempt.FactionToken, attempt.ExpectedPawnIDs, attempt.Silver)
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

func (b *SettlementGiftBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.SettlementGiftDispatch) error {
	p := dispatch.Attempt
	if err := boundary.Admission(receipt, p, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return executor.ErrEvidence
	}
	return nil
}

func (b *SettlementGiftBoundary) ObserveSettlementGift(ctx context.Context, dispatch executor.SettlementGiftDispatch, current domain.GenerationSnapshot) (executor.SettlementGiftEvidence, error) {
	out := executor.SettlementGiftEvidence{StartedAt: b.clock.Now(), Caravan: dispatch.Admission.Caravan}
	attempt, err := b.attempt(dispatch)
	if err != nil {
		return out, err
	}
	p := dispatch.Attempt
	reply, _, err := b.native.LookupSettlementGift(ctx, attempt)
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
	progress, _, err := b.native.ObserveSettlementGiftProgress(ctx, attempt, nil)
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

var _ executor.SettlementGiftBoundary = (*SettlementGiftBoundary)(nil)
