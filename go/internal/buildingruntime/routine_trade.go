package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"slices"
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

// RoutineTradeSource is the native census RoutineTradePlanner reads between
// phases: the trader census for the caravan and negotiator to open with, the
// trade-session read for the walk or session native holds, a
// fresh colony facts read for the medicine and resource stock the need is
// re-measured from, and the session-scoped sheet each decision is made
// from. A phase is not previewed: native judges it when it applies, and a
// refusal fails its method.
type RoutineTradeSource interface {
	ReadConstructionDeficits(context.Context, *c.Identity) (bridge.ConstructionDeficitRead, bridge.Result, error)
	ReadColonyFacts(context.Context, *c.Identity, bool) (*o.ColonyFactsReply, bridge.Result, error)
	FrameTables(context.Context, *c.Identity) (bridge.Tables, error)
	ListTraders(context.Context, *c.Identity) (bridge.TradersRead, bridge.Result, error)
	ReadTradeSession(context.Context, *c.Identity) (bridge.TradeSessionRead, bridge.Result, error)
	ReadTradeSheet(context.Context, *c.Identity) (bridge.TradeSheetRead, bridge.Result, error)
}

// RoutineTradePlanner drives TradeWithCaravan (#234) one phase edge per
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
// caravan and TradeWithCaravan occurrence (#1078) the method ids are fixed, so a restart resumes
// where it left off. A caravan whose session reached accept or cancel, or
// whose last open attempt ended without a session, is settled for the
// occurrence and never reopened: it closes when the caravan leaves or
// the next review measures nothing left to trade.
//
// Adjacency is native's: OpenTrade orders vanilla's TradeWithPawn job, which
// walks the negotiator after the trader, and native opens the session where
// that job would open its dialog. While the session read shows the walk, the
// routine lends the clock a short window; a walk the game interrupted reads
// as neither walk nor session, and the open is sent again. Once open, the
// session binds its participants and the remaining phases need no escort.
type RoutineTradePlanner struct {
	reviewer *RoutineReviewer
	native   RoutineTradeSource
}
type RoutineTradeResult struct {
	Reason RoutineBuildingReason
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
const tradeArrivalTicks = 2500

// tradeWalkTicks is the window lent while a negotiator walks to open a
// session, after which the session is read again.
const tradeWalkTicks = 250

func NewRoutineTradePlanner(reviewer *RoutineReviewer, native RoutineTradeSource) (*RoutineTradePlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineTradePlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoutineTradePlanner{reviewer, native}, nil
}

// tradeMethod is the fixed method id of one caravan's phase attempt.
func tradeMethod(kind domain.TradeOperationKind, trader string, attempt int) domain.MethodID {
	digest := sha256.Sum256([]byte(trader))
	return domain.MethodID(fmt.Sprintf("trade-%s-%x-%d", kind, digest[:8], attempt))
}

// tradeAttempts bounds how often a phase is retried after a failure. An
// open ends without a session when the world moves under the walk -- the
// trader wandering off as the negotiator arrives, a path that could not
// complete -- and is worth another walk; a refused line staging, accept or
// end is not.
func tradeAttempts(kind domain.TradeOperationKind) int {
	if kind == domain.TradeOpen {
		return 3
	}
	return 1
}

// tradePhase is one caravan's latest recorded attempt at a phase and the
// settled stage of its trade action: open reports in-flight work, and a
// terminal stage other than Completed is a failure of the attempt.
type tradePhase struct {
	found, open, completed bool
	attempt                int
}

func (r *RoutineTradePlanner) failed(p tradePhase) bool { return p.found && !p.open && !p.completed }

func (r *RoutineTradePlanner) phase(ctx context.Context, incident store.IncidentState, kind domain.TradeOperationKind, trader string) (tradePhase, error) {
	out := tradePhase{}
	for attempt := 0; attempt < tradeAttempts(kind); attempt++ {
		want := tradeMethod(kind, trader, attempt)
		i := slices.IndexFunc(incident.Methods, func(m store.IncidentMethod) bool { return m.Method == want })
		if i < 0 {
			break
		}
		plan, err := r.reviewer.player.journal.LoadPlan(ctx, incident.Methods[i].Plan)
		if err != nil {
			return tradePhase{}, err
		}
		found := false
		for _, progress := range plan.Progress {
			if _, ok := progress.Action().Trade(); ok {
				out = tradePhase{found: true, open: store.PlanOpen(plan), completed: progress.View().Stage == domain.Completed, attempt: attempt}
				found = true
			}
		}
		if !found {
			return tradePhase{}, fmt.Errorf("%w: phase: !found", ErrControl)
		}
	}
	return out, nil
}

// tradeSettled reports whether the caravan's session is over for this occurrence:
// its last open attempt ended with no session or walk left, its accept
// applied or was refused, or an end phase exists and is no longer open.
// engaged is whether native holds a walk or session with the trader.
func (r *RoutineTradePlanner) tradeSettled(ctx context.Context, incident store.IncidentState, trader string, engaged bool) (bool, error) {
	open, err := r.phase(ctx, incident, domain.TradeOpen, trader)
	if err != nil {
		return false, err
	}
	if tradeOpenSpent(open, engaged) && open.attempt+1 >= tradeAttempts(domain.TradeOpen) {
		return true, nil
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

// tradeAcceptSpent reports an accept that is over, applied or refused
// (#1156): native closes the session either way, so the caravan is settled
// for the occurrence and the routine replans rather than reopening it.
func tradeAcceptSpent(accept tradePhase) bool { return accept.found && !accept.open }

// tradeOpenSpent reports an open attempt that is over without a session
// or walk to show for it: refused, or applied and since ended.
func tradeOpenSpent(open tradePhase, engaged bool) bool {
	return open.found && !open.open && (!open.completed || !engaged)
}

func (r *RoutineTradePlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineTradeResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineTradeResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineTradeResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineTradeResult{Reason: BuildingMethodNoReview}, nil
	}
	incident, found, err := incidentDeficit(call, p.journal, review, policy.TradeWithCaravan)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	if !found {
		return RoutineTradeResult{Reason: BuildingMethodNoDeficit}, nil
	}
	if open, err := incidentOpenWork(call, p.journal, incident); err != nil || open {
		return RoutineTradeResult{Reason: BuildingMethodExistingWork}, err
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	census, _, err := r.native.ListTraders(call, identity)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	if _, err = boundary.Context(census.Context, state.Snapshot); err != nil || census.Context.GetTick() < int64(review.Tick) {
		return RoutineTradeResult{}, fmt.Errorf("%w: step: err != nil || census.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	session, _, err := r.native.ReadTradeSession(call, identity)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	if _, err = boundary.Context(session.Context, state.Snapshot); err != nil || session.Context.GetTick() < int64(review.Tick) {
		return RoutineTradeResult{}, fmt.Errorf("%w: step: err != nil || session.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	// A caravan native holds a session or walk with is driven first,
	// tradeable or not: a session native still holds must be settled, never
	// left, and a walk only game time finishes.
	traders := make([]policy.TraderFacts, 0, len(census.Traders))
	arriving := false
	for _, row := range census.Traders {
		arriving = arriving || row.Travelling
		traders = append(traders, policy.TraderFacts{ID: row.ID, Kind: row.Kind, Faction: row.Faction, CanTrade: row.CanTrade, Travelling: row.Travelling, GoodsStacks: int64(row.GoodsStacks)})
		if row.ID != session.Trader {
			continue
		}
		settled, err := r.tradeSettled(call, incident, row.ID, true)
		if err != nil {
			return RoutineTradeResult{}, err
		}
		if settled {
			continue
		}
		if !session.Open {
			return RoutineTradeResult{Reason: BuildingMethodExistingWork, Trader: row.ID, Phase: domain.TradeOpen, NativeWorkTicks: tradeWalkTicks}, nil
		}
		return r.drive(call, epoch, state, incident, review, row.ID, domain.PawnID(session.Negotiator), started)
	}
	settled := map[string]bool{}
	waiting := arriving
	for _, row := range census.Traders {
		if settled[row.ID], err = r.tradeSettled(call, incident, row.ID, row.ID == session.Trader); err != nil {
			return RoutineTradeResult{}, err
		}
		// A settled caravan is waited out: the occurrence closes when it
		// leaves, and only game time takes it away.
		waiting = waiting || (settled[row.ID] && row.CanTrade)
	}
	trader, ok := policy.SelectTrader(traders, settled)
	if !ok {
		if waiting {
			return RoutineTradeResult{Reason: BuildingMethodRefused, NativeWorkTicks: tradeArrivalTicks}, nil
		}
		return RoutineTradeResult{Reason: BuildingMethodUsed}, nil
	}
	if len(census.Negotiators) == 0 {
		return RoutineTradeResult{Reason: BuildingMethodUnknown}, nil
	}
	negotiator, err := r.negotiator(call, state, review, census.Negotiators)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	open, err := r.phase(call, incident, domain.TradeOpen, trader.ID)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	attempt := 0
	if open.found {
		attempt = open.attempt + 1
	}
	return r.open(call, epoch, state, incident, trader.ID, negotiator, attempt, arbiter, started)
}

// bid posts each purchase on the joint acquisition board (#728), so the
// resource and acquisition planners leave a resource the caravan is
// selling cheaper than their best method.
func (r *RoutineTradePlanner) bid(state ControlState, trader string, selection policy.TradeSelection, rows []policy.TradeSheetRowFact, tick domain.Tick) {
	price := map[string]float64{}
	for _, row := range rows {
		if row.BuyPriceKnown {
			price[row.DefName] = row.BuyPrice
		}
	}
	for _, line := range selection.Selected {
		resource := policy.Resource(line.DefName)
		candidate, ok := policy.TradeCandidate(resource, trader, line.Count, price[line.DefName])
		if !ok {
			continue
		}
		ranked, err := policy.RankResourceCandidates(policy.ResourceDeficitDemand(resource, line.Count), []policy.AcquisitionCandidate{candidate}, policy.AcquisitionCompetition{})
		if err != nil || len(ranked) == 0 {
			continue
		}
		r.reviewer.bids.bid(state.Snapshot, resource, bidTrade, ranked[0].Score, policy.AcquisitionTrade, tick)
	}
}

// negotiator picks the colonist to open with: policy.TraderFor over the
// roster profiles, restricted to the pawns native listed as eligible
// (alive, undrafted, able to talk). Native's own first row (best trade
// price improvement) stands when the roster is unknown or no profile
// qualifies -- native has already vetted every listed row.
func (r *RoutineTradePlanner) negotiator(call context.Context, state ControlState, review store.RoutineReview, eligible []bridge.NegotiatorRead) (bridge.NegotiatorRead, error) {
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return bridge.NegotiatorRead{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return bridge.NegotiatorRead{}, fmt.Errorf("%w: negotiator: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
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
func (r *RoutineTradePlanner) open(call, epoch context.Context, state ControlState, incident store.IncidentState, trader string, negotiator bridge.NegotiatorRead, attempt int, arbiter *stepArbiter, started time.Time) (RoutineTradeResult, error) {
	if !arbiter.tryClaim(nil, "pawn:"+negotiator.ID) {
		return RoutineTradeResult{Reason: BuildingMethodUsed}, nil
	}
	value, err := domain.NewTradeOpen(trader, domain.PawnID(negotiator.ID), false)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	return r.commit(call, epoch, state, incident, domain.TradeOpen, attempt, trader, value, started)
}

// drive advances one caravan's open session: stage the selected lines from
// its sheet, confirm and accept them, or cancel.
func (r *RoutineTradePlanner) drive(call, epoch context.Context, state ControlState, incident store.IncidentState, review store.RoutineReview, trader string, negotiator domain.PawnID, started time.Time) (RoutineTradeResult, error) {
	lines, err := r.phase(call, incident, domain.TradeSetLines, trader)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	end, err := r.phase(call, incident, domain.TradeEnd, trader)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	if end.found {
		// An end that failed leaves native holding the session; there is
		// nothing further to propose, and tradeSettled already reads it as
		// over.
		return RoutineTradeResult{Reason: BuildingMethodExhausted, Trader: trader, Phase: domain.TradeEnd}, nil
	}
	accept, err := r.phase(call, incident, domain.TradeAccept, trader)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	if r.failed(accept) {
		return r.cancel(call, epoch, state, incident, trader, negotiator, started)
	}
	identity := boundary.Identity(state.Snapshot)
	sheet, _, err := r.native.ReadTradeSheet(call, identity)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	if _, err = boundary.Context(sheet.Context, state.Snapshot); err != nil {
		return RoutineTradeResult{}, fmt.Errorf("%w: drive: err != nil", ErrControl)
	}
	if sheet.Trader != trader || sheet.Negotiator != string(negotiator) || sheet.GiftMode || !sheet.CanTradeNow {
		return r.cancel(call, epoch, state, incident, trader, negotiator, started)
	}
	staged := tradeStagedLines(sheet)
	phase := tradeSessionPhase(lines, len(staged))
	if phase == domain.TradeEnd {
		return r.cancel(call, epoch, state, incident, trader, negotiator, started)
	}
	economic, facts, capacity, err := r.selection(call, state, review, sheet)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	selection := policy.SelectTrade(economic, facts)
	if len(economic.Targets) == 0 && len(facts.SaleArt) == 0 {
		// Nothing to buy or sell by the resource catalog: only a pawn
		// purchase can still stage, against the same silver reserve.
		selection = policy.TradeSelection{SilverReserve: max(economic.SilverReserve, facts.Floors["Silver"])}
	}
	clockSchedulerLog("trade selection: phase=%v sale_art=%v refused=%v reason=%q selected=%+v evidence=%+v trader_silver=%d", phase, facts.SaleArt, selection.Refused, selection.Reason, selection.Selected, selection.Evidence, facts.TraderSilver)
	r.bid(state, trader, selection, facts.Rows, review.Tick)
	// A pawn buy is its own line beside the resource lines (#1037).
	if !selection.Refused && facts.SilverKnown {
		if pawn, ok := policy.SelectPawnPurchase(capacity, facts.Rows, facts.ColonySilver, selection.SilverReserve, selection.Selected); ok {
			selection.Selected = append(selection.Selected, pawn)
		}
	}
	if phase == domain.TradeSetLines {
		if selection.Refused || len(selection.Selected) == 0 {
			return r.cancel(call, epoch, state, incident, trader, negotiator, started)
		}
		value, err := domain.NewTradeSetLines(trader, negotiator, tradeLinesOf(selection), false)
		if err != nil {
			return RoutineTradeResult{}, err
		}
		return r.commit(call, epoch, state, incident, domain.TradeSetLines, 0, trader, value, started)
	}
	// Lines are staged: re-run the same selection over the live sheet and
	// require an exact match, then native's affordability and the silver
	// reserve, before accepting. Any drift cancels.
	if selection.Refused || !sameTradeLines(tradeLinesOf(selection), staged) || !sheet.BalanceKnown || !sheet.ColonyCanAfford || !sheet.TraderHasSilver || sheet.DealSignature == "" {
		return r.cancel(call, epoch, state, incident, trader, negotiator, started)
	}
	if float64(facts.ColonySilver)+sheet.Balance < float64(selection.SilverReserve) {
		return r.cancel(call, epoch, state, incident, trader, negotiator, started)
	}
	floors := tradeAcceptFloors(economic, selection)
	value, err := domain.NewTradeAccept(trader, negotiator, sheet.DealSignature, floors, false, false)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	return r.commit(call, epoch, state, incident, domain.TradeAccept, 0, trader, value, started)
}

// tradeSessionPhase is the next phase of an open session (#999, D1): read
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
func (r *RoutineTradePlanner) selection(call context.Context, state ControlState, review store.RoutineReview, sheet bridge.TradeSheetRead) (domain.TradeEconomicPolicy, policy.TradeSelectionFacts, bool, error) {
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
	medicalFacts := medicalReserveObservationFacts(observed)
	medical, err := policy.ReviewMedicalReserve(medicalFacts, review.Latches.MedicalReserve, r.reviewer.policy.MedicalReserve)
	if err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, err
	}
	// Refresh through the same projection and per-tick plan owner used by
	// goal review; a staged trade never relies on a previous tick's need.
	tables, err := r.native.FrameTables(call, identity)
	if err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, err
	}
	projection, err := observation.DecodeColony(reply, observation.Identity{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map, Tick: domain.Tick(observed.Context.GetTick())}, tables)
	if err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, err
	}
	zoneNative, _ := r.native.(observation.ZonesNative)
	if err = observation.FillZones(call, zoneNative, observed.Context.Identity, projection.Identity, &projection); err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, err
	}
	projection.Facts.FoodPlan = r.reviewer.planFood(projection)
	seasonal := r.reviewer.seasonal(projection.Facts)
	construction, _, err := r.native.ReadConstructionDeficits(call, identity)
	if err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, err
	}
	if _, err = boundary.Context(construction.Context, state.Snapshot); err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, fmt.Errorf("%w: selection: err != nil", ErrControl)
	}
	floors := policy.RoutineTradeFloors(seasonal, construction.StillNeed)
	// MaintainResource's floors (the wood floor, shortfall edges) are the
	// catalog's trade demand (#728): a caravan selling one buys it.
	targets, err := r.reviewer.resourceTargets(call, state.Snapshot, projection.Facts.Resources)
	if err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, err
	}
	targets = policy.ResourceGoalTargets(targets, seasonal.ResourceTargets)
	// Restore parts no bench can fabricate are bought (#1168).
	parts, benches, err := surgeryPartDemand(call, r.native, identity, projection.Facts.MedicalPawns)
	if err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, err
	}
	// A harvested organ sells while the silver runway is short (#1169).
	saleArt, err := r.saleArt(call, identity, state.Snapshot)
	if err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, err
	}
	// Unreserved art under negative wealth headroom is shed_art (#1247).
	headroom := projection.Facts.WealthBudget()
	artCount := domain.Unknown[int64]()
	if saleArt != nil {
		artCount = domain.Known(int64(len(saleArt)))
	}
	need, known := policy.ShedArtNeed(policy.SurgeryTradeNeed(policy.ReserveSurgeryStock(policy.OrganSaleSurplus(policy.ReviewTradeNeed(medical, medicalFacts.Resources, targets, floors, projection.Facts.Wealth, seasonal.Trade, policy.RoutineTradeFood(projection.Facts, seasonal)), medicalFacts.Resources, projection.Facts.Colonists), projection.Facts.MedicalPawns), policy.TradeSurgeryParts(parts, policy.FabricableParts(benches))), headroom, artCount).Value()
	if !known {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, false, fmt.Errorf("%w: selection: !known", ErrControl)
	}
	rows := tradeSheetRowFacts(sheet.Rows)
	economic := policy.RoutineTradeTargets(need, rows, policy.ResourceGoalTargets(targets, r.reviewer.policy.ResourceTargets), r.reviewer.policy.Trade, projection.Facts.Colonists)
	facts := policy.TradeSelectionFacts{Complete: true, Rows: rows, Floors: floors, CropSurplusFloors: policy.CropSurplusFloors(need)}
	facts.ColonySilver, facts.TraderSilver, facts.SilverKnown = tradeSheetSilver(sheet.Rows)
	facts.MaxSilverSpend = max(0, facts.ColonySilver)
	facts.SaleArt = saleArt
	h, hk := headroom.Value()
	facts.ArtFirst = hk && h < 0
	capacity, _ := policy.JoinerCapacity(projection.Facts.JoinerCapacity()).Value()
	return economic, facts, capacity, nil
}

func (r *RoutineTradePlanner) cancel(call, epoch context.Context, state ControlState, incident store.IncidentState, trader string, negotiator domain.PawnID, started time.Time) (RoutineTradeResult, error) {
	value, err := domain.NewTradeEnd(trader, negotiator, domain.TradeEndCancel, false)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	return r.commit(call, epoch, state, incident, domain.TradeEnd, 0, trader, value, started)
}

// commit records one phase as the caravan's fixed incident method: a plan of
// the trade action alone.
func (r *RoutineTradePlanner) commit(call, epoch context.Context, state ControlState, incident store.IncidentState, kind domain.TradeOperationKind, attempt int, trader string, value domain.Trade, started time.Time) (RoutineTradeResult, error) {
	p := r.reviewer.player
	method := tradeMethod(kind, trader, attempt)
	id := domain.MintPlanID()
	tradeID := domain.ActionID(fmt.Sprintf("%s-trade", id))
	action, err := domain.NewTradeAction(tradeID, value)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineTradeResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineTradeResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineTradeResult{}, fmt.Errorf("%w: commit: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitIncidentMethod(call, incident.Incident.ID, method, "", plan); err != nil {
		return RoutineTradeResult{}, err
	}
	// The next phase lands in the stop after this window: a session left
	// open under a full window outlasts the caravan's visit (#1195).
	var ticks uint32
	if kind != domain.TradeEnd {
		ticks = tradeArrivalTicks
	}
	return RoutineTradeResult{Reason: BuildingMethodAdmitted, Plan: id, Trader: trader, Phase: kind, NativeWorkTicks: ticks}, nil
}

func tradeFoodFact(food *o.TradeFoodFacts) domain.Fact[policy.TradeFoodGood] {
	if food == nil {
		return domain.Unknown[policy.TradeFoodGood]()
	}
	var class policy.FoodIngredientClass
	switch food.IngredientClass {
	case o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_MEAT:
		class = policy.IngredientMeat
	case o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_VEGETABLE:
		class = policy.IngredientVegetable
	case o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_ANIMAL_PRODUCT:
		class = policy.IngredientAnimalProduct
	case o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_ANY:
		class = policy.IngredientAny
	default:
		return domain.Unknown[policy.TradeFoodGood]()
	}
	return domain.Known(policy.TradeFoodGood{Nutrition: food.Nutrition, Class: class, Prepared: food.Prepared, NonPerishable: food.NonPerishable, Crop: food.Crop})
}

func tradeSheetRowFacts(rows []bridge.TradeSheetRow) []policy.TradeSheetRowFact {
	out := make([]policy.TradeSheetRowFact, 0, len(rows))
	for _, row := range rows {
		out = append(out, policy.TradeSheetRowFact{
			Food:   tradeFoodFact(row.Food),
			LineID: row.LineID, DefName: row.DefName, ColonyCount: row.ColonyCount, TraderCount: row.TraderCount,
			BuyPrice: row.BuyPrice, BuyPriceKnown: row.BuyPriceKnown, SellPrice: row.SellPrice, SellPriceKnown: row.SellPriceKnown,
			TraderWillTrade: row.TraderWillTrade, TraderWillTradeKnown: row.TraderWillTradeKnown,
			Currency: row.Currency, CurrencyKnown: row.CurrencyKnown, Pawn: row.Pawn, PawnKnown: row.PawnKnown,
			ProtectedExport: row.ProtectedExport, ProtectedExportKnown: row.ProtectedExportKnown, ThingID: row.ThingID,
			Skills: tradePawnSkills(row.Skills), ViolenceCapable: row.ViolenceCapable, ViolenceCapableKnown: row.ViolenceCapableKnown,
		})
	}
	return out
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

// tradeAcceptFloors builds AcceptTrade's reserve guards: every sold
// definition's retained target, plus the silver reserve. Native checks
// floors only on rows the colony gives, so purchases carry none.
func tradeAcceptFloors(p domain.TradeEconomicPolicy, selection policy.TradeSelection) []domain.TradeEconomicFloor {
	stock := map[string]int64{}
	for _, target := range p.Targets {
		stock[target.Item] = target.Stock
	}
	for _, evidence := range selection.Evidence {
		if evidence.Matched {
			stock[evidence.Item] = max(stock[evidence.Item], evidence.RetainedTarget)
		}
	}
	out := make([]domain.TradeEconomicFloor, 0, len(selection.Selected)+1)
	for _, line := range selection.Selected {
		if line.Count < 0 {
			out = append(out, domain.TradeEconomicFloor{DefName: line.DefName, Count: int32(stock[line.DefName])})
		}
	}
	return append(out, domain.TradeEconomicFloor{DefName: "Silver", Count: int32(selection.SilverReserve)})
}
