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

// TradeNative and TradeWriter narrow *bridge.Client and *bridge.TradeWriter
// to what tradeBoundary consumes. Open is anchored on the trader census
// (bridge.ListTraders) for the fresh trader and negotiator CAS tokens; the
// session-scoped kinds are anchored on the session recorded by the Open
// action's completion evidence (store.TradeSession), never rediscovered.
type TradeNative interface {
	ListTraders(context.Context, *c.Identity) (bridge.TradersRead, bridge.Result, error)
	PreviewOpenTrade(context.Context, *c.Identity, string, string, string, string, bool) (*op.PreviewReply, bridge.Result, error)
	LookupTradeOpen(context.Context, bridge.TradeOpenAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveTradeOpenProgress(context.Context, bridge.TradeOpenAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
	PreviewSetTradeLines(context.Context, *c.Identity, string, string, []bridge.TradeLineInput, bool) (*op.PreviewReply, bridge.Result, error)
	LookupTradeSetLines(context.Context, bridge.TradeSetLinesAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveTradeSetLinesProgress(context.Context, bridge.TradeSetLinesAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
	PreviewAcceptTrade(context.Context, *c.Identity, string, string, string, []bridge.TradeEconomicFloor, bool, bool) (*op.PreviewReply, bridge.Result, error)
	LookupTradeAccept(context.Context, bridge.TradeAcceptAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveTradeAcceptProgress(context.Context, bridge.TradeAcceptAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
	PreviewEndTrade(context.Context, *c.Identity, string, string, op.EndTradeKind, bool) (*op.PreviewReply, bridge.Result, error)
	LookupTradeEnd(context.Context, bridge.TradeEndAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveTradeEndProgress(context.Context, bridge.TradeEndAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type TradeWriter interface {
	ApplyOpenTrade(context.Context, *a.WritePrecondition, string, string, string, string, bool) (*op.ExecuteReply, bridge.Result, error)
	ApplySetTradeLines(context.Context, *a.WritePrecondition, string, string, []bridge.TradeLineInput, bool) (*op.ExecuteReply, bridge.Result, error)
	ApplyAcceptTrade(context.Context, *a.WritePrecondition, string, string, string, []bridge.TradeEconomicFloor, bool, bool) (*op.ExecuteReply, bridge.Result, error)
	ApplyEndTrade(context.Context, *a.WritePrecondition, string, string, op.EndTradeKind, bool) (*op.ExecuteReply, bridge.Result, error)
}
type TradeCapabilities struct {
	Native TradeNative
	Writer TradeWriter
}
type tradeBoundary struct {
	*boundary.Boundary
	trade TradeCapabilities
}

func tradeEndKindWire(kind domain.TradeEndKind) (op.EndTradeKind, error) {
	switch kind {
	case domain.TradeEndCancel:
		return op.EndTradeKind_END_TRADE_KIND_CANCEL, nil
	case domain.TradeEndCloseDialog:
		return op.EndTradeKind_END_TRADE_KIND_CLOSE_DIALOG, nil
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

func (b *tradeBoundary) InspectTrade(ctx context.Context, target executor.Target, dependency executor.TradeDependency) (executor.TradeInspection, error) {
	out := executor.TradeInspection{StartedAt: b.Clock.Now()}
	trade, ok := target.Action.Trade()
	if !ok {
		return out, executor.ErrEvidence
	}
	if trade.Kind() == domain.TradeOpen {
		return b.inspectOpen(ctx, target, trade, out)
	}
	if !dependency.Resolved {
		return b.inspectUnresolvedSession(ctx, target, out)
	}
	identity, session, token := boundary.Identity(target.Snapshot), dependency.Session.SessionID, dependency.Session.SessionToken
	var preview *op.PreviewReply
	var err error
	switch trade.Kind() {
	case domain.TradeSetLines:
		preview, _, err = b.trade.Native.PreviewSetTradeLines(ctx, identity, session, token, tradeLinesWire(trade.Lines()), trade.AllowPawns())
	case domain.TradeAccept:
		preview, _, err = b.trade.Native.PreviewAcceptTrade(ctx, identity, session, token, trade.ExpectedDealSignature(), tradeFloorsWire(trade.EconomicFloors()), trade.AllowEmpty(), trade.ReceiveQuest())
	case domain.TradeEnd:
		var kind op.EndTradeKind
		if kind, err = tradeEndKindWire(trade.EndKind()); err != nil {
			return out, err
		}
		preview, _, err = b.trade.Native.PreviewEndTrade(ctx, identity, session, token, kind, trade.ReceiveQuest())
	default:
		return out, executor.ErrEvidence
	}
	if err != nil {
		return out, err
	}
	evaluated := preview.GetEvaluated()
	if evaluated == nil {
		return out, executor.ErrHeld
	}
	observedContext, err := boundary.Context(evaluated.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	prep := evaluated.GetTrade()
	if evaluated.Accepted == nil || prep == nil || prep.GetSessionId() != session {
		return out, executor.ErrEvidence
	}
	out.Facts = policy.TradeAdmissionFacts{
		Snapshot: observedContext, PreviewTick: domain.Tick(evaluated.Context.GetTick()), NativeCanTry: domain.Known(evaluated.GetAccepted()),
		Session: policy.TradeSessionFacts{Resolved: true, SessionID: session, SessionToken: token},
	}
	out.ObservedAt = b.Clock.Now()
	return out, ctx.Err()
}

// inspectOpen re-reads the trader census for the exact trader and negotiator
// the plan named, then previews the open with their fresh tokens. A trader
// that has left the map is evidence (the plan cannot be re-anchored); an
// unlisted negotiator is likewise evidence.
func (b *tradeBoundary) inspectOpen(ctx context.Context, target executor.Target, trade domain.Trade, out executor.TradeInspection) (executor.TradeInspection, error) {
	census, _, err := b.trade.Native.ListTraders(ctx, boundary.Identity(target.Snapshot))
	if err != nil {
		return out, err
	}
	observedContext, err := boundary.Context(census.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	var trader *bridge.TraderRead
	for i := range census.Traders {
		if census.Traders[i].ID == trade.Trader() {
			trader = &census.Traders[i]
		}
	}
	var negotiator *bridge.NegotiatorRead
	for i := range census.Negotiators {
		if census.Negotiators[i].ID == string(trade.Negotiator()) {
			negotiator = &census.Negotiators[i]
		}
	}
	if trader == nil || negotiator == nil {
		return out, executor.ErrEvidence
	}
	preview, _, err := b.trade.Native.PreviewOpenTrade(ctx, boundary.Identity(observedContext), trader.ID, trader.Token, negotiator.ID, negotiator.Token, trade.GiftMode())
	if err != nil {
		return out, err
	}
	evaluated := preview.GetEvaluated()
	if evaluated == nil {
		return out, executor.ErrHeld
	}
	previewContext, err := boundary.Context(evaluated.Context, observedContext)
	if err != nil {
		return out, err
	}
	prep := evaluated.GetTrade()
	if evaluated.Accepted == nil || prep == nil || prep.GetSettlementId() != trader.ID {
		return out, executor.ErrEvidence
	}
	out.Facts = policy.TradeAdmissionFacts{
		Snapshot: previewContext, PreviewTick: domain.Tick(evaluated.Context.GetTick()), NativeCanTry: domain.Known(evaluated.GetAccepted()),
		Open: policy.TradeOpenFacts{TraderSnapshotToken: trader.Token, TraderCanTrade: domain.Known(trader.CanTrade), NegotiatorSnapshotToken: negotiator.Token},
	}
	out.ObservedAt = b.Clock.Now()
	return out, ctx.Err()
}

// inspectUnresolvedSession establishes a fresh context (the census read is
// the cheapest trade-scoped read) so admission refuses with the typed
// TradeSessionUnresolved reason instead of an opaque hold: the Open action
// has not yet been observed complete, or its session not yet recorded.
func (b *tradeBoundary) inspectUnresolvedSession(ctx context.Context, target executor.Target, out executor.TradeInspection) (executor.TradeInspection, error) {
	census, _, err := b.trade.Native.ListTraders(ctx, boundary.Identity(target.Snapshot))
	if err != nil {
		return out, err
	}
	observedContext, err := boundary.Context(census.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	out.Facts = policy.TradeAdmissionFacts{Snapshot: observedContext, PreviewTick: domain.Tick(census.Context.GetTick())}
	out.ObservedAt = b.Clock.Now()
	return out, ctx.Err()
}

func (b *tradeBoundary) openAttempt(dispatch executor.TradeDispatch) (bridge.TradeOpenAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	trade, ok := p.Action.Trade()
	if !ok || trade.Kind() != domain.TradeOpen || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Kind != domain.TradeOpen || admission.Tick > p.Tick ||
		!boundary.ValidID(admission.TraderSnapshotToken) || !boundary.ValidID(admission.NegotiatorSnapshotToken) {
		return bridge.TradeOpenAttempt{}, executor.ErrEvidence
	}
	return bridge.TradeOpenAttempt{
		Identity: boundary.Identity(p.Snapshot), Attempt: b.Attempt(p), Generation: uint64(p.Snapshot.Native),
		Trader: trade.Trader(), TraderToken: admission.TraderSnapshotToken,
		Negotiator: string(trade.Negotiator()), NegotiatorToken: admission.NegotiatorSnapshotToken, GiftMode: trade.GiftMode(),
	}, nil
}
func (b *tradeBoundary) sessionScope(dispatch executor.TradeDispatch, kind domain.TradeOperationKind) (domain.Trade, string, string, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	trade, ok := p.Action.Trade()
	if !ok || trade.Kind() != kind || kind == domain.TradeOpen || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Kind != kind || admission.Tick > p.Tick || !boundary.ValidID(admission.SessionToken) {
		return domain.Trade{}, "", "", executor.ErrEvidence
	}
	if !dispatch.Dependency.Resolved || dispatch.Dependency.Session.SessionToken != admission.SessionToken || !boundary.ValidID(dispatch.Dependency.Session.SessionID) {
		return domain.Trade{}, "", "", executor.ErrEvidence
	}
	return trade, dispatch.Dependency.Session.SessionID, admission.SessionToken, nil
}
func (b *tradeBoundary) setLinesAttempt(dispatch executor.TradeDispatch) (bridge.TradeSetLinesAttempt, error) {
	trade, session, token, err := b.sessionScope(dispatch, domain.TradeSetLines)
	if err != nil {
		return bridge.TradeSetLinesAttempt{}, err
	}
	return bridge.TradeSetLinesAttempt{
		Identity: boundary.Identity(dispatch.Attempt.Snapshot), Attempt: b.Attempt(dispatch.Attempt), Generation: uint64(dispatch.Attempt.Snapshot.Native),
		Session: session, SessionToken: token, Lines: tradeLinesWire(trade.Lines()), AllowPawns: trade.AllowPawns(),
	}, nil
}
func (b *tradeBoundary) acceptAttempt(dispatch executor.TradeDispatch) (bridge.TradeAcceptAttempt, error) {
	trade, session, token, err := b.sessionScope(dispatch, domain.TradeAccept)
	if err != nil {
		return bridge.TradeAcceptAttempt{}, err
	}
	return bridge.TradeAcceptAttempt{
		Identity: boundary.Identity(dispatch.Attempt.Snapshot), Attempt: b.Attempt(dispatch.Attempt), Generation: uint64(dispatch.Attempt.Snapshot.Native),
		Session: session, SessionToken: token, ExpectedDealSignature: trade.ExpectedDealSignature(), EconomicFloors: tradeFloorsWire(trade.EconomicFloors()),
		AllowEmpty: trade.AllowEmpty(), ReceiveQuest: trade.ReceiveQuest(),
	}, nil
}
func (b *tradeBoundary) endAttempt(dispatch executor.TradeDispatch) (bridge.TradeEndAttempt, error) {
	trade, session, token, err := b.sessionScope(dispatch, domain.TradeEnd)
	if err != nil {
		return bridge.TradeEndAttempt{}, err
	}
	kind, err := tradeEndKindWire(trade.EndKind())
	if err != nil {
		return bridge.TradeEndAttempt{}, err
	}
	return bridge.TradeEndAttempt{
		Identity: boundary.Identity(dispatch.Attempt.Snapshot), Attempt: b.Attempt(dispatch.Attempt), Generation: uint64(dispatch.Attempt.Snapshot.Native),
		Session: session, SessionToken: token, Kind: kind, ReceiveQuest: trade.ReceiveQuest(),
	}, nil
}

func (b *tradeBoundary) WriteTrade(ctx context.Context, dispatch executor.TradeDispatch) (executor.Receipt, error) {
	p := dispatch.Attempt
	trade, ok := p.Action.Trade()
	if !ok {
		return executor.Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptUnknown}, executor.ErrEvidence
	}
	var write func(*a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error)
	var attemptErr error
	switch trade.Kind() {
	case domain.TradeOpen:
		attempt, err := b.openAttempt(dispatch)
		attemptErr = err
		write = func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			return b.trade.Writer.ApplyOpenTrade(ctx, pre, attempt.Trader, attempt.TraderToken, attempt.Negotiator, attempt.NegotiatorToken, attempt.GiftMode)
		}
	case domain.TradeSetLines:
		attempt, err := b.setLinesAttempt(dispatch)
		attemptErr = err
		write = func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			return b.trade.Writer.ApplySetTradeLines(ctx, pre, attempt.Session, attempt.SessionToken, attempt.Lines, attempt.AllowPawns)
		}
	case domain.TradeAccept:
		attempt, err := b.acceptAttempt(dispatch)
		attemptErr = err
		write = func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			return b.trade.Writer.ApplyAcceptTrade(ctx, pre, attempt.Session, attempt.SessionToken, attempt.ExpectedDealSignature, attempt.EconomicFloors, attempt.AllowEmpty, attempt.ReceiveQuest)
		}
	case domain.TradeEnd:
		attempt, err := b.endAttempt(dispatch)
		attemptErr = err
		write = func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			return b.trade.Writer.ApplyEndTrade(ctx, pre, attempt.Session, attempt.SessionToken, attempt.Kind, attempt.ReceiveQuest)
		}
	default:
		attemptErr = executor.ErrEvidence
	}
	return b.DispatchWrite(ctx, p, func() error { return attemptErr }, write)
}

func (b *tradeBoundary) ObserveTrade(ctx context.Context, dispatch executor.TradeDispatch, current domain.GenerationSnapshot) (executor.TradeEvidence, error) {
	p := dispatch.Attempt
	out := executor.TradeEvidence{StartedAt: b.Clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundary.World(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	trade, ok := p.Action.Trade()
	if !ok {
		return out, executor.ErrEvidence
	}
	var attemptKey *c.AttemptKey
	var lookup *r.LookupReply
	var progress func(*r.Receipt) (*r.ProgressReply, bridge.Result, error)
	var err error
	switch trade.Kind() {
	case domain.TradeOpen:
		var attempt bridge.TradeOpenAttempt
		if attempt, err = b.openAttempt(dispatch); err != nil {
			return out, err
		}
		attemptKey = attempt.Attempt
		lookup, _, err = b.trade.Native.LookupTradeOpen(ctx, attempt)
		progress = func(admitted *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
			return b.trade.Native.ObserveTradeOpenProgress(ctx, attempt, admitted)
		}
	case domain.TradeSetLines:
		var attempt bridge.TradeSetLinesAttempt
		if attempt, err = b.setLinesAttempt(dispatch); err != nil {
			return out, err
		}
		attemptKey = attempt.Attempt
		lookup, _, err = b.trade.Native.LookupTradeSetLines(ctx, attempt)
		progress = func(admitted *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
			return b.trade.Native.ObserveTradeSetLinesProgress(ctx, attempt, admitted)
		}
	case domain.TradeAccept:
		var attempt bridge.TradeAcceptAttempt
		if attempt, err = b.acceptAttempt(dispatch); err != nil {
			return out, err
		}
		attemptKey = attempt.Attempt
		lookup, _, err = b.trade.Native.LookupTradeAccept(ctx, attempt)
		progress = func(admitted *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
			return b.trade.Native.ObserveTradeAcceptProgress(ctx, attempt, admitted)
		}
	case domain.TradeEnd:
		var attempt bridge.TradeEndAttempt
		if attempt, err = b.endAttempt(dispatch); err != nil {
			return out, err
		}
		attemptKey = attempt.Attempt
		lookup, _, err = b.trade.Native.LookupTradeEnd(ctx, attempt)
		progress = func(admitted *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
			return b.trade.Native.ObserveTradeEndProgress(ctx, attempt, admitted)
		}
	default:
		return out, executor.ErrEvidence
	}
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
	if admitted.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return out, executor.ErrEvidence
	}
	reply, _, err := progress(admitted)
	if err != nil {
		return out, err
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, attemptKey) {
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
	out.ObservedAt = b.Clock.Now()
	switch effect := v.Effect.(type) {
	case *r.Progress_Completed:
		if !v.GetCompleteInspection() || effect.Completed == nil {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect, out.Complete = domain.EffectCompleted, true
		if trade.Kind() == domain.TradeOpen {
			evidence := effect.Completed.GetEvidence().GetTrade()
			if evidence == nil || !boundary.ValidID(evidence.GetSessionId()) || !boundary.ValidID(evidence.GetSnapshot().GetAfterToken()) {
				return out, executor.ErrEvidence
			}
			out.SessionID, out.SessionToken = evidence.GetSessionId(), evidence.GetSnapshot().GetAfterToken()
		}
	case *r.Progress_Unsuccessful:
		if !v.GetCompleteInspection() || effect.Unsuccessful == nil {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect, out.Observation.UnsuccessfulReason, out.Complete = domain.EffectUnsuccessful, domain.NativeFailure, true
	case *r.Progress_Absent:
		if !v.GetCompleteInspection() {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect, out.Complete = domain.EffectAbsent, true
	case *r.Progress_Pending:
		if !v.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation.Effect = domain.EffectPending
	case *r.Progress_Unknown:
	default:
		return out, executor.ErrEvidence
	}
	return out, ctx.Err()
}

var _ executor.TradeBoundary = (*tradeBoundary)(nil)
