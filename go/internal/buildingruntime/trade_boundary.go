package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// TradeNative narrows *bridge.Client to what tradeBoundary reads. Every
// trade action is an idempotent intent naming its trader and negotiator,
// sent through Actions/Apply; native validates it against the one live
// session and refuses with a reason when it is stale.
type TradeNative interface {
	ReadTradeSession(context.Context, *c.Identity) (bridge.TradeSessionRead, bridge.Result, error)
}
type TradeCapabilities struct {
	Native TradeNative
	Writer boundary.ActionsWriter
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
