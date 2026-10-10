package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"slices"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// RoundsTradeSource is the native census RoundsTradePlanner reads between
// phases: the trader census for the caravan and negotiator to open with, the
// trade-session read for the walk or session native holds, a
// fresh colony facts read for the medicine and resource stock the need is
// re-measured from, and the session-scoped sheet each decision is made
// from. A phase is not previewed: native judges it when it applies, and a
// refusal fails its method.
type RoundsTradeSource interface {
	ReadConstructionDeficits(context.Context, *c.Identity) (bridge.ConstructionDeficitRead, bridge.Result, error)
	ReadColonyFacts(context.Context, *c.Identity, bool) (*o.ColonyFactsReply, bridge.Result, error)
	FrameTables(context.Context, *c.Identity) (bridge.Tables, error)
	ListTraders(context.Context, *c.Identity) (bridge.TradersRead, bridge.Result, error)
	ReadTradeSession(context.Context, *c.Identity) (bridge.TradeSessionRead, bridge.Result, error)
	ReadTradeSheet(context.Context, *c.Identity) (bridge.TradeSheetRead, bridge.Result, error)
}

// RoundsTradePlanner drives TradeWithCaravan one phase edge per
// step. RimWorld's trade sheet is session-scoped: the line ids and counts a
// SetTradeLines action carries do not exist until Open has run, and the
// deal signature Accept requires does not exist until the lines are staged.
// A committed plan is immutable, so each phase is its own incident method --
// open, then set lines or cancel, then accept or cancel -- each an intent
// naming the session's trader and negotiator, which native checks. Trade
// is an intent-mode kind (domain.ActionKind.IntentMode): a phase's applied
// receipt completes its method and a refused one fails it, and what followed
// is read from live state: the trade-session read (who is walking to or
// trading with whom) and the session sheet. Per
// caravan and TradeWithCaravan occurrence the method ids are fixed, so a restart resumes
// where it left off. A caravan whose session reached accept or cancel, or
// whose open the shared refusal budget barred (native refused it for good, or
// for the current world), is settled for the occurrence: it closes when the caravan leaves or
// the next review measures nothing left to trade.
//
// Adjacency is native's: OpenTrade orders vanilla's TradeWithPawn job, which
// walks the negotiator after the trader, and native opens the session where
// that job would open its dialog. While the session read shows the walk, the
// routine lends the clock a short window; a walk the game interrupted reads
// as neither walk nor session, and the open is sent again. Once open, the
// session binds its participants and the remaining phases need no escort.
type RoundsTradePlanner struct {
	reviewer *Rounder
	native   RoundsTradeSource
}
type RoundsTradeResult struct {
	Verdict
	Plan   domain.PlanID
	Trader string
	Phase  domain.TradeOperationKind
	// NativeWorkTicks asks for a clock window without a plan: a caravan is
	// still walking to its trade spot, or a settled one has yet to leave,
	// and only game time moves it.
	NativeWorkTicks uint32
}

// tradeArrivalTicks is the window lent while a caravan walks in or a
// settled one lingers: one game hour, after which the census is read again.
const tradeArrivalTicks = domain.TicksPerHour

// tradeWalkTicks is the window lent while a negotiator walks to open a
// session, after which the session is read again.
const tradeWalkTicks = 250

func NewRoundsTradePlanner(reviewer *Rounder, native RoundsTradeSource) (*RoundsTradePlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsTradePlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoundsTradePlanner{reviewer, native}, nil
}

// tradeMethod is the fixed method id of one caravan's phase attempt.
func tradeMethod(kind domain.TradeOperationKind, trader string, attempt int) domain.MethodID {
	digest := sha256.Sum256([]byte(trader))
	return domain.MethodID(fmt.Sprintf("trade-%s-%x-%d", kind, digest[:8], attempt))
}

// tradeOpenSubject is the shared refusal budget's subject for opening a
// session with trader. An open ends without a session when the world moves
// under the walk -- the trader wandering off as the negotiator arrives, a
// path that could not complete -- and is walked again; only native's refusals
// of it spend the budget. A line staging, accept or end is attempted once.
func tradeOpenSubject(trader string) string {
	digest := sha256.Sum256([]byte(trader))
	return fmt.Sprintf("trade-open-%x", digest[:8])
}

// tradePhase is one caravan's latest recorded attempt at a phase and the
// settled stage of its trade action: open reports in-flight work, and a
// terminal stage other than Completed is a failure of the attempt. plans
// lists every attempt's plan, for the refusal budget.
type tradePhase struct {
	found, open, completed bool
	attempt                int
	plans                  []domain.PlanID
}

func (r *RoundsTradePlanner) failed(p tradePhase) bool { return p.found && !p.open && !p.completed }

func (r *RoundsTradePlanner) phase(ctx context.Context, incident store.IncidentState, kind domain.TradeOperationKind, trader string) (tradePhase, error) {
	out := tradePhase{}
	for attempt := 0; ; attempt++ {
		want := tradeMethod(kind, trader, attempt)
		i := slices.IndexFunc(incident.Methods, func(m store.IncidentMethod) bool { return m.Method == want })
		if i < 0 {
			return out, nil
		}
		out.plans = append(out.plans, incident.Methods[i].Plan)
		plan, err := r.reviewer.player.journal.LoadPlan(ctx, incident.Methods[i].Plan)
		if err != nil {
			return tradePhase{}, err
		}
		found := false
		for _, progress := range plan.Progress {
			if _, ok := progress.Action().Trade(); ok {
				out = tradePhase{found: true, open: store.PlanOpen(plan), completed: progress.View().Stage == domain.Completed, attempt: attempt, plans: out.plans}
				found = true
			}
		}
		if !found {
			return tradePhase{}, fmt.Errorf("%w: phase: !found", ErrControl)
		}
	}
}

// tradeSettled reports whether the caravan's session is over for this occurrence:
// its last open attempt ended with no session or walk left and the
// refusal budget bars another, its accept
// applied or was refused, or an end phase exists and is no longer open.
// engaged is whether native holds a walk or session with the trader.
func (r *RoundsTradePlanner) tradeSettled(ctx context.Context, incident store.IncidentState, trader string, engaged bool, world domain.GenerationSnapshot) (bool, error) {
	open, err := r.phase(ctx, incident, domain.TradeOpen, trader)
	if err != nil {
		return false, err
	}
	if tradeOpenSpent(open, engaged) {
		// An open that ended is walked again until native's refusals bar it:
		// a permanent one for good, a transient one until the world changes.
		if _, ok, err := admitSubject(ctx, r.reviewer.player.journal, tradeOpenSubject(trader), open.plans, world); err != nil || !ok {
			return err == nil, err
		}
	}
	accept, err := r.phase(ctx, incident, domain.TradeAccept, trader)
	if err != nil {
		return false, err
	}
	if tradeAcceptSpent(accept) {
		return true, nil
	}
	end, err := r.phase(ctx, incident, domain.TradeEnd, trader)
	if err != nil {
		return false, err
	}
	return end.found && !end.open, nil
}

// tradeAcceptSpent reports an accept that is over, applied or refused:
// native closes the session either way, so the caravan is settled
// for the occurrence and the routine replans rather than reopening it.
func tradeAcceptSpent(accept tradePhase) bool { return accept.found && !accept.open }

// tradeOpenSpent reports an open attempt that is over without a session
// or walk to show for it: refused, or applied and since ended.
func tradeOpenSpent(open tradePhase, engaged bool) bool {
	return open.found && !open.open && (!open.completed || !engaged)
}

func (r *RoundsTradePlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsTradeResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsTradeResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsTradeResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsTradeResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsTradeResult{Verdict: BuildingReasonNoReview}, nil
	}
	if result, handled, err := r.mission(call, epoch, state, review, arbiter); handled || err != nil {
		return result, err
	}
	if result, handled, err := r.request(call, epoch, state, review); handled || err != nil {
		return result, err
	}
	incident, found, err := incidentDeficit(call, p.journal, review, policy.TradeWithCaravan)
	if err != nil {
		return RoundsTradeResult{}, err
	}
	if !found {
		return RoundsTradeResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	if open, err := incidentOpenWork(call, p.journal, incident); err != nil || open {
		return RoundsTradeResult{Verdict: BuildingReasonExistingWork}, err
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	census, _, err := r.native.ListTraders(call, identity)
	if err != nil {
		return RoundsTradeResult{}, err
	}
	if _, err = boundary.Context(census.Context, state.Snapshot); err != nil || census.Context.GetTick() < int64(review.Tick) {
		return RoundsTradeResult{}, fmt.Errorf("%w: step: err != nil || census.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	session, _, err := r.native.ReadTradeSession(call, identity)
	if err != nil {
		return RoundsTradeResult{}, err
	}
	if _, err = boundary.Context(session.Context, state.Snapshot); err != nil || session.Context.GetTick() < int64(review.Tick) {
		return RoundsTradeResult{}, fmt.Errorf("%w: step: err != nil || session.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	// A caravan native holds a session or walk with is driven first,
	// tradeable or not: a session native still holds must be settled, never
	// left, and a walk only game time finishes.
	traders := make([]policy.TraderFacts, 0, len(census.Traders))
	for _, row := range census.Traders {
		traders = append(traders, policy.TraderFacts{Participant: row.Participant, ID: row.ID, Kind: row.Kind, Faction: row.Faction, CanTrade: row.CanTrade, Travelling: row.Travelling, GoodsStacks: int64(row.GoodsStacks)})
	}
	arriving := false
	for _, row := range census.Traders {
		arriving = arriving || row.Travelling
		if row.ID != session.Trader {
			continue
		}
		settled, err := r.tradeSettled(call, incident, row.ID, true, state.Snapshot)
		if err != nil {
			return RoundsTradeResult{}, err
		}
		if settled {
			continue
		}
		if !session.Open {
			return RoundsTradeResult{Verdict: BuildingReasonExistingWork, Trader: row.ID, Phase: domain.TradeOpen, NativeWorkTicks: tradeWalkTicks}, nil
		}
		return r.drive(call, epoch, state, incident, review, row.ID, traders, domain.PawnID(session.Negotiator), started)
	}
	settled := map[string]bool{}
	waiting := arriving
	for _, row := range census.Traders {
		if settled[row.ID], err = r.tradeSettled(call, incident, row.ID, row.ID == session.Trader, state.Snapshot); err != nil {
			return RoundsTradeResult{}, err
		}
		// A settled caravan is waited out: the occurrence closes when it
		// leaves, and only game time takes it away.
		waiting = waiting || (settled[row.ID] && row.CanTrade)
	}
	trader, ok := policy.SelectTrader(traders, settled)
	if !ok {
		if waiting {
			return RoundsTradeResult{Verdict: waitFor(policy.CauseExistingWork, "caravan_departure"), NativeWorkTicks: tradeArrivalTicks}, nil
		}
		return RoundsTradeResult{Verdict: waitFor(policy.CauseMethodUsed, "caravan_trade")}, nil
	}
	if len(census.Negotiators) == 0 {
		return RoundsTradeResult{Verdict: noWorker("negotiator")}, nil
	}
	negotiator, err := r.negotiator(call, state, review, census.Negotiators)
	if err != nil {
		return RoundsTradeResult{}, err
	}
	open, err := r.phase(call, incident, domain.TradeOpen, trader.ID)
	if err != nil {
		return RoundsTradeResult{}, err
	}
	attempt := 0
	if open.found {
		attempt = open.attempt + 1
	}
	return r.open(call, epoch, state, incident, trader.ID, negotiator, attempt, arbiter, started, trader.Participant)
}

// negotiator picks the colonist to open with: policy.TraderFor over the
// roster profiles, restricted to the pawns native listed as eligible
// (alive, undrafted, able to talk). Native's own first row (best trade
// price improvement) stands when the roster is unknown or no profile
// qualifies -- native has already vetted every listed row.
func (r *RoundsTradePlanner) negotiator(call context.Context, state ControlState, review store.Rounds, eligible []bridge.NegotiatorRead) (bridge.NegotiatorRead, error) {
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return bridge.NegotiatorRead{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return bridge.NegotiatorRead{}, fmt.Errorf("%w: negotiator: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return bridge.NegotiatorRead{}, err
	}
	pawns, known := read.Projection.WorkPawns.Value()
	if !known {
		return eligible[0], nil
	}
	ids := make([]policy.PawnID, 0, len(eligible))
	for _, row := range eligible {
		ids = append(ids, policy.PawnID(row.ID))
	}
	chosen, ok := policy.TraderFor(policy.Among(policy.Profiles(pawns), ids))
	if !ok {
		return eligible[0], nil
	}
	for _, row := range eligible {
		if policy.PawnID(row.ID) == chosen {
			return row, nil
		}
	}
	return eligible[0], nil
}

// open commits the Open phase: the census already vetted the negotiator's
// eligibility and the trader's tradeability, and native checks
// reachability when the open applies and walks the negotiator over.
func (r *RoundsTradePlanner) open(call, epoch context.Context, state ControlState, incident store.IncidentState, trader string, negotiator bridge.NegotiatorRead, attempt int, arbiter *stepArbiter, started time.Time, participant domain.TradeParticipant) (RoundsTradeResult, error) {
	if !arbiter.tryClaim(nil, "pawn:"+negotiator.ID) {
		return RoundsTradeResult{Verdict: waitFor(policy.CauseMethodUsed, "negotiator_claim")}, nil
	}
	value, err := domain.NewTradeOpen(trader, domain.PawnID(negotiator.ID), false)
	if err != nil {
		return RoundsTradeResult{}, err
	}
	if participant.Kind != "" {
		value, err = value.WithParticipant(participant)
		if err != nil {
			return RoundsTradeResult{}, err
		}
	}
	return r.commit(call, epoch, state, incident, domain.TradeOpen, attempt, trader, value, started)
}

// drive advances one caravan's open session: stage the selected lines from
// its sheet, confirm and accept them, or cancel.
func (r *RoundsTradePlanner) drive(call, epoch context.Context, state ControlState, incident store.IncidentState, review store.Rounds, trader string, traders []policy.TraderFacts, negotiator domain.PawnID, started time.Time) (RoundsTradeResult, error) {
	lines, err := r.phase(call, incident, domain.TradeSetLines, trader)
	if err != nil {
		return RoundsTradeResult{}, err
	}
	end, err := r.phase(call, incident, domain.TradeEnd, trader)
	if err != nil {
		return RoundsTradeResult{}, err
	}
	if end.found {
		// An end that failed leaves native holding the session; there is
		// nothing further to propose, and tradeSettled already reads it as
		// over.
		return RoundsTradeResult{Verdict: refuse(policy.CauseRetriesSpent, "trade_end", ""), Trader: trader, Phase: domain.TradeEnd}, nil
	}
	accept, err := r.phase(call, incident, domain.TradeAccept, trader)
	if err != nil {
		return RoundsTradeResult{}, err
	}
	if r.failed(accept) {
		return r.cancel(call, epoch, state, incident, trader, negotiator, started)
	}
	identity := boundary.Identity(state.Snapshot)
	sheet, _, err := r.native.ReadTradeSheet(call, identity)
	if err != nil {
		return RoundsTradeResult{}, err
	}
	if _, err = boundary.Context(sheet.Context, state.Snapshot); err != nil {
		return RoundsTradeResult{}, fmt.Errorf("%w: drive: err != nil", ErrControl)
	}
	if sheet.Trader != trader || sheet.Negotiator != string(negotiator) || sheet.GiftMode || !sheet.CanTradeNow {
		return r.cancel(call, epoch, state, incident, trader, negotiator, started)
	}
	if err = r.recordOffers(call, state, sheet, traderStacks(traders, trader)); err != nil {
		return RoundsTradeResult{}, err
	}
	staged := tradeStagedLines(sheet)
	phase := tradeSessionPhase(lines, len(staged))
	if phase == domain.TradeEnd {
		return r.cancel(call, epoch, state, incident, trader, negotiator, started)
	}
	economic, facts, capacity, err := r.selection(call, state, review, sheet, trader, traders)
	if err != nil {
		return RoundsTradeResult{}, err
	}
	var selection policy.TradeSelection
	switch {
	case facts.Favor:
		// The tribute collector pays favor: only gold sells, with no silver
		// budget, reserve or purchase.
		selection = policy.SelectFavorSale(facts, facts.FavorKeep)
	case len(economic.Targets) == 0 && len(facts.SaleArt)+len(facts.SaleAnimals)+len(facts.SaleGear) == 0:
		// Nothing to buy or sell by the resource catalog: only a pawn
		// purchase can still stage, against the same silver reserve.
		currency, _ := policy.TradeCurrency(facts.Rows)
		selection = policy.TradeSelection{SilverReserve: max(economic.SilverReserve, facts.Floors[currency])}
	default:
		selection = policy.SelectTrade(economic, facts)
	}
	// A pawn buy is its own line beside the resource lines.
	if !selection.Refused && facts.SilverKnown && !facts.Favor {
		if pawn, ok := policy.SelectPawnPurchase(capacity, facts.Rows, facts.ColonySilver, selection.SilverReserve, selection.Selected); ok {
			selection.Selected = append(selection.Selected, pawn)
		}
		// A wanted animal is a second purchase line under the same reserve;
		// native needs no allow_pawns to buy a pawn row.
		if animal, ok := policy.SelectAnimalPurchase(facts.HerdWants, facts.Rows, facts.ColonySilver, selection.SilverReserve, selection.Selected); ok {
			selection.Selected = append(selection.Selected, animal)
		}
	}
	if phase == domain.TradeSetLines {
		if selection.Refused || len(selection.Selected) == 0 {
			return r.cancel(call, epoch, state, incident, trader, negotiator, started)
		}
		// Selling an animal is a pawn give-away native refuses without
		// allow_pawns; only a selected pawn sale asks for it.
		pawnRows := map[string]bool{}
		for _, row := range facts.Rows {
			pawnRows[row.LineID] = row.PawnKnown && row.Pawn
		}
		sellsPawn := false
		for _, line := range selection.Selected {
			sellsPawn = sellsPawn || line.Count < 0 && pawnRows[line.LineID]
		}
		value, err := domain.NewTradeSetLines(trader, negotiator, tradeLinesOf(selection), sellsPawn)
		if err != nil {
			return RoundsTradeResult{}, err
		}
		return r.commit(call, epoch, state, incident, domain.TradeSetLines, 0, trader, value, started)
	}
	// Lines are staged: re-run the same selection over the live sheet and
	// require an exact match, then native's affordability and the silver
	// reserve, before accepting. Any drift cancels.
	if selection.Refused || !sameTradeLines(tradeLinesOf(selection), staged) || !sheet.ColonyCanAfford || !sheet.TraderHasSilver || sheet.DealSignature == "" || !facts.Favor && !sheet.BalanceKnown {
		return r.cancel(call, epoch, state, incident, trader, negotiator, started)
	}
	if !facts.Favor && float64(facts.ColonySilver)+sheet.Balance < float64(selection.SilverReserve) {
		return r.cancel(call, epoch, state, incident, trader, negotiator, started)
	}
	value, err := domain.NewTradeAccept(trader, negotiator, sheet.DealSignature, false, false)
	if err != nil {
		return RoundsTradeResult{}, err
	}
	return r.commit(call, epoch, state, incident, domain.TradeAccept, 0, trader, value, started)
}

// traderStacks is the goods stacks of the trader on the census.
func traderStacks(traders []policy.TraderFacts, id string) int64 {
	for _, t := range traders {
		if t.ID == id {
			return t.GoodsStacks
		}
	}
	return 0
}

// tradeSessionPhase is the next phase of an open session (D1): read
// from the live sheet, not the journal, so a session a save load carried
// over resumes where native holds it. Lines already staged on the sheet go
// to accept; a recorded line staging that failed cancels; otherwise the
// lines are staged.
func tradeSessionPhase(lines tradePhase, staged int) domain.TradeOperationKind {
	switch {
	case lines.found && !lines.open && !lines.completed:
		return domain.TradeEnd
	case staged > 0 || lines.completed:
		return domain.TradeAccept
	}
	return domain.TradeSetLines
}

// selection re-measures the need from a fresh colony read and turns it,
// with the live sheet, into SelectTrade's inputs.
func (r *RoundsTradePlanner) selection(call context.Context, state ControlState, review store.Rounds, sheet bridge.TradeSheetRead, trader string, traders []policy.TraderFacts) (domain.TradeEconomicPolicy, policy.TradeSelectionFacts, bool, error) {
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := r.native.ReadColonyFacts(call, identity, false)
	if err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, fmt.Errorf("%w: selection: observed == nil", ErrControl)
	}
	if err = bridge.ValidateColonyFacts(observed, identity); err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, fmt.Errorf("%w: selection: err != nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, fmt.Errorf("%w: selection: err != nil", ErrControl)
	}
	// Refresh through the same projection and per-tick plan owner used by
	// goal review; a staged trade never relies on a previous tick's need.
	tables, err := r.native.FrameTables(call, identity)
	if err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, err
	}
	items, err := r.reviewer.itemFacts(call, state.Snapshot)
	if err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, err
	}
	medicalFacts, err := observation.ColonyMedicalReserve(observed, tables)
	if err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, err
	}
	medicalFacts.Catalog = items
	medical, err := policy.ReviewMedicalReserve(medicalFacts, review.Latches.MedicalReserve, r.reviewer.policy.MedicalReserve)
	if err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, err
	}
	projection, err := observation.DecodeColony(reply, observation.Identity{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map, Tick: domain.Tick(observed.Context.GetTick())}, tables)
	if err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, err
	}
	projection.Facts.Items = items
	zoneNative, _ := r.native.(observation.ZonesNative)
	if err = observation.FillZones(call, zoneNative, observed.Context.Identity, projection.Identity, &projection); err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, err
	}
	// The food plan reads the caravans on the census as present, the session's
	// priced by the offers just recorded from this sheet.
	projection.Facts.Traders = domain.Known(traders)
	r.reviewer.planFood(&projection)
	seasonal := r.reviewer.seasonal(projection.Facts)
	construction, _, err := r.native.ReadConstructionDeficits(call, identity)
	if err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, err
	}
	if _, err = boundary.Context(construction.Context, state.Snapshot); err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, fmt.Errorf("%w: selection: err != nil", ErrControl)
	}
	floors := policy.RoundsTradeFloors(seasonal, construction.StillNeed)
	// MaintainResource's floors (the wood floor, shortfall edges) are the
	// catalog's trade demand: a caravan selling one buys it.
	targets := r.reviewer.resourceTargets(state.Snapshot)
	// A wealth-driven sale keeps what the review's demand retains:
	// the stock the trade detector held the sale to.
	retained := r.reviewer.demand.get(state.Snapshot).Retained
	// Restore parts no bench can fabricate are bought.
	parts, benches, err := surgeryPartDemand(call, r.native, identity, projection.Facts.MedicalPawns, projection.SurgeryContext())
	if err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, err
	}
	// A harvested organ sells while the silver runway is short.
	saleArt, err := r.saleArt(call, identity, state.Snapshot)
	if err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, err
	}
	// Unreserved art under negative wealth headroom is shed_art.
	headroom := projection.Facts.WealthBudget()
	artCount := domain.Unknown[int64]()
	if saleArt != nil {
		artCount = domain.Known(int64(len(saleArt)))
	}
	// Surplus animals sell while the silver runway is short; the
	// race catalog is the herd plan's, the catalog's race rows the projection
	// decoded.
	if tables.Catalog == nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, fmt.Errorf("%w: selection: no definition catalog", ErrControl)
	}
	rows, err := tradeSheetRowFacts(sheet.Rows, tables.Catalog)
	if err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, err
	}
	// The plan counts the animals this trader offers as obtainable, so its
	// wants (bought below) and its surplus (sold) agree.
	planInput := projection.Facts.HerdPlanInput()
	planInput.Offers = policy.HerdOffers(rows, projection.Facts.AnimalUpkeep.AnimalRaces)
	herd := policy.PlanHerd(planInput)
	saleAnimals := policy.HerdSaleAnimals(projection.Facts.AnimalUpkeep.Animals, herd.Policy)
	need, known := policy.AnimalSaleNeed(projection.Facts.Items, policy.ShedArtNeed(policy.SurgeryTradeNeed(policy.ReserveSurgeryStock(policy.OrganSaleSurplus(projection.Facts.Items, policy.ExportSaleSurplus(projection.Facts.Items, policy.ReviewTradeNeed(projection.Facts.Items, medical, medicalFacts.Resources, targets, floors, projection.Facts.Wealth, retained, policy.RoundsTradeFood(projection.Facts, seasonal)), medicalFacts.Resources, projection.Facts.Colonists, r.reviewer.exports.products(state.Snapshot)), medicalFacts.Resources, projection.Facts.Colonists, projection.Facts.Recipes), projection.Facts.MedicalPawns), policy.SurgeryPurchaseParts(projection.Facts.MedicalPawns, projection.SurgeryContext(), parts, policy.FabricableParts(benches))), headroom, artCount), saleAnimals, projection.Facts.Silver(), projection.Facts.Colonists).Value()
	if !known {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, fmt.Errorf("%w: selection: !known", ErrControl)
	}
	// A resource is bought only as far as the supply plan opened this
	// trader's offer for it.
	planned, err := r.plannedPurchases(call, state, review, trader)
	if err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, err
	}
	need = restrictToPlan(need, planned, policy.PlannedTradeNutrition(projection.Facts.FoodPlan, trader))
	economic := policy.RoundsTradeTargets(projection.Facts.Items, need, rows, targets, projection.Facts.Colonists)
	facts := policy.TradeSelectionFacts{Complete: true, Rows: rows, Floors: floors, CropSurplusFloors: policy.CropSurplusFloors(need)}
	facts.ColonySilver, facts.TraderSilver, facts.SilverKnown = tradeSheetSilver(sheet.Rows)
	facts.MaxSilverSpend = max(0, facts.ColonySilver)
	facts.Favor, facts.FavorKeep = sheet.FavorCurrency, policy.FavorGoldKeep(targets, floors, retained)
	facts.SaleArt = saleArt
	if facts.Favor {
		if facts.FavorPrisoners, err = r.favorPrisoners(call, state, review); err != nil {
			return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, err
		}
	}
	// Warehouse gear below the keep floors sells.
	claims, err := r.reviewer.player.journal.ZoneClaims(call, state.Snapshot, projection.Identity.Tick)
	if err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, err
	}
	warehouses := map[string]bool{}
	owned, _ := claims.Value()
	for _, z := range owned {
		if z.Role == domain.GeneralRole || strings.HasPrefix(z.Role, domain.GeneralRole+":") {
			warehouses[z.ID] = true
		}
	}
	facts.SaleGear = policy.SaleGear(rows, warehouses)
	// While the silver gap is open, gear above demand sells too, cheapest
	// first until the prices cover the gap (MaintainTrade's sale path).
	if gap, known := policy.SilverGap(projection.Facts.Items, domain.Known(need), projection.Facts.Silver(), projection.Facts.Colonists).Value(); known && gap > 0 {
		if surplus, ok := r.reviewer.exports.surplus(state.Snapshot); ok {
			for id := range policy.SaleGearSurplus(rows, warehouses, surplus, gap) {
				facts.SaleGear[id] = true
			}
		}
	}
	facts.HerdWants = append(policy.HerdWants(herd), policy.PlannedAnimalPurchases(projection.Facts.FoodPlan, trader)...)
	if need.SurplusAnimals > 0 {
		facts.SaleAnimals = make(map[string]bool, len(saleAnimals))
		for id := range saleAnimals {
			facts.SaleAnimals[string(id)] = true
		}
	}
	h, hk := headroom.Value()
	facts.ArtFirst = hk && h < 0
	capacity, _ := policy.JoinerCapacity(projection.Facts.JoinerCapacity()).Value()
	return economic, facts, capacity, nil
}

// favorPrisoners reads the prisoner census through the routine reading (the
// one the negotiator choice uses) and returns the surplus prisoners a favor
// session sells.
func (r *RoundsTradePlanner) favorPrisoners(call context.Context, state ControlState, review store.Rounds) (map[string]bool, error) {
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return nil, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return nil, fmt.Errorf("%w: favorPrisoners: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return nil, err
	}
	short, _ := policy.RoundsSilverShort(read.Projection.Facts, r.reviewer.policy, review.Latches.MedicalReserve).Value()
	return read.Projection.Facts.SurplusPrisoners(short), nil
}

func (r *RoundsTradePlanner) cancel(call, epoch context.Context, state ControlState, incident store.IncidentState, trader string, negotiator domain.PawnID, started time.Time) (RoundsTradeResult, error) {
	value, err := domain.NewTradeEnd(trader, negotiator, domain.TradeEndCancel, false)
	if err != nil {
		return RoundsTradeResult{}, err
	}
	return r.commit(call, epoch, state, incident, domain.TradeEnd, 0, trader, value, started)
}

// commit records one phase as the caravan's fixed incident method: a plan of
// the trade action alone.
func (r *RoundsTradePlanner) commit(call, epoch context.Context, state ControlState, incident store.IncidentState, kind domain.TradeOperationKind, attempt int, trader string, value domain.Trade, started time.Time) (RoundsTradeResult, error) {
	p := r.reviewer.player
	method := tradeMethod(kind, trader, attempt)
	id := domain.MintPlanID()
	tradeID := domain.ActionID(fmt.Sprintf("%s-trade", id))
	if kind != domain.TradeOpen {
		session, _, readErr := r.native.ReadTradeSession(call, boundary.Identity(state.Snapshot))
		if readErr != nil {
			return RoundsTradeResult{}, readErr
		}
		if session.Trader == trader && session.Target != nil {
			var err error
			value, err = value.WithParticipant(bridge.TradeParticipantOf(session.Target))
			if err != nil {
				return RoundsTradeResult{}, err
			}
		}
	}
	action, err := domain.NewTradeAction(tradeID, value)
	if err != nil {
		return RoundsTradeResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsTradeResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsTradeResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsTradeResult{}, fmt.Errorf("%w: commit: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitIncidentMethod(call, incident.Incident.ID, method, "", plan); err != nil {
		return RoundsTradeResult{}, err
	}
	// An accept or end settles the caravan: its offers no longer describe a
	// session the plan can use.
	if kind == domain.TradeAccept || kind == domain.TradeEnd {
		r.reviewer.tradeOffers.drop(trader)
	}
	// The next phase lands in the stop after this window: a session left
	// open under a full window outlasts the caravan's visit.
	var ticks uint32
	if kind != domain.TradeEnd {
		ticks = tradeArrivalTicks
	}
	return RoundsTradeResult{Verdict: BuildingReasonAdmitted, Plan: id, Trader: trader, Phase: kind, NativeWorkTicks: ticks}, nil
}

// tradeFoodFact classifies a food line from the catalog's rows; native
// supplies only the nutrition stat.
func tradeFoodFact(food *o.TradeFoodFacts, def string, catalog *bridge.DefinitionCatalog) (domain.Fact[policy.TradeFoodGood], error) {
	if food == nil {
		return domain.Unknown[policy.TradeFoodGood](), nil
	}
	good, ok, err := catalog.TradeFood(def)
	if err != nil || !ok {
		return domain.Unknown[policy.TradeFoodGood](), err
	}
	good.Nutrition = food.Nutrition
	return domain.Known(good), nil
}

func tradeSheetRowFacts(rows []bridge.TradeSheetRow, catalog *bridge.DefinitionCatalog) ([]policy.TradeSheetRowFact, error) {
	out := make([]policy.TradeSheetRowFact, 0, len(rows))
	for _, row := range rows {
		food, err := tradeFoodFact(row.Food, row.DefName, catalog)
		if err != nil {
			return nil, err
		}
		out = append(out, policy.TradeSheetRowFact{
			Food:   food,
			LineID: row.LineID, DefName: row.DefName, ColonyCount: row.ColonyCount, TraderCount: row.TraderCount,
			BuyPrice: row.BuyPrice, BuyPriceKnown: row.BuyPriceKnown, SellPrice: row.SellPrice, SellPriceKnown: row.SellPriceKnown,
			TraderWillTrade: row.TraderWillTrade, TraderWillTradeKnown: row.TraderWillTradeKnown,
			Currency: row.Currency, CurrencyKnown: row.CurrencyKnown, Pawn: row.Pawn, PawnKnown: row.PawnKnown,
			ThingID: row.ThingID, Stuff: row.Stuff, HitPoints: row.HitPoints, HitPointsKnown: row.HitPointsKnown, Quality: row.Quality, QualityKnown: row.QualityKnown, ZoneID: row.ZoneID, PawnID: row.PawnID, PawnGender: row.PawnGender,
			Skills: tradePawnSkills(row.Skills), ViolenceCapable: row.ViolenceCapable, ViolenceCapableKnown: row.ViolenceCapableKnown,
			GuestStatus: row.GuestStatus, PrisonerSecure: row.PrisonerSecure, PrisonerSecureKnown: row.PrisonerSecureKnown, PawnDowned: row.PawnDowned, PawnDownedKnown: row.PawnDownedKnown,
			ExtraHomeFaction: row.ExtraHomeFaction, ExtraHostFaction: row.ExtraHostFaction,
		})
	}
	return out, nil
}

func tradePawnSkills(skills []bridge.TradeSheetSkill) []policy.ProfileSkill {
	var out []policy.ProfileSkill
	for _, skill := range skills {
		out = append(out, policy.ProfileSkill{Name: skill.Name, Level: int(skill.Level), Passion: skill.Passion, Disabled: skill.Disabled})
	}
	return out
}

// tradeSheetSilver reads both sides' current silver from the sheet's own
// currency row.
func tradeSheetSilver(rows []bridge.TradeSheetRow) (int64, int64, bool) {
	for _, row := range rows {
		if row.CurrencyKnown && row.Currency {
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

// tradeStagedLines reads the non-currency rows the live sheet has staged,
// in sheet order, for comparison against a fresh selection.
func tradeStagedLines(sheet bridge.TradeSheetRead) []domain.TradeLine {
	var out []domain.TradeLine
	for _, row := range sheet.Rows {
		if row.TransferNow != 0 && !(row.CurrencyKnown && row.Currency) {
			out = append(out, domain.TradeLine{LineID: row.LineID, AbsoluteCount: int32(row.TransferNow)})
		}
	}
	return out
}

// sameTradeLines compares two line sets regardless of order: the sheet
// stages rows in sheet order, the selection in target order with any pawn
// buy last.
func sameTradeLines(left, right []domain.TradeLine) bool {
	if len(left) != len(right) {
		return false
	}
	counts := map[domain.TradeLine]int{}
	for i := range left {
		counts[left[i]]++
		counts[right[i]]--
	}
	for _, n := range counts {
		if n != 0 {
			return false
		}
	}
	return true
}
