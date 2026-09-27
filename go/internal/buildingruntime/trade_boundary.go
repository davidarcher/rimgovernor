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
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// TradeNative and TradeWriter narrow *bridge.Client and *bridge.TradeWriter
// to what tradeBoundary consumes. Every trade action is an idempotent
// intent naming its trader and negotiator; native validates it against the
// one live session and refuses with a reason when it is stale.
type TradeNative interface {
	ReadTradeSession(context.Context, *c.Identity) (bridge.TradeSessionRead, bridge.Result, error)
}
type TradeWriter interface {
	ApplyTrade(context.Context, *a.WritePrecondition, *op.Operation) (*op.ExecuteReply, bridge.Result, error)
}
type TradeCapabilities struct {
	Native TradeNative
	Writer TradeWriter
}
type tradeBoundary struct {
	*boundary.Boundary
	trade TradeCapabilities
}

func tradeLinesWire(lines []domain.TradeLine) []bridge.TradeLineInput {
	out := make([]bridge.TradeLineInput, 0, len(lines))
	for _, l := range lines {
		out = append(out, bridge.TradeLineInput{LineID: l.LineID, AbsoluteCount: l.AbsoluteCount})
	}
	return out
}

// tradeIntent is the wire form of one domain trade action.
func tradeIntent(action domain.Action) (*op.Operation, error) {
	t, ok := action.Trade()
	if !ok {
		return nil, executor.ErrEvidence
	}
	trader, negotiator := t.Trader(), string(t.Negotiator())
	switch t.Kind() {
	case domain.TradeOpen:
		return bridge.OpenTradeIntent(trader, negotiator, t.GiftMode()), nil
	case domain.TradeSetLines:
		return bridge.SetTradeLinesIntent(trader, negotiator, tradeLinesWire(t.Lines()), t.AllowPawns()), nil
	case domain.TradeAccept:
		floors := make([]bridge.TradeEconomicFloor, 0, len(t.EconomicFloors()))
		for _, f := range t.EconomicFloors() {
			floors = append(floors, bridge.TradeEconomicFloor{DefName: f.DefName, Count: f.Count})
		}
		return bridge.AcceptTradeIntent(trader, negotiator, t.ExpectedDealSignature(), floors, t.AllowEmpty(), t.ReceiveQuest()), nil
	case domain.TradeEnd:
		switch t.EndKind() {
		case domain.TradeEndCancel:
			return bridge.EndTradeIntent(trader, negotiator, op.EndTradeKind_END_TRADE_KIND_CANCEL, t.ReceiveQuest()), nil
		case domain.TradeEndCloseDialog:
			return bridge.EndTradeIntent(trader, negotiator, op.EndTradeKind_END_TRADE_KIND_CLOSE_DIALOG, t.ReceiveQuest()), nil
		}
	}
	return nil, executor.ErrEvidence
}

// InspectTrade anchors the dispatch to a fresh native read. It does not
// preview: native judges the intent when it applies, and a refusal there is
// the intent's terminal receipt, where a refused preview would only hold it.
func (b *tradeBoundary) InspectTrade(ctx context.Context, target executor.Target) (executor.TradeInspection, error) {
	out := executor.TradeInspection{StartedAt: b.Clock.Now()}
	if _, err := tradeIntent(target.Action); err != nil {
		return out, err
	}
	session, _, err := b.trade.Native.ReadTradeSession(ctx, boundary.Identity(target.Snapshot))
	if err != nil {
		return out, err
	}
	observed, err := boundary.Context(session.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	out.Facts = policy.TradeAdmissionFacts{Snapshot: observed, PreviewTick: domain.Tick(session.Context.GetTick())}
	out.ObservedAt = b.Clock.Now()
	return out, ctx.Err()
}

// WriteTrade sends the intent. A native failure reply is a refused receipt
// (the intent does not apply to live state), except an attempt conflict,
// which stays uncertain.
func (b *tradeBoundary) WriteTrade(ctx context.Context, p executor.Placement) (executor.Receipt, error) {
	intent, err := tradeIntent(p.Action)
	receipt, err := b.DispatchWrite(ctx, p, func() error { return err }, func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
		return b.trade.Writer.ApplyTrade(ctx, pre, intent)
	})
	var refused *bridge.NativeFailure
	if errors.As(err, &refused) && refused.Value != nil && refused.Value.GetCode() != c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT {
		receipt.Kind = domain.ReceiptRefused
		return receipt, nil
	}
	return receipt, err
}

var _ executor.TradeBoundary = (*tradeBoundary)(nil)
