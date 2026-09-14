package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// TradeEconomyNative is the read-only native surface the negotiation driver
// needs between phases: the identity every boundary resolves first, the
// session-scoped trade sheet each decision is made from, the construction
// deficit census economic floors fold in, and native's own accept preview,
// whose Accepted verdict is trading.py's `wouldSucceed`. Nothing here writes:
// every write this driver causes goes through the ordinary committed-plan
// executor path, exactly as a directly submitted Trade action does.
type TradeEconomyNative interface {
	Identity(context.Context) (*l.IdentityReply, bridge.Result, error)
	ReadTradeSheet(context.Context, *c.Identity, string) (bridge.TradeSheetRead, bridge.Result, error)
	ReadConstructionDeficits(context.Context, *c.Identity) (bridge.ConstructionDeficitRead, bridge.Result, error)
	PreviewAcceptTrade(context.Context, *c.Identity, string, string, string, []bridge.TradeEconomicFloor, bool, bool) (*o.PreviewReply, bridge.Result, error)
}

// TradeEconomy drives one player TradeEconomy request through its phases.
//
// RimWorld's trade sheet is session-scoped: the concrete line ids and counts a
// SetTradeLines action must carry do not exist until an Open has actually run.
// A committed domain.PlanSpec is immutable, so those two operations cannot
// share one plan with real values in it. Rather than inventing a generic
// plan-amendment facility, or collapsing the four already-built trade
// operations into one mega-action, this driver submits each phase as its own
// one-action plan -- the same single-action submission shape trade_open
// already resolves through -- and binds each successor action to the Open
// action whose session it addresses (store.RecordTradeSessionReference). It is
// the same "a fresh plan per tick, never an amended one" discipline
// store.CommitGoalMethod already uses for goal review.
//
// Advance is edge-triggered on a phase's plan reaching a terminal stage, so it
// is safe to call repeatedly and safe to call again after a restart: the
// negotiation record is the only state, every transition is guarded on the
// phase it came from, and a transition that lost a race returns ErrConflict
// rather than opening a second deal.
type TradeEconomy struct {
	player *Player
	native TradeEconomyNative
}

func NewTradeEconomy(player *Player, native TradeEconomyNative) (*TradeEconomy, error) {
	if player == nil || native == nil {
		return nil, ErrControl
	}
	return &TradeEconomy{player: player, native: native}, nil
}

// Submit stores explicit player intent to run one whole economic negotiation
// and commits its Open phase, under the shared player gate -- the same
// discipline SubmitTrade and SubmitSettlementGift use. Submission neither
// acquires authority nor issues a native command.
func (t *TradeEconomy) Submit(ctx context.Context, request store.TradeEconomySubmissionRequest) (store.TradeNegotiation, bool, error) {
	call, epoch, done, err := t.player.enter(ctx, false)
	if err != nil {
		return store.TradeNegotiation{}, false, err
	}
	defer done()
	old, err := t.player.journal.LookupTradeEconomy(call, request.RequestID)
	if err == nil {
		if old.Request.RequestID != request.RequestID {
			return store.TradeNegotiation{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.TradeNegotiation{}, false, err
	}
	if err = t.player.world(call, request.World); err != nil {
		return store.TradeNegotiation{}, false, err
	}
	if err = t.player.current(call, epoch); err != nil {
		return store.TradeNegotiation{}, false, err
	}
	return t.player.journal.SubmitTradeEconomy(call, request)
}

// Lookup reports one negotiation's current state.
func (t *TradeEconomy) Lookup(ctx context.Context, requestID string) (store.TradeNegotiation, error) {
	return t.player.journal.LookupTradeEconomy(ctx, requestID)
}

// Advance ticks every unfinished negotiation in one world exactly one phase
// edge. A negotiation whose current phase is still in flight is left alone. It
// returns the negotiations it moved.
func (t *TradeEconomy) Advance(ctx context.Context, world store.World) ([]store.TradeNegotiation, error) {
	call, _, done, err := t.player.enter(ctx, false)
	if err != nil {
		return nil, err
	}
	defer done()
	pending, err := t.player.journal.UnfinishedTradeNegotiations(call, world)
	if err != nil {
		return nil, err
	}
	if len(pending) == 0 {
		return nil, nil
	}
	identity, err := t.identity(call)
	if err != nil {
		return nil, err
	}
	var moved []store.TradeNegotiation
	for _, negotiation := range pending {
		next, changed, err := t.step(call, identity, negotiation)
		if err != nil {
			return moved, err
		}
		if changed {
			moved = append(moved, next)
		}
	}
	return moved, ctx.Err()
}

func (t *TradeEconomy) identity(ctx context.Context) (*c.Identity, error) {
	reply, _, err := t.native.Identity(ctx)
	if err != nil {
		return nil, err
	}
	decoded, err := observation.DecodeIdentity(reply)
	if err != nil {
		return nil, err
	}
	return &c.Identity{ColonyId: proto.String(string(decoded.Colony)), LoadToken: proto.String(string(decoded.Load)), MapId: proto.Int32(int32(decoded.Map))}, nil
}

// phaseStage reports the terminal stage of the one action the current phase's
// plan carries, or ok=false while that action is still in flight.
func (t *TradeEconomy) phaseStage(ctx context.Context, n store.TradeNegotiation) (domain.Stage, bool, error) {
	state, err := t.player.journal.LoadPlan(ctx, n.CurrentPlan)
	if err != nil {
		return "", false, err
	}
	for _, p := range state.Progress {
		if p.Action().ID() != n.CurrentAction {
			continue
		}
		v := p.View()
		if v.Unresolved {
			return "", false, nil
		}
		switch v.Stage {
		case domain.Completed, domain.Cancelled, domain.Unsuccessful:
			return v.Stage, true, nil
		}
		return "", false, nil
	}
	return "", false, errors.New("trade negotiation phase plan lost its action")
}

func (t *TradeEconomy) step(ctx context.Context, identity *c.Identity, n store.TradeNegotiation) (store.TradeNegotiation, bool, error) {
	stage, settled, err := t.phaseStage(ctx, n)
	if err != nil || !settled {
		return n, false, err
	}
	switch n.Phase {
	case store.TradeNegotiationPendingOpen:
		if stage != domain.Completed {
			next, err := t.player.journal.FinalizeTradeNegotiation(ctx, n.Request.RequestID, n.Phase, store.TradeNegotiationRefused, "Native trade session was not established", 0, nil)
			return next, err == nil, err
		}
		return t.decide(ctx, identity, n)
	case store.TradeNegotiationPendingLines:
		if stage != domain.Completed {
			return t.cancel(ctx, n, "Native trade rejected the staged lines; the deal was not accepted")
		}
		return t.confirm(ctx, identity, n)
	case store.TradeNegotiationPendingAccept:
		if stage != domain.Completed {
			next, err := t.player.journal.FinalizeTradeNegotiation(ctx, n.Request.RequestID, n.Phase, store.TradeNegotiationRefused, "Native trade did not confirm an exchange; inspect before retrying", 0, nil)
			return next, err == nil, err
		}
		next, err := t.player.journal.FinalizeTradeNegotiation(ctx, n.Request.RequestID, n.Phase, store.TradeNegotiationAccepted,
			"Native deal executed; delivery and hauling require observation.", n.NetSilver, nil)
		return next, err == nil, err
	case store.TradeNegotiationPendingEnd:
		outcome := store.TradeNegotiationNoTrade
		if stage != domain.Completed {
			outcome = store.TradeNegotiationRefused
		}
		next, err := t.player.journal.FinalizeTradeNegotiation(ctx, n.Request.RequestID, n.Phase, outcome, "", 0, nil)
		return next, err == nil, err
	default:
		return n, false, nil
	}
}

// cancel moves a negotiation to an End(cancel)-only plan. It is the one way
// this driver abandons a session it opened: never by leaving it open, and
// never by accepting something it could not fully re-verify.
func (t *TradeEconomy) cancel(ctx context.Context, n store.TradeNegotiation, reason string) (store.TradeNegotiation, bool, error) {
	end, err := domain.NewTradeEnd(domain.TradeEndCancel, false)
	if err != nil {
		return n, false, err
	}
	next, err := t.player.journal.AdvanceTradeNegotiation(ctx, n.Request.RequestID, n.Phase, store.TradeNegotiationStep{
		Phase: store.TradeNegotiationPendingEnd, Trade: end, Reason: reason,
	})
	return next, err == nil, err
}

// tradeSelectionFacts turns one live sheet plus the world's own economic
// commitments into SelectTrade's inputs.
func (t *TradeEconomy) tradeSelectionFacts(ctx context.Context, identity *c.Identity, n store.TradeNegotiation, sheet bridge.TradeSheetRead) (policy.TradeSelectionFacts, error) {
	directives, err := t.player.journal.ResourcePolicies(ctx, n.Request.World)
	if err != nil {
		return policy.TradeSelectionFacts{}, err
	}
	reserve := policy.TradeReserveFacts{Reserves: map[string]int64{}}
	for _, d := range directives {
		if d.Reserve() > 0 {
			reserve.Reserves[d.Resource()] = d.Reserve()
		}
		if d.Restricted() {
			reserve.Restricted = append(reserve.Restricted, d.Resource())
		}
	}
	construction, _, err := t.native.ReadConstructionDeficits(ctx, identity)
	if err != nil {
		return policy.TradeSelectionFacts{}, err
	}
	reserve.Construction = construction.StillNeed
	floors, stopped := policy.EconomicReserves(n.Request.Policy, reserve)
	facts := policy.TradeSelectionFacts{
		Complete: true, Rows: tradeSheetRowFacts(sheet.Rows),
		Floors: floors, Stopped: stopped, MaxSilverSpend: n.Request.MaxSilverSpend,
	}
	facts.ColonySilver, facts.TraderSilver, facts.SilverKnown = tradeSheetSilver(sheet.Rows)
	return facts, nil
}

// decide is the pending_open edge: Open completed, so the session exists and
// its sheet can finally be read. That sheet is what SelectTrade decides from;
// nothing before this point could have known these line ids or counts.
func (t *TradeEconomy) decide(ctx context.Context, identity *c.Identity, n store.TradeNegotiation) (store.TradeNegotiation, bool, error) {
	session, resolved, err := t.player.journal.LookupTradeSession(ctx, n.OpenAction)
	if err != nil {
		return n, false, err
	}
	if !resolved {
		// Open is complete but its session has not been durably recorded yet;
		// wait rather than guess which session is open.
		return n, false, nil
	}
	sheet, _, err := t.native.ReadTradeSheet(ctx, identity, session.SessionID)
	if err != nil {
		return n, false, err
	}
	if !tradeSheetUsable(sheet, n, session.SessionID) {
		return t.cancel(ctx, n, "Trade participants or session changed; the deal was not staged")
	}
	facts, err := t.tradeSelectionFacts(ctx, identity, n, sheet)
	if err != nil {
		return n, false, err
	}
	selection := policy.SelectTrade(n.Request.Policy, facts)
	if selection.Refused {
		return t.cancel(ctx, n, selection.Reason)
	}
	if len(selection.Selected) == 0 {
		end, err := domain.NewTradeEnd(domain.TradeEndCancel, false)
		if err != nil {
			return n, false, err
		}
		next, err := t.player.journal.AdvanceTradeNegotiation(ctx, n.Request.RequestID, n.Phase, store.TradeNegotiationStep{
			Phase: store.TradeNegotiationPendingEnd, Trade: end,
			Evidence: selection.Evidence, Reason: "No affordable eligible trade meets current policy.",
		})
		return next, err == nil, err
	}
	lines := tradeLinesOf(selection)
	setLines, err := domain.NewTradeSetLines(lines, false)
	if err != nil {
		return n, false, err
	}
	next, err := t.player.journal.AdvanceTradeNegotiation(ctx, n.Request.RequestID, n.Phase, store.TradeNegotiationStep{
		Phase: store.TradeNegotiationPendingLines, Trade: setLines,
		Selected: lines, Floors: tradeAcceptFloors(n.Request.Policy, facts.Floors, selection.SilverReserve),
		Evidence: selection.Evidence,
	})
	return next, err == nil, err
}

// confirm is the pending_lines edge, and ports trading.py's whole
// post-staging verification: re-read the sheet, re-run the same selection over
// it and require an exact match, then re-check budget, affordability and the
// protected silver reserve before accepting. Any drift cancels; none of these
// checks is skipped as a best-effort approximation.
func (t *TradeEconomy) confirm(ctx context.Context, identity *c.Identity, n store.TradeNegotiation) (store.TradeNegotiation, bool, error) {
	session, resolved, err := t.player.journal.LookupTradeSession(ctx, n.OpenAction)
	if err != nil {
		return n, false, err
	}
	if !resolved {
		return n, false, nil
	}
	sheet, _, err := t.native.ReadTradeSheet(ctx, identity, session.SessionID)
	if err != nil {
		return n, false, err
	}
	if !tradeSheetUsable(sheet, n, session.SessionID) {
		return t.cancel(ctx, n, "Trade participants or session changed; the deal was not accepted")
	}
	facts, err := t.tradeSelectionFacts(ctx, identity, n, sheet)
	if err != nil {
		return n, false, err
	}
	selection := policy.SelectTrade(n.Request.Policy, facts)
	if selection.Refused || !sameTradeLines(tradeLinesOf(selection), n.Selected) {
		return t.cancel(ctx, n, "Economic inventory or prices changed; inspect before a new trade")
	}
	if !sheet.BalanceKnown || !sheet.ColonyCanAfford || !sheet.TraderHasSilver {
		return t.cancel(ctx, n, "Trade exceeds its budget or native affordability checks failed")
	}
	net := sheet.Balance
	if net < -float64(n.Request.MaxSilverSpend) {
		return t.cancel(ctx, n, "Trade exceeds its budget or native affordability checks failed")
	}
	colonySilver, _, silverKnown := tradeSheetSilver(sheet.Rows)
	stopped := false
	for _, item := range facts.Stopped {
		stopped = stopped || item == "Silver"
	}
	if !silverKnown || float64(colonySilver)+net < float64(selection.SilverReserve) || (stopped && net < 0) {
		return t.cancel(ctx, n, "Trade violates the protected silver reserve")
	}
	if sheet.DealSignature == "" {
		return t.cancel(ctx, n, "Native trade preview identity is unavailable")
	}
	floors := n.Floors
	preview, _, err := t.native.PreviewAcceptTrade(ctx, identity, session.SessionID, session.SessionToken, sheet.DealSignature, tradeFloorsWire(floors), false, false)
	if err != nil {
		return n, false, err
	}
	// Accepted is native's own wouldSucceed verdict; acceptance is not
	// authority, and the executor still re-previews at dispatch.
	evaluated := preview.GetEvaluated()
	if evaluated == nil || !evaluated.GetAccepted() || evaluated.GetTrade().GetSessionId() != session.SessionID || evaluated.GetTrade().GetDealSignature() != sheet.DealSignature {
		return t.cancel(ctx, n, "Trade exceeds its budget or native affordability checks failed")
	}
	accept, err := domain.NewTradeAccept(sheet.DealSignature, floors, false, false)
	if err != nil {
		return n, false, err
	}
	next, err := t.player.journal.AdvanceTradeNegotiation(ctx, n.Request.RequestID, n.Phase, store.TradeNegotiationStep{
		Phase: store.TradeNegotiationPendingAccept, Trade: accept, Evidence: selection.Evidence,
	})
	if err == nil {
		next.NetSilver = net
	}
	return next, err == nil, err
}

// tradeSheetUsable re-checks that the sheet just read still describes this
// negotiation's own session and participants, and is still tradeable, before
// any decision is made from it -- trading.py's participant check.
func tradeSheetUsable(sheet bridge.TradeSheetRead, n store.TradeNegotiation, session string) bool {
	return sheet.SessionID == session && sheet.SessionToken != "" && !sheet.GiftMode && sheet.CanTradeNow &&
		sheet.Trader == string(n.Request.Trader) && sheet.Negotiator == string(n.Request.Negotiator)
}

func tradeSheetRowFacts(rows []bridge.TradeSheetRow) []policy.TradeSheetRowFact {
	out := make([]policy.TradeSheetRowFact, 0, len(rows))
	for _, row := range rows {
		out = append(out, policy.TradeSheetRowFact{
			LineID: row.LineID, DefName: row.DefName, ColonyCount: row.ColonyCount, TraderCount: row.TraderCount,
			BuyPrice: row.BuyPrice, BuyPriceKnown: row.BuyPriceKnown, SellPrice: row.SellPrice, SellPriceKnown: row.SellPriceKnown,
			TraderWillTrade: row.TraderWillTrade, TraderWillTradeKnown: row.TraderWillTradeKnown,
			Currency: row.Currency, CurrencyKnown: row.CurrencyKnown, Pawn: row.Pawn, PawnKnown: row.PawnKnown,
			ProtectedExport: row.ProtectedExport, ProtectedExportKnown: row.ProtectedExportKnown,
		})
	}
	return out
}

// tradeSheetSilver reads both sides' current silver from the sheet's own
// currency row. Python read these from the legacy handler's balance dict as
// colonySilverNow/traderSilverNow; the proto TradeSheet carries only a net
// balance, and this row is the same native figure those two fields reported.
func tradeSheetSilver(rows []bridge.TradeSheetRow) (int64, int64, bool) {
	for _, row := range rows {
		if row.DefName == "Silver" && row.CurrencyKnown && row.Currency {
			return row.ColonyCount, row.TraderCount, true
		}
	}
	return 0, 0, false
}

func tradeLinesOf(selection policy.TradeSelection) []domain.TradeLine {
	out := make([]domain.TradeLine, 0, len(selection.Selected))
	for _, line := range selection.Selected {
		out = append(out, domain.TradeLine{LineID: line.LineID, AbsoluteCount: int32(line.Count)})
	}
	return out
}

func sameTradeLines(left, right []domain.TradeLine) bool {
	if len(left) != len(right) {
		return false
	}
	for i := range left {
		if left[i] != right[i] {
			return false
		}
	}
	return true
}

// tradeAcceptFloors builds AcceptTrade's reserve guards exactly as
// trading.py does: every policy target's own retained level, raised to the
// computed economic floor where that is higher, plus the silver reserve.
func tradeAcceptFloors(p domain.TradeEconomicPolicy, floors map[string]int64, reserve int64) []domain.TradeEconomicFloor {
	out := make([]domain.TradeEconomicFloor, 0, len(p.Targets)+1)
	for _, target := range p.Targets {
		if target.Item == "Silver" {
			continue
		}
		out = append(out, domain.TradeEconomicFloor{DefName: target.Item, Count: int32(max(target.Stock, floors[target.Item]))})
	}
	return append(out, domain.TradeEconomicFloor{DefName: "Silver", Count: int32(reserve)})
}
