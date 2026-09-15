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
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// tradeWorldRadius reads the entire world settlement census (bridge.ReadWorld's
// radius is measured in world-map tiles; a real world map never spans this
// far), since Trade's already-selected trader is identified by id, not by a
// caller-supplied tile the way a caravan-anchored read (e.g.
// bridge.ReadSettlementGiftTarget) can use. There is no dedicated Trade
// world read; this is the same ReadWorld every settlement-facing boundary
// already calls, just without a caravan position to scope it.
const tradeWorldRadius = 1e9

// TradeNative composes the generic bridge.Client reads Trade needs
// (settlements for the trader's fresh token/relation, pawns for the
// negotiator's fresh token) with bridge/trade.go's own Preview/Lookup/
// Observe methods for each of the four sub-operations. There is no
// dedicated bridge.ReadTradeTarget: unlike the caravan- or quest-anchored
// verticals, Trade has no single composed read to reuse (see
// bridge/trade.go's package doc), so this boundary composes the generic
// reads itself.
type TradeNative interface {
	ReadWorld(context.Context, *c.Identity, int32, float64) (bridge.WorldRead, bridge.Result, error)
	ReadPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	PreviewOpenTrade(context.Context, *c.Identity, string, string, string, string, bool) (*o.PreviewReply, bridge.Result, error)
	LookupTradeOpen(context.Context, bridge.TradeOpenAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveTradeOpenProgress(context.Context, bridge.TradeOpenAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
	PreviewSetTradeLines(context.Context, *c.Identity, string, string, []bridge.TradeLineInput, bool) (*o.PreviewReply, bridge.Result, error)
	LookupTradeSetLines(context.Context, bridge.TradeSetLinesAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveTradeSetLinesProgress(context.Context, bridge.TradeSetLinesAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
	PreviewAcceptTrade(context.Context, *c.Identity, string, string, string, []bridge.TradeEconomicFloor, bool, bool) (*o.PreviewReply, bridge.Result, error)
	LookupTradeAccept(context.Context, bridge.TradeAcceptAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveTradeAcceptProgress(context.Context, bridge.TradeAcceptAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
	PreviewEndTrade(context.Context, *c.Identity, string, string, o.EndTradeKind, bool) (*o.PreviewReply, bridge.Result, error)
	LookupTradeEnd(context.Context, bridge.TradeEndAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveTradeEndProgress(context.Context, bridge.TradeEndAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type TradeWriter interface {
	ApplyOpenTrade(context.Context, *a.WritePrecondition, string, string, string, string, bool) (*o.ExecuteReply, bridge.Result, error)
	ApplySetTradeLines(context.Context, *a.WritePrecondition, string, string, []bridge.TradeLineInput, bool) (*o.ExecuteReply, bridge.Result, error)
	ApplyAcceptTrade(context.Context, *a.WritePrecondition, string, string, string, []bridge.TradeEconomicFloor, bool, bool) (*o.ExecuteReply, bridge.Result, error)
	ApplyEndTrade(context.Context, *a.WritePrecondition, string, string, o.EndTradeKind, bool) (*o.ExecuteReply, bridge.Result, error)
}
type TradeCapabilities struct {
	Native TradeNative
	Writer TradeWriter
}
type TradeBoundary struct {
	native  TradeNative
	writer  TradeWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
}

func NewTradeBoundary(native TradeNative, writer TradeWriter, leases boundary.LeaseSource, clock executor.Clock, session string) (*TradeBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid trade boundary dependencies")
	}
	return &TradeBoundary{native, writer, leases, clock, session}, nil
}

func tradeEndKindWire(kind domain.TradeEndKind) (o.EndTradeKind, error) {
	switch kind {
	case domain.TradeEndCancel:
		return o.EndTradeKind_END_TRADE_KIND_CANCEL, nil
	case domain.TradeEndCloseDialog:
		return o.EndTradeKind_END_TRADE_KIND_CLOSE_DIALOG, nil
	default:
		return 0, executor.ErrEvidence
	}
}
func tradeLinesWire(lines []domain.TradeLine) []bridge.TradeLineInput {
	out := make([]bridge.TradeLineInput, 0, len(lines))
	for _, l := range lines {
		out = append(out, bridge.TradeLineInput{LineID: l.LineID, AbsoluteCount: l.AbsoluteCount})
	}
	return out
}
func tradeFloorsWire(floors []domain.TradeEconomicFloor) []bridge.TradeEconomicFloor {
	out := make([]bridge.TradeEconomicFloor, 0, len(floors))
	for _, f := range floors {
		out = append(out, bridge.TradeEconomicFloor{DefName: f.DefName, Count: f.Count})
	}
	return out
}

func (b *TradeBoundary) InspectTrade(ctx context.Context, target executor.Target, dependency executor.TradeDependency) (executor.TradeInspection, error) {
	out := executor.TradeInspection{StartedAt: b.clock.Now()}
	trade, ok := target.Action.Trade()
	if !ok {
		return out, executor.ErrEvidence
	}
	switch trade.Kind() {
	case domain.TradeOpen:
		return b.inspectOpen(ctx, target, trade, out)
	case domain.TradeSetLines:
		return b.inspectSetLines(ctx, target, trade, dependency, out)
	case domain.TradeAccept:
		return b.inspectAccept(ctx, target, trade, dependency, out)
	case domain.TradeEnd:
		return b.inspectEnd(ctx, target, trade, dependency, out)
	default:
		return out, executor.ErrEvidence
	}
}

func (b *TradeBoundary) inspectOpen(ctx context.Context, target executor.Target, trade domain.Trade, out executor.TradeInspection) (executor.TradeInspection, error) {
	world, _, err := b.native.ReadWorld(ctx, boundary.Identity(target.Snapshot), 0, tradeWorldRadius)
	if err != nil {
		return out, err
	}
	observedContext, err := boundary.Context(world.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	var trader *bridge.SettlementFact
	for i := range world.Settlements {
		if world.Settlements[i].ID == string(trade.Trader()) {
			trader = &world.Settlements[i]
			break
		}
	}
	if trader == nil {
		return out, executor.ErrEvidence
	}
	pawns, _, err := b.native.ReadPawns(ctx, boundary.Identity(observedContext), []string{string(trade.Negotiator())})
	if err != nil {
		return out, err
	}
	pawnObserved := pawns.GetObserved()
	if pawnObserved == nil {
		return out, executor.ErrHeld
	}
	pawnCtx, err := boundary.Context(pawnObserved.Context, observedContext)
	if err != nil {
		return out, err
	}
	if len(pawnObserved.Pawns) != 1 || pawnObserved.Pawns[0] == nil || pawnObserved.Pawns[0].Pawn == nil || pawnObserved.Pawns[0].Pawn.GetId() != string(trade.Negotiator()) {
		return out, executor.ErrEvidence
	}
	negotiatorToken, err := boundary.PawnToken(pawnObserved.Pawns[0], pawnObserved.Context)
	if err != nil {
		return out, err
	}
	preview, _, err := b.native.PreviewOpenTrade(ctx, boundary.Identity(pawnCtx), string(trade.Trader()), trader.SnapshotToken, string(trade.Negotiator()), negotiatorToken, trade.GiftMode())
	if err != nil {
		return out, err
	}
	evaluated := preview.GetEvaluated()
	if evaluated == nil {
		return out, executor.ErrHeld
	}
	if _, err = boundary.Context(evaluated.Context, pawnCtx); err != nil {
		return out, err
	}
	prep := evaluated.GetTrade()
	if evaluated.Context.GetTick() < pawnObserved.Context.GetTick() || evaluated.Accepted == nil || prep == nil || prep.GetSettlementId() != string(trade.Trader()) {
		return out, executor.ErrEvidence
	}
	facts := policy.TradeAdmissionFacts{
		Snapshot: observedContext, PreviewTick: domain.Tick(evaluated.Context.GetTick()), NativeCanTry: domain.Known(evaluated.GetAccepted()),
		Open: policy.TradeOpenFacts{
			TraderSnapshotToken: trader.SnapshotToken, TraderIsPlayer: domain.Known(trader.Player), TraderHostile: domain.Known(trader.Relation == "Hostile"),
			NegotiatorSnapshotToken: negotiatorToken,
		},
	}
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func (b *TradeBoundary) inspectSetLines(ctx context.Context, target executor.Target, trade domain.Trade, dependency executor.TradeDependency, out executor.TradeInspection) (executor.TradeInspection, error) {
	if !dependency.Resolved {
		return b.inspectUnresolvedSession(ctx, target, out)
	}
	preview, _, err := b.native.PreviewSetTradeLines(ctx, boundary.Identity(target.Snapshot), dependency.Session.SessionID, dependency.Session.SessionToken, tradeLinesWire(trade.Lines()), trade.AllowPawns())
	if err != nil {
		return out, err
	}
	return b.inspectSessionPreview(ctx, target, dependency, preview, out)
}
func (b *TradeBoundary) inspectAccept(ctx context.Context, target executor.Target, trade domain.Trade, dependency executor.TradeDependency, out executor.TradeInspection) (executor.TradeInspection, error) {
	if !dependency.Resolved {
		return b.inspectUnresolvedSession(ctx, target, out)
	}
	preview, _, err := b.native.PreviewAcceptTrade(ctx, boundary.Identity(target.Snapshot), dependency.Session.SessionID, dependency.Session.SessionToken, trade.ExpectedDealSignature(), tradeFloorsWire(trade.EconomicFloors()), trade.AllowEmpty(), trade.ReceiveQuest())
	if err != nil {
		return out, err
	}
	return b.inspectSessionPreview(ctx, target, dependency, preview, out)
}
func (b *TradeBoundary) inspectEnd(ctx context.Context, target executor.Target, trade domain.Trade, dependency executor.TradeDependency, out executor.TradeInspection) (executor.TradeInspection, error) {
	if !dependency.Resolved {
		return b.inspectUnresolvedSession(ctx, target, out)
	}
	kind, err := tradeEndKindWire(trade.EndKind())
	if err != nil {
		return out, err
	}
	preview, _, err := b.native.PreviewEndTrade(ctx, boundary.Identity(target.Snapshot), dependency.Session.SessionID, dependency.Session.SessionToken, kind, trade.ReceiveQuest())
	if err != nil {
		return out, err
	}
	return b.inspectSessionPreview(ctx, target, dependency, preview, out)
}

// inspectUnresolvedSession still establishes a live, fresh tick/context via
// the generic world read (there is no dedicated Trade read), so admission
// can refuse with the typed TradeSessionUnresolved reason instead of an
// opaque hold: the dependency Open action has not yet been observed
// complete, or its session has not yet been durably recorded (see
// store.TradeSession) -- never rediscovered or guessed here.
func (b *TradeBoundary) inspectUnresolvedSession(ctx context.Context, target executor.Target, out executor.TradeInspection) (executor.TradeInspection, error) {
	world, _, err := b.native.ReadWorld(ctx, boundary.Identity(target.Snapshot), 0, tradeWorldRadius)
	if err != nil {
		return out, err
	}
	observedContext, err := boundary.Context(world.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	facts := policy.TradeAdmissionFacts{Snapshot: observedContext, PreviewTick: domain.Tick(world.Context.GetTick())}
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func (b *TradeBoundary) inspectSessionPreview(ctx context.Context, target executor.Target, dependency executor.TradeDependency, preview *o.PreviewReply, out executor.TradeInspection) (executor.TradeInspection, error) {
	evaluated := preview.GetEvaluated()
	if evaluated == nil {
		return out, executor.ErrHeld
	}
	observedContext, err := boundary.Context(evaluated.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	prep := evaluated.GetTrade()
	if evaluated.Accepted == nil || prep == nil || prep.GetSessionId() != dependency.Session.SessionID {
		return out, executor.ErrEvidence
	}
	facts := policy.TradeAdmissionFacts{
		Snapshot: observedContext, PreviewTick: domain.Tick(evaluated.Context.GetTick()), NativeCanTry: domain.Known(evaluated.GetAccepted()),
		Session: policy.TradeSessionFacts{Resolved: true, SessionID: dependency.Session.SessionID, SessionToken: dependency.Session.SessionToken},
	}
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func (b *TradeBoundary) key(p executor.Placement) *c.AttemptKey {
	return &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))}
}

func (b *TradeBoundary) openAttempt(dispatch executor.TradeDispatch) (bridge.TradeOpenAttempt, domain.Trade, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	trade, ok := p.Action.Trade()
	if !ok || trade.Kind() != domain.TradeOpen || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Kind != domain.TradeOpen || admission.Tick > p.Tick ||
		!boundary.ValidID(admission.TraderSnapshotToken) || !boundary.ValidID(admission.NegotiatorSnapshotToken) {
		return bridge.TradeOpenAttempt{}, domain.Trade{}, executor.ErrEvidence
	}
	attemptKey := b.key(p)
	return bridge.TradeOpenAttempt{
		Identity: boundary.Identity(p.Snapshot), Attempt: attemptKey, Generation: uint64(p.Snapshot.Native),
		Trader: string(trade.Trader()), TraderToken: admission.TraderSnapshotToken,
		Negotiator: string(trade.Negotiator()), NegotiatorToken: admission.NegotiatorSnapshotToken, GiftMode: trade.GiftMode(),
	}, trade, nil
}
func (b *TradeBoundary) sessionScope(dispatch executor.TradeDispatch) (domain.Trade, string, string, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	trade, ok := p.Action.Trade()
	if !ok || trade.Kind() == domain.TradeOpen || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Kind != trade.Kind() || admission.Tick > p.Tick || !boundary.ValidID(admission.SessionToken) {
		return domain.Trade{}, "", "", executor.ErrEvidence
	}
	if !dispatch.Dependency.Resolved || dispatch.Dependency.Session.SessionToken != admission.SessionToken || !boundary.ValidID(dispatch.Dependency.Session.SessionID) {
		return domain.Trade{}, "", "", executor.ErrEvidence
	}
	return trade, dispatch.Dependency.Session.SessionID, admission.SessionToken, nil
}
func (b *TradeBoundary) setLinesAttempt(dispatch executor.TradeDispatch) (bridge.TradeSetLinesAttempt, error) {
	trade, session, token, err := b.sessionScope(dispatch)
	if err != nil || trade.Kind() != domain.TradeSetLines {
		return bridge.TradeSetLinesAttempt{}, executor.ErrEvidence
	}
	attemptKey := b.key(dispatch.Attempt)
	return bridge.TradeSetLinesAttempt{
		Identity: boundary.Identity(dispatch.Attempt.Snapshot), Attempt: attemptKey, Generation: uint64(dispatch.Attempt.Snapshot.Native),
		Session: session, SessionToken: token, Lines: tradeLinesWire(trade.Lines()), AllowPawns: trade.AllowPawns(),
	}, nil
}
func (b *TradeBoundary) acceptAttempt(dispatch executor.TradeDispatch) (bridge.TradeAcceptAttempt, error) {
	trade, session, token, err := b.sessionScope(dispatch)
	if err != nil || trade.Kind() != domain.TradeAccept {
		return bridge.TradeAcceptAttempt{}, executor.ErrEvidence
	}
	attemptKey := b.key(dispatch.Attempt)
	return bridge.TradeAcceptAttempt{
		Identity: boundary.Identity(dispatch.Attempt.Snapshot), Attempt: attemptKey, Generation: uint64(dispatch.Attempt.Snapshot.Native),
		Session: session, SessionToken: token, ExpectedDealSignature: trade.ExpectedDealSignature(), EconomicFloors: tradeFloorsWire(trade.EconomicFloors()),
		AllowEmpty: trade.AllowEmpty(), ReceiveQuest: trade.ReceiveQuest(),
	}, nil
}
func (b *TradeBoundary) endAttempt(dispatch executor.TradeDispatch) (bridge.TradeEndAttempt, error) {
	trade, session, token, err := b.sessionScope(dispatch)
	if err != nil || trade.Kind() != domain.TradeEnd {
		return bridge.TradeEndAttempt{}, executor.ErrEvidence
	}
	kind, err := tradeEndKindWire(trade.EndKind())
	if err != nil {
		return bridge.TradeEndAttempt{}, err
	}
	attemptKey := b.key(dispatch.Attempt)
	return bridge.TradeEndAttempt{
		Identity: boundary.Identity(dispatch.Attempt.Snapshot), Attempt: attemptKey, Generation: uint64(dispatch.Attempt.Snapshot.Native),
		Session: session, SessionToken: token, Kind: kind, ReceiveQuest: trade.ReceiveQuest(),
	}, nil
}

func (b *TradeBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.TradeDispatch) error {
	if err := boundary.Admission(receipt, dispatch.Attempt, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return executor.ErrEvidence
	}
	return nil
}

func (b *TradeBoundary) WriteTrade(ctx context.Context, dispatch executor.TradeDispatch) (executor.Receipt, error) {
	p := dispatch.Attempt
	out := executor.Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptUnknown}
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
	pre := func(identity *c.Identity, attempt *c.AttemptKey) *a.WritePrecondition {
		return &a.WritePrecondition{Identity: identity, Attempt: attempt, ExpectedGeneration: proto.Uint64(uint64(p.Snapshot.Native)),}
	}
	var reply *o.ExecuteReply
	var callErr error
	trade, ok := p.Action.Trade()
	if !ok {
		return out, executor.ErrEvidence
	}
	switch trade.Kind() {
	case domain.TradeOpen:
		attempt, _, err := b.openAttempt(dispatch)
		if err != nil {
			return out, err
		}
		reply, _, callErr = b.writer.ApplyOpenTrade(ctx, pre(attempt.Identity, attempt.Attempt), attempt.Trader, attempt.TraderToken, attempt.Negotiator, attempt.NegotiatorToken, attempt.GiftMode)
	case domain.TradeSetLines:
		attempt, err := b.setLinesAttempt(dispatch)
		if err != nil {
			return out, err
		}
		reply, _, callErr = b.writer.ApplySetTradeLines(ctx, pre(attempt.Identity, attempt.Attempt), attempt.Session, attempt.SessionToken, attempt.Lines, attempt.AllowPawns)
	case domain.TradeAccept:
		attempt, err := b.acceptAttempt(dispatch)
		if err != nil {
			return out, err
		}
		reply, _, callErr = b.writer.ApplyAcceptTrade(ctx, pre(attempt.Identity, attempt.Attempt), attempt.Session, attempt.SessionToken, attempt.ExpectedDealSignature, attempt.EconomicFloors, attempt.AllowEmpty, attempt.ReceiveQuest)
	case domain.TradeEnd:
		attempt, err := b.endAttempt(dispatch)
		if err != nil {
			return out, err
		}
		reply, _, callErr = b.writer.ApplyEndTrade(ctx, pre(attempt.Identity, attempt.Attempt), attempt.Session, attempt.SessionToken, attempt.Kind, attempt.ReceiveQuest)
	default:
		return out, executor.ErrEvidence
	}
	var refused *bridge.NativeFailure
	if errors.As(callErr, &refused) && refused.Value != nil && refused.Value.GetCode() != c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT {
		out.Kind = domain.ReceiptRefused
		return out, nil
	}
	if callErr != nil {
		return out, callErr
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

func (b *TradeBoundary) ObserveTrade(ctx context.Context, dispatch executor.TradeDispatch, current domain.GenerationSnapshot) (executor.TradeEvidence, error) {
	out := executor.TradeEvidence{StartedAt: b.clock.Now()}
	p := dispatch.Attempt
	trade, ok := p.Action.Trade()
	if !ok {
		return out, executor.ErrEvidence
	}
	var attemptKey *c.AttemptKey
	var lookupErr error
	var lookup *r.LookupReply
	var progressReply *r.ProgressReply
	switch trade.Kind() {
	case domain.TradeOpen:
		attempt, _, err := b.openAttempt(dispatch)
		if err != nil {
			return out, err
		}
		attemptKey = attempt.Attempt
		lookup, _, lookupErr = b.native.LookupTradeOpen(ctx, attempt)
		if lookupErr == nil {
			if err = b.checkLookup(lookup, dispatch, current); err != nil {
				return out, err
			}
			progressReply, _, lookupErr = b.native.ObserveTradeOpenProgress(ctx, attempt, nil)
		}
	case domain.TradeSetLines:
		attempt, err := b.setLinesAttempt(dispatch)
		if err != nil {
			return out, err
		}
		attemptKey = attempt.Attempt
		lookup, _, lookupErr = b.native.LookupTradeSetLines(ctx, attempt)
		if lookupErr == nil {
			if err = b.checkLookup(lookup, dispatch, current); err != nil {
				return out, err
			}
			progressReply, _, lookupErr = b.native.ObserveTradeSetLinesProgress(ctx, attempt, nil)
		}
	case domain.TradeAccept:
		attempt, err := b.acceptAttempt(dispatch)
		if err != nil {
			return out, err
		}
		attemptKey = attempt.Attempt
		lookup, _, lookupErr = b.native.LookupTradeAccept(ctx, attempt)
		if lookupErr == nil {
			if err = b.checkLookup(lookup, dispatch, current); err != nil {
				return out, err
			}
			progressReply, _, lookupErr = b.native.ObserveTradeAcceptProgress(ctx, attempt, nil)
		}
	case domain.TradeEnd:
		attempt, err := b.endAttempt(dispatch)
		if err != nil {
			return out, err
		}
		attemptKey = attempt.Attempt
		lookup, _, lookupErr = b.native.LookupTradeEnd(ctx, attempt)
		if lookupErr == nil {
			if err = b.checkLookup(lookup, dispatch, current); err != nil {
				return out, err
			}
			progressReply, _, lookupErr = b.native.ObserveTradeEndProgress(ctx, attempt, nil)
		}
	default:
		return out, executor.ErrEvidence
	}
	if lookupErr != nil {
		return out, lookupErr
	}
	if u, isUnknown := lookup.Outcome.(*r.LookupReply_Unknown); isUnknown {
		if _, err := boundary.Context(u.Unknown.GetContext(), current); err != nil {
			return out, err
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: domain.Tick(u.Unknown.GetContext().GetTick()), Effect: domain.EffectUnknown, Causality: domain.AfterDispatch}
		out.ObservedAt = b.clock.Now()
		return out, ctx.Err()
	}
	prog := progressReply.GetProgress()
	if prog == nil {
		return out, executor.ErrHeld
	}
	if !proto.Equal(prog.Attempt, attemptKey) {
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
	case *r.Progress_Pending:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectPending, Causality: domain.AfterDispatch}
		return out, ctx.Err()
	case *r.Progress_Completed:
		if !prog.GetCompleteInspection() || outcome.Completed == nil {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}
		out.Complete = true
		if trade.Kind() == domain.TradeOpen {
			effect := outcome.Completed.GetEvidence().GetTrade()
			if effect == nil || effect.GetSessionId() == "" || effect.GetSnapshot().GetAfterToken() == "" {
				return out, executor.ErrEvidence
			}
			out.SessionID, out.SessionToken = effect.GetSessionId(), effect.GetSnapshot().GetAfterToken()
		}
		return out, ctx.Err()
	case *r.Progress_Absent:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectAbsent, Causality: domain.AfterDispatch}
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

func (b *TradeBoundary) checkLookup(lookup *r.LookupReply, dispatch executor.TradeDispatch, current domain.GenerationSnapshot) error {
	switch v := lookup.Outcome.(type) {
	case *r.LookupReply_Unknown:
		return nil
	case *r.LookupReply_InFlight:
		if v.InFlight == nil {
			return executor.ErrEvidence
		}
		return boundary.Admission(&r.Receipt{Attempt: v.InFlight.Attempt, AdmittedContext: v.InFlight.AdmittedContext}, dispatch.Attempt, b.session)
	case *r.LookupReply_Receipt:
		return b.checkReceipt(v.Receipt, dispatch)
	default:
		return executor.ErrEvidence
	}
}

var _ executor.TradeBoundary = (*TradeBoundary)(nil)
