package buildingruntime

import (
	"context"
	"errors"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// TradeNative narrows *bridge.Client to what tradeBoundary reads. Every
// trade action is an idempotent intent naming its trader and negotiator,
// sent through Actions/Apply; native validates it against the one live
// session and refuses with a reason when it is stale.
type TradeNative interface {
	ReadTradeSession(context.Context, *c.Identity) (bridge.TradeSessionRead, bridge.Result, error)
}
type CommsTradeRequestNative interface {
	ReadTradeAcquisition(context.Context, *c.Identity, *op.FormCaravanIntent) (*o.TradeAcquisition, bridge.Result, error)
	ListTraders(context.Context, *c.Identity) (bridge.TradersRead, bridge.Result, error)
}
type TradeCapabilities struct {
	Native   TradeNative
	Writer   boundary.ActionsWriter
	Requests CommsTradeRequestNative
}
type tradeBoundary struct {
	*boundary.Boundary
	trade TradeCapabilities
}

// InspectTrade anchors the dispatch to a fresh native read. It does not
// preview: native judges the intent when it applies, and a refusal there is
// the intent's terminal receipt, where a refused preview would only hold it.
func (b *tradeBoundary) InspectTrade(ctx context.Context, target executor.Target) (executor.TradeInspection, error) {
	out := executor.TradeInspection{StartedAt: b.Clock.Now()}
	if _, err := bridge.IntentAction("inspect", target.Action); err != nil {
		return out, errors.Join(executor.ErrEvidence, err)
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

// WriteTrade sends the intent through Actions/Apply.
func (b *tradeBoundary) WriteTrade(ctx context.Context, p executor.Placement) (executor.Receipt, error) {
	return b.DispatchIntent(ctx, p, b.trade.Writer)
}

var _ executor.TradeBoundary = (*tradeBoundary)(nil)

func (b *tradeBoundary) InspectCommsTradeRequest(ctx context.Context, target executor.Target) (executor.CommsTradeInspection, error) {
	out := executor.CommsTradeInspection{StartedAt: b.Clock.Now()}
	request, ok := target.Action.CommsTradeRequest()
	if !ok {
		return out, executor.ErrEvidence
	}
	facts, _, err := b.trade.Requests.ReadTradeAcquisition(ctx, boundary.Identity(target.Snapshot), nil)
	if err != nil {
		return out, err
	}
	if facts == nil || facts.Context == nil {
		return out, executor.ErrEvidence
	}
	out.Snapshot, err = boundary.Context(facts.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	out.Tick = domain.Tick(facts.Context.GetTick())
	kind := c.TradeRequestKind_TRADE_REQUEST_KIND_CARAVAN
	if request.Kind == domain.TradeRequestOrbital {
		kind = c.TradeRequestKind_TRADE_REQUEST_KIND_ORBITAL
	}
	for _, row := range facts.Requests {
		if row.GetFactionId() == request.Faction && row.Kind == kind && row.GetTraderKind() == request.TraderKind {
			out.RequestKnown, out.LastRequestTick = true, row.GetLastRequestTick()
			out.Eligible = row.GetEligible() && slices.Contains(row.NegotiatorIds, request.Negotiator) && slices.ContainsFunc(facts.Consoles, func(c *o.TradeConsole) bool {
				return c.GetId() == request.Console && slices.Contains(c.NegotiatorIds, request.Negotiator)
			})
		}
	}
	for _, work := range facts.CommsWork {
		if work.GetFactionId() == request.Faction && work.GetConsoleId() == request.Console && work.GetNegotiatorId() == request.Negotiator {
			out.MatchingWork = true
		}
	}
	for _, arrival := range facts.Arrivals {
		if arrival.GetFactionId() == request.Faction && arrival.Kind == kind && arrival.GetTraderKind() == request.TraderKind {
			out.MatchingArrival = true
		}
	}
	sellers, _, err := b.trade.Requests.ListTraders(ctx, boundary.Identity(target.Snapshot))
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(sellers.Context, target.Snapshot); err != nil || sellers.Context.GetTick() < int64(out.Tick) {
		return out, executor.ErrEvidence
	}
	out.Tick = domain.Tick(sellers.Context.GetTick())
	for _, seller := range sellers.Traders {
		if seller.Faction == request.Faction && seller.Kind == request.TraderKind {
			out.MatchingSeller = true
		}
	}
	out.ObservedAt = b.Clock.Now()
	return out, nil
}
func (b *tradeBoundary) WriteCommsTradeRequest(ctx context.Context, placement executor.Placement) (executor.Receipt, error) {
	return b.DispatchIntent(ctx, placement, b.trade.Writer)
}
