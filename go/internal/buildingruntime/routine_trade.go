package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// RoutineTradeSource is the native census RoutineTradePlanner reads between
// phases: the trader census for the caravan and negotiator to open with, a
// fresh colony facts read for the medicine and resource stock the need is
// re-measured from, the session-scoped sheet each decision is made from,
// and native's own previews of the session phases (acceptance, not
// authority; the executor re-previews at dispatch).
type RoutineTradeSource interface {
	ReadConstructionDeficits(context.Context, *c.Identity) (bridge.ConstructionDeficitRead, bridge.Result, error)
	ReadColonyFacts(context.Context, *c.Identity, bool, []string) (*o.ColonyFactsReply, bridge.Result, error)
	ListTraders(context.Context, *c.Identity) (bridge.TradersRead, bridge.Result, error)
	ReadTradeSheet(context.Context, *c.Identity, string) (bridge.TradeSheetRead, bridge.Result, error)
	PreviewSetTradeLines(context.Context, *c.Identity, string, string, []bridge.TradeLineInput, bool) (*op.PreviewReply, bridge.Result, error)
	PreviewEndTrade(context.Context, *c.Identity, string, string, op.EndTradeKind, bool) (*op.PreviewReply, bridge.Result, error)
}

// RoutineTradePlanner drives TradeWithCaravan (#234) one phase edge per
// step. RimWorld's trade sheet is session-scoped: the line ids and counts a
// SetTradeLines action carries do not exist until Open has run, and the
// deal signature Accept requires does not exist until the lines are staged.
// A committed plan is immutable, so each phase is its own goal method --
// open, then set lines or cancel, then accept or cancel -- bound to the
// Open action's recorded session through store.CommitTradeGoalMethod. Per
// caravan and goal epoch the method ids are fixed, so the phase a caravan
// is in is read back from the goal's own methods and a restart resumes
// where it left off. A caravan whose session reached accept or cancel, or
// whose open failed, is settled for the epoch and never reopened: the
// goal recovers when the caravan leaves or the next review measures
// nothing left to trade.
//
// Adjacency is native's: OpenTrade walks the negotiator to the trader (a
// Goto that tracks the trader's wandering) and opens the session the tick
// it arrives, so the open plan is the trade action alone and stays pending
// for the walk. Once open, the session binds its participants and the
// remaining phases need no escort.
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

func NewRoutineTradePlanner(reviewer *RoutineReviewer, native RoutineTradeSource) (*RoutineTradePlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineTradePlanner{reviewer, native}, nil
}
func (r *RoutineTradePlanner) Step(ctx context.Context) (RoutineTradeResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

// tradeMethod is the fixed method id of one caravan's phase attempt.
func tradeMethod(kind domain.TradeOperationKind, trader string, attempt int) domain.MethodID {
	digest := sha256.Sum256([]byte(trader))
	return domain.MethodID(fmt.Sprintf("trade-%s-%x-%d", kind, digest[:8], attempt))
}

// tradeAttempts bounds how often a phase is retried after a failure. An
// open fails on the world moving under the walk -- the trader wandering
// off as the negotiator arrives, a path that could not complete -- and is
// worth another walk; a refused line staging, accept or end is not.
func tradeAttempts(kind domain.TradeOperationKind) int {
	if kind == domain.TradeOpen {
		return 3
	}
	return 1
}

// tradePhase is one caravan's latest recorded attempt at a phase and the
// settled stage of its trade action: open reports in-flight work, a
// terminal stage other than Completed is a failure of the attempt, and
// retry reports a further attempt is allowed.
type tradePhase struct {
	found, open, completed, retry bool
	attempt                       int
	action                        domain.ActionID
}

func (r *RoutineTradePlanner) failed(p tradePhase) bool { return p.found && !p.open && !p.completed }

func (r *RoutineTradePlanner) phase(ctx context.Context, goal store.GoalState, kind domain.TradeOperationKind, trader string) (tradePhase, error) {
	out := tradePhase{}
	for attempt := 0; attempt < tradeAttempts(kind); attempt++ {
		method, err := r.reviewer.player.journal.LoadGoalMethod(ctx, goal.Goal.ID, goal.Goal.Epoch, tradeMethod(kind, trader, attempt))
		if errors.Is(err, store.ErrNotFound) {
			break
		}
		if err != nil {
			return tradePhase{}, err
		}
		plan, err := r.reviewer.player.journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return tradePhase{}, err
		}
		found := false
		for _, progress := range plan.Progress {
			if _, ok := progress.Action().Trade(); ok {
				view := progress.View()
				out = tradePhase{found: true, open: domain.GoalWorkOpen(plan.Progress), completed: view.Stage == domain.Completed, attempt: attempt, action: progress.Action().ID()}
				found = true
			}
		}
		if !found {
			return tradePhase{}, ErrControl
		}
	}
	out.retry = r.failed(out) && out.attempt+1 < tradeAttempts(kind)
	return out, nil
}

// tradeSettled reports whether the caravan's session is over for this epoch:
// its open failed for the last time, its accept completed, or an end phase
// exists and is no longer open.
func (r *RoutineTradePlanner) tradeSettled(ctx context.Context, goal store.GoalState, trader string) (bool, error) {
	open, err := r.phase(ctx, goal, domain.TradeOpen, trader)
	if err != nil {
		return false, err
	}
	if r.failed(open) && !open.retry {
		return true, nil
	}
	accept, err := r.phase(ctx, goal, domain.TradeAccept, trader)
	if err != nil {
		return false, err
	}
	if accept.completed {
		return true, nil
	}
	end, err := r.phase(ctx, goal, domain.TradeEnd, trader)
	if err != nil {
		return false, err
	}
	return end.found && !end.open, nil
}

func (r *RoutineTradePlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineTradeResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineTradeResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineTradeResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineTradeResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.TradeWithCaravan {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineTradeResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineTradeResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineTradeResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineTradeResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	census, _, err := r.native.ListTraders(call, identity)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	if _, err = boundary.Context(census.Context, state.Snapshot); err != nil || census.Context.GetTick() < int64(review.Tick) {
		return RoutineTradeResult{}, ErrControl
	}
	// A caravan with an open session this epoch is driven first, tradeable
	// or not: a session native still holds must be settled, never left.
	traders := make([]policy.TraderFacts, 0, len(census.Traders))
	arriving := false
	for _, row := range census.Traders {
		arriving = arriving || row.Travelling
		traders = append(traders, policy.TraderFacts{ID: row.ID, Kind: row.Kind, Faction: row.Faction, CanTrade: row.CanTrade, Travelling: row.Travelling, GoodsStacks: int64(row.GoodsStacks)})
		open, err := r.phase(call, goal, domain.TradeOpen, row.ID)
		if err != nil {
			return RoutineTradeResult{}, err
		}
		if !open.found || !open.completed {
			continue
		}
		settled, err := r.tradeSettled(call, goal, row.ID)
		if err != nil {
			return RoutineTradeResult{}, err
		}
		if !settled {
			return r.drive(call, epoch, state, goal, review, census, row.ID, open.action, started)
		}
	}
	settled := map[string]bool{}
	waiting := arriving
	for _, row := range traders {
		if settled[row.ID], err = r.tradeSettled(call, goal, row.ID); err != nil {
			return RoutineTradeResult{}, err
		}
		// A settled caravan is waited out: the goal recovers when it
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
	open, err := r.phase(call, goal, domain.TradeOpen, trader.ID)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	attempt := 0
	if open.found {
		attempt = open.attempt + 1
	}
	return r.open(call, epoch, state, goal, trader.ID, negotiator, attempt, arbiter, started)
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
		return bridge.NegotiatorRead{}, ErrControl
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
// eligibility and the trader's tradeability, and the executor previews the
// open itself at dispatch (native checks reachability there and walks the
// negotiator over).
func (r *RoutineTradePlanner) open(call, epoch context.Context, state ControlState, goal store.GoalState, trader string, negotiator bridge.NegotiatorRead, attempt int, arbiter *stepArbiter, started time.Time) (RoutineTradeResult, error) {
	if !arbiter.tryClaim(nil, "pawn:"+negotiator.ID) {
		return RoutineTradeResult{Reason: BuildingMethodUsed}, nil
	}
	value, err := domain.NewTradeOpen(trader, domain.PawnID(negotiator.ID), false)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	return r.commit(call, epoch, state, goal, domain.TradeOpen, attempt, trader, value, "", started)
}

// drive advances one caravan's open session: stage the selected lines from
// its sheet, confirm and accept them, or cancel.
func (r *RoutineTradePlanner) drive(call, epoch context.Context, state ControlState, goal store.GoalState, review store.RoutineReview, census bridge.TradersRead, trader string, openAction domain.ActionID, started time.Time) (RoutineTradeResult, error) {
	p := r.reviewer.player
	session, resolved, err := p.journal.LookupTradeSession(call, openAction)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	if !resolved {
		// Open completed but its session is not yet recorded; wait rather
		// than guess which session is open.
		return RoutineTradeResult{Reason: BuildingMethodExistingWork, Trader: trader}, nil
	}
	lines, err := r.phase(call, goal, domain.TradeSetLines, trader)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	end, err := r.phase(call, goal, domain.TradeEnd, trader)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	if end.found {
		// An end that failed leaves native holding the session; there is
		// nothing further to propose, and tradeSettled already reads it as
		// over.
		return RoutineTradeResult{Reason: BuildingMethodExhausted, Trader: trader, Phase: domain.TradeEnd}, nil
	}
	accept, err := r.phase(call, goal, domain.TradeAccept, trader)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	if r.failed(accept) && !accept.retry {
		return r.cancel(call, epoch, state, goal, trader, openAction, session, started)
	}
	identity := boundary.Identity(state.Snapshot)
	sheet, _, err := r.native.ReadTradeSheet(call, identity, session.SessionID)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	if _, err = boundary.Context(sheet.Context, state.Snapshot); err != nil {
		return RoutineTradeResult{}, ErrControl
	}
	if sheet.SessionID != session.SessionID || sheet.Trader != trader || sheet.GiftMode || !sheet.CanTradeNow {
		return r.cancel(call, epoch, state, goal, trader, openAction, session, started)
	}
	if lines.found && !lines.completed {
		return r.cancel(call, epoch, state, goal, trader, openAction, session, started)
	}
	economic, facts, err := r.selection(call, state, review, sheet)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	selection := policy.SelectTrade(economic, facts)
	if !lines.found {
		if len(economic.Targets) == 0 || selection.Refused || len(selection.Selected) == 0 {
			return r.cancel(call, epoch, state, goal, trader, openAction, session, started)
		}
		staged := tradeLinesOf(selection)
		preview, _, err := r.native.PreviewSetTradeLines(call, identity, session.SessionID, session.SessionToken, tradeLinesWire(staged), false)
		if err != nil {
			return RoutineTradeResult{}, err
		}
		if evaluated := preview.GetEvaluated(); evaluated == nil || !evaluated.GetAccepted() {
			return r.cancel(call, epoch, state, goal, trader, openAction, session, started)
		}
		value, err := domain.NewTradeSetLines(staged, false)
		if err != nil {
			return RoutineTradeResult{}, err
		}
		return r.commit(call, epoch, state, goal, domain.TradeSetLines, 0, trader, value, openAction, started)
	}
	// Lines are staged: re-run the same selection over the live sheet and
	// require an exact match, then native's affordability and the silver
	// reserve, before accepting. Any drift cancels.
	staged := tradeStagedLines(sheet)
	if selection.Refused || !sameTradeLines(tradeLinesOf(selection), staged) || !sheet.BalanceKnown || !sheet.ColonyCanAfford || !sheet.TraderHasSilver || sheet.DealSignature == "" {
		return r.cancel(call, epoch, state, goal, trader, openAction, session, started)
	}
	if float64(facts.ColonySilver)+sheet.Balance < float64(selection.SilverReserve) {
		return r.cancel(call, epoch, state, goal, trader, openAction, session, started)
	}
	floors := tradeAcceptFloors(economic, selection)
	value, err := domain.NewTradeAccept(sheet.DealSignature, floors, false, false)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	return r.commit(call, epoch, state, goal, domain.TradeAccept, 0, trader, value, openAction, started)
}

// selection re-measures the need from a fresh colony read and turns it,
// with the live sheet, into SelectTrade's inputs.
func (r *RoutineTradePlanner) selection(call context.Context, state ControlState, review store.RoutineReview, sheet bridge.TradeSheetRead) (domain.TradeEconomicPolicy, policy.TradeSelectionFacts, error) {
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := r.native.ReadColonyFacts(call, identity, false, nil)
	if err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, ErrControl
	}
	if err = bridge.ValidateColonyFacts(observed, identity); err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, ErrControl
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, ErrControl
	}
	medicalFacts := medicalReserveObservationFacts(observed)
	medical, err := policy.ReviewMedicalReserve(medicalFacts, review.Latches.MedicalReserve, r.reviewer.policy.MedicalReserve)
	if err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, err
	}
	// Refresh through the same projection and per-tick plan owner used by
	// goal review; a staged trade never relies on a previous tick's need.
	projection, err := observation.DecodeColony(reply, observation.Identity{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map, Tick: domain.Tick(observed.Context.GetTick())})
	if err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, err
	}
	projection.Facts.FoodPlan = r.reviewer.planFood(projection)
	seasonal := r.reviewer.seasonal(projection.Facts)
	construction, _, err := r.native.ReadConstructionDeficits(call, identity)
	if err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, err
	}
	if _, err = boundary.Context(construction.Context, state.Snapshot); err != nil {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, ErrControl
	}
	floors := policy.RoutineTradeFloors(seasonal, construction.StillNeed)
	need, known := policy.ReviewTradeNeed(medical, medicalFacts.Resources, seasonal.ResourceTargets, floors, projection.Facts.Wealth, seasonal.Trade, policy.RoutineTradeFood(projection.Facts, seasonal)).Value()
	if !known {
		return domain.TradeEconomicPolicy{}, policy.TradeSelectionFacts{}, ErrControl
	}
	rows := tradeSheetRowFacts(sheet.Rows)
	economic := policy.RoutineTradeTargets(need, rows, r.reviewer.policy.ResourceTargets, r.reviewer.policy.Trade)
	facts := policy.TradeSelectionFacts{Complete: true, Rows: rows, Floors: floors, CropSurplusFloors: policy.CropSurplusFloors(need)}
	facts.ColonySilver, facts.TraderSilver, facts.SilverKnown = tradeSheetSilver(sheet.Rows)
	facts.MaxSilverSpend = max(0, facts.ColonySilver)
	return economic, facts, nil
}

func (r *RoutineTradePlanner) cancel(call, epoch context.Context, state ControlState, goal store.GoalState, trader string, openAction domain.ActionID, session store.TradeSession, started time.Time) (RoutineTradeResult, error) {
	preview, _, err := r.native.PreviewEndTrade(call, boundary.Identity(state.Snapshot), session.SessionID, session.SessionToken, op.EndTradeKind_END_TRADE_KIND_CANCEL, false)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	if evaluated := preview.GetEvaluated(); evaluated == nil || !evaluated.GetAccepted() {
		return RoutineTradeResult{Reason: BuildingMethodRefused, Trader: trader, Phase: domain.TradeEnd}, nil
	}
	value, err := domain.NewTradeEnd(domain.TradeEndCancel, false)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	return r.commit(call, epoch, state, goal, domain.TradeEnd, 0, trader, value, openAction, started)
}

// commit records one phase as the caravan's fixed goal method: a plan of
// the trade action alone.
func (r *RoutineTradePlanner) commit(call, epoch context.Context, state ControlState, goal store.GoalState, kind domain.TradeOperationKind, attempt int, trader string, value domain.Trade, openAction domain.ActionID, started time.Time) (RoutineTradeResult, error) {
	p := r.reviewer.player
	method := tradeMethod(kind, trader, attempt)
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-trade-%x", digest[:16]))
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
		return RoutineTradeResult{}, ErrControl
	}
	if kind == domain.TradeOpen {
		_, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan)
	} else {
		_, err = p.journal.CommitTradeGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan, openAction)
	}
	if err != nil {
		return RoutineTradeResult{}, err
	}
	return RoutineTradeResult{Reason: BuildingMethodAdmitted, Plan: id, Trader: trader, Phase: kind}, nil
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
			ProtectedExport: row.ProtectedExport, ProtectedExportKnown: row.ProtectedExportKnown,
		})
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
