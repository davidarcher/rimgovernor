using System.Security.Cryptography;
using System.Text;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Runtime;

public sealed class TradeResult : IPlannerResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }

    public string Trader { get; init; } = "";

    public TradeOperationKind Phase { get; init; }
}

/// <summary>
/// One caravan's latest recorded attempt at a phase and the settled stage of its trade action: open reports
/// in-flight work, and a terminal stage other than Completed is a failure of the attempt. Plans lists every
/// attempt's plan, for the refusal budget.
/// </summary>
internal readonly record struct TradePhase(bool Found, bool Open, bool Completed, IReadOnlyList<PlanId> Plans);

/// <summary>
/// Drives the economy/caravan Need (#3316) one phase edge per step. RimWorld's trade sheet is session-scoped: the
/// line ids and counts a SetTradeLines action carries do not exist until Open has run, and the deal signature
/// Accept requires does not exist until the lines are staged. A committed plan is immutable, so each phase is its
/// own Method of the Need -- open, then set lines or cancel, then accept or cancel -- each an intent naming the
/// session's trader and negotiator, which native checks. Trade is an intent-mode kind: a phase's applied receipt
/// completes its method and a refused one fails it, and what followed is read from live state: the trade-session
/// read (who is walking to or trading with whom) and the session sheet. Per caravan the MethodIds name the trader
/// and phase, so a restart resumes where it left off; an open that ended is re-admitted as the MethodId's next
/// attempt. A caravan whose session reached accept or cancel, or whose open the shared refusal budget barred
/// (native refused it for good, or for the current world), is settled for the load: the Need is met again when the
/// caravan leaves or the next review measures nothing left to trade.
///
/// Adjacency is native's: OpenTrade orders vanilla's TradeWithPawn job, which walks the negotiator after the
/// trader, and native opens the session where that job would open its dialog. While the session read shows the
/// walk, the routine lends the clock a short window; a walk the game interrupted reads as neither walk nor
/// session, and the open is sent again. Once open, the session binds its participants and the remaining phases
/// need no escort.
/// </summary>
public sealed partial class TradePlanner
{
    internal ColonyReview Reviewer { get; }

    internal GameClient Native { get; }

    public TradePlanner(ColonyReview reviewer, GameClient native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewReviewTradePlanner: reviewer == nil || native == nil");
        }
        Reviewer = reviewer;
        Native = native;
    }

    private static bool Failed(TradePhase p) => p.Found && !p.Open && !p.Completed;

    private TradePhase Phase(Sto.NeedOwner owner, TradeOperationKind kind, string trader, CancellationToken ct)
    {
        var journal = Reviewer.Player.Journal;
        var plans = journal.OwnerMethodAttempts(owner, TradeMethod(kind, trader), ct);
        if (plans.Count == 0)
        {
            return new TradePhase(false, false, false, []);
        }
        var plan = journal.LoadPlan(plans[^1], ct);
        foreach (var progress in plan.Progress)
        {
            if (progress.Action.Trade is not null)
            {
                return new TradePhase(true, StorePackage.PlanOpen(plan), progress.View.Stage == Stage.Completed, plans);
            }
        }
        throw new ControlException("phase: !found");
    }

    /// <summary>
    /// Whether the caravan's session is over for the load: its last open attempt ended with no session or
    /// walk left and the refusal budget bars another, its accept applied or was refused, or an end phase exists
    /// and is no longer open. Engaged is whether native holds a walk or session with the trader.
    /// </summary>
    private bool TradeSettled(Sto.NeedOwner owner, string trader, bool engaged, GenerationSnapshot world, CancellationToken ct)
    {
        var open = Phase(owner, TradeOperationKind.Open, trader, ct);
        if (TradeOpenSpent(open, engaged))
        {
            // An open that ended is walked again until native's refusals bar it: a permanent one for good, a
            // transient one until the world changes.
            var (_, ok) = ReviewRefusalBudget.AdmitSubject(Reviewer.Player.Journal, TradeOpenSubject(trader), open.Plans, world, ct);
            if (!ok)
            {
                return true;
            }
        }
        var accept = Phase(owner, TradeOperationKind.Accept, trader, ct);
        if (TradeAcceptSpent(accept))
        {
            return true;
        }
        var end = Phase(owner, TradeOperationKind.End, trader, ct);
        return end.Found && !end.Open;
    }

    /// <summary>The live session state and current review this Need's step runs in; null with the verdict when there is none.</summary>
    private (ControlState State, Sto.ReviewState? Review, Verdict Verdict) Current(CancellationToken call)
    {
        var p = Reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return (state, null, BuildingReasonDisabled);
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: !state.ObservationKnown || state.Snapshot.Validate() != nil");
        }
        var review = p.Journal.LoadReview(call);
        return !review.Enabled || review.Snapshot != state.Snapshot ? (state, null, BuildingReasonNoReview) : (state, review, default);
    }

    /// <summary>Whether any of <paramref name="owner"/>'s unretired Methods is still open work.</summary>
    private bool OpenWork(Sto.NeedOwner owner, CancellationToken call) =>
        owner.Methods.Any(m => StorePackage.PlanOpen(Reviewer.Player.Journal.LoadPlan(m.Plan, call)));

    /// <summary>
    /// The economy/caravan Need's work: drive the caravan native holds a session or walk with, else open a session
    /// with the best caravan that is not settled for the load.
    /// </summary>
    internal async Task<TradeResult> CaravanAsync(NeedWork work, CancellationToken call)
    {
        var p = Reviewer.Player;
        var (state, current, verdict) = Current(call);
        if (current is not { } review)
        {
            return new TradeResult { Verdict = verdict };
        }
        var owner = work.Owner;
        var epoch = work.Epoch;
        if (OpenWork(owner, call))
        {
            return new TradeResult { Verdict = BuildingReasonExistingWork };
        }
        var started = Reviewer.Clock.Now();
        var identity = Boundary.Identity(state.Snapshot);
        var census = await Native.ListTradersAsync(identity, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(census.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException($"step: err != nil || census.Context.GetTick() < int64(review.Tick): {e.Message}");
        }
        if (census.Context.Tick < (long)review.Tick.Value)
        {
            throw new ControlException("step: err != nil || census.Context.GetTick() < int64(review.Tick)");
        }
        var session = await Native.ReadTradeSessionAsync(identity, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(session.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException($"step: err != nil || session.Context.GetTick() < int64(review.Tick): {e.Message}");
        }
        if (session.Context.Tick < (long)review.Tick.Value)
        {
            throw new ControlException("step: err != nil || session.Context.GetTick() < int64(review.Tick)");
        }
        // A caravan native holds a session or walk with is driven first, tradeable or not: a session native
        // still holds must be settled, never left, and a walk only game time finishes.
        IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.Trader> traders = census.Traders;
        var engaged = session.Engagement as RimGovernor.Brain.GameFacts.Observations.TradeEngagement.Engaged;
        var engagedTrader = TradeTarget.TraderOf(session);
        var arriving = false;
        foreach (var row in traders)
        {
            arriving = arriving || row.Travelling;
            if (engaged is null || row.Key() != engagedTrader)
            {
                continue;
            }
            if (TradeSettled(owner, row.Key(), true, state.Snapshot, call))
            {
                continue;
            }
            if (!engaged.Open)
            {
                return new TradeResult { Verdict = BuildingReasonExistingWork, Trader = row.Key(), Phase = TradeOperationKind.Open };
            }
            return await DriveAsync(state, owner, review, row.Key(), traders, engaged.Negotiator, started, call, epoch).ConfigureAwait(false);
        }
        return new TradeResult { Verdict = arriving ? WaitFor(Pol.Cause.ExistingWork, "caravan_arrival") : BuildingReasonNoDeficit };
    }
    /// <summary>Advances one caravan's open session: stage the selected lines from its sheet, confirm and accept them, or cancel.</summary>
    private async Task<TradeResult> DriveAsync(ControlState state, Sto.NeedOwner owner, Sto.ReviewState review, string trader, IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.Trader> traders, Domain.PawnId negotiator, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        var lines = Phase(owner, TradeOperationKind.SetLines, trader, call);
        var end = Phase(owner, TradeOperationKind.End, trader, call);
        if (end.Found)
        {
            // An end that failed leaves native holding the session; there is nothing further to propose, and
            // TradeSettled already reads it as over.
            return new TradeResult { Verdict = Refuse(Pol.Cause.RetriesSpent, "trade_end", ""), Trader = trader, Phase = TradeOperationKind.End };
        }
        var accept = Phase(owner, TradeOperationKind.Accept, trader, call);
        if (Failed(accept))
        {
            return await CancelAsync(state, owner, trader, negotiator, started, call, epoch).ConfigureAwait(false);
        }
        var identity = Boundary.Identity(state.Snapshot);
        var sheet = await Native.ReadTradeSheetAsync(identity, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(sheet.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException($"drive: err != nil: {e.Message}");
        }
        if (TradeTarget.TradeParticipantOf(sheet.Target).Key() != trader || sheet.Negotiator != negotiator || sheet.GiftMode || !sheet.CanTradeNow)
        {
            return await CancelAsync(state, owner, trader, negotiator, started, call, epoch).ConfigureAwait(false);
        }
        await RecordOffersAsync(state, sheet, TraderStacks(traders, trader), call).ConfigureAwait(false);
        var staged = TradeStagedLines(sheet);
        var phase = TradeSessionPhase(lines, staged.Count);
        if (phase == TradeOperationKind.End)
        {
            return await CancelAsync(state, owner, trader, negotiator, started, call, epoch).ConfigureAwait(false);
        }
        var (economic, facts, capacity) = await SelectionAsync(state, review, sheet, trader, traders, call).ConfigureAwait(false);
        Pol.TradeSelection selection;
        if (facts.Favor)
        {
            // The tribute collector pays favor: only gold sells, with no silver budget, reserve or purchase.
            selection = Pol.TradePolicyRules.SelectFavorSale(facts, facts.FavorKeep);
        }
        else if (economic.Targets.Count == 0 && facts.StockNeeds.Count == 0 && facts.SaleArt.Count + facts.SaleAnimals.Count + facts.SaleGear.Count == 0)
        {
            // Nothing to buy or sell by the resource catalog: only a pawn purchase can still stage, against the same silver reserve.
            selection = Pol.TradePolicyRules.ReserveOnly(economic, facts);
        }
        else
        {
            selection = Pol.TradePolicyRules.SelectTrade(economic, facts);
        }
        // A pawn buy is its own line beside the resource lines.
        if (!selection.Refused && facts.SilverKnown && !facts.Favor)
        {
            var (pawn, pawnOk) = Pol.TradePolicyRules.SelectPawnPurchase(capacity, facts.Rows, facts.ColonySilver, selection.SilverReserve, selection.Selected);
            if (pawnOk)
            {
                selection = selection with { Selected = [.. selection.Selected, pawn] };
            }
            // A wanted animal is a second purchase line under the same reserve; native needs no allow_pawns to buy a pawn row.
            var (animal, animalOk) = HerdPurchaseRules.SelectAnimalPurchase(facts.HerdWants, facts.Rows, facts.ColonySilver, selection.SilverReserve, selection.Selected);
            if (animalOk)
            {
                selection = selection with { Selected = [.. selection.Selected, animal] };
            }
        }
        if (phase == TradeOperationKind.SetLines)
        {
            if (selection.Refused || selection.Selected.Count == 0)
            {
                return await CancelAsync(state, owner, trader, negotiator, started, call, epoch).ConfigureAwait(false);
            }
            // Selling an animal is a pawn give-away native refuses without allow_pawns; only a selected pawn sale asks for it.
            var pawnRows = new Dictionary<string, bool>();
            foreach (var row in facts.Rows)
            {
                pawnRows[row.LineID] = row.Pawn is not null;
            }
            var sellsPawn = false;
            foreach (var line in selection.Selected)
            {
                sellsPawn = sellsPawn || (line.Count < 0 && pawnRows.GetValueOrDefault(line.LineID));
            }
            var setLines = Trade.NewTradeSetLines(trader, negotiator, TradeLinesOf(selection), sellsPawn);
            return await CommitAsync(state, owner, TradeOperationKind.SetLines, trader, setLines, started, call, epoch).ConfigureAwait(false);
        }
        // Lines are staged: re-run the same selection over the live sheet and require an exact match, then
        // native's affordability and the silver reserve, before accepting. Any drift cancels.
        if (selection.Refused || !SameTradeLines(TradeLinesOf(selection), staged) || sheet.Balance is not RimGovernor.Brain.GameFacts.Observations.TradeBalance.Counted { ColonyCanAfford: true } balance || !sheet.TraderHasEnoughSilver || sheet.DealSignature == "")
        {
            return await CancelAsync(state, owner, trader, negotiator, started, call, epoch).ConfigureAwait(false);
        }
        if (!Pol.TradePolicyRules.ReserveSatisfied(facts, selection, balance.Transfer))
        {
            return await CancelAsync(state, owner, trader, negotiator, started, call, epoch).ConfigureAwait(false);
        }
        var value = Trade.NewTradeAccept(trader, negotiator, sheet.DealSignature, false, false);
        return await CommitAsync(state, owner, TradeOperationKind.Accept, trader, value, started, call, epoch).ConfigureAwait(false);
    }

    /// <summary>Re-measures the need from a fresh colony read and turns it, with the live sheet, into SelectTrade's inputs.</summary>
    private async Task<(TradeEconomicPolicy Economic, Pol.TradeSelectionFacts Facts, bool Capacity)> SelectionAsync(ControlState state, Sto.ReviewState review, RimGovernor.Brain.GameFacts.Observations.TradeSheet sheet, string trader, IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.Trader> traders, CancellationToken call)
    {
        var identity = Boundary.Identity(state.Snapshot);
        var observed = await Native.ReadColonyFactsAsync(identity, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(observed.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException($"selection: err != nil: {e.Message}");
        }
        // Refresh through the same projection and per-tick plan owner used by goal review; a staged trade never
        // relies on a previous tick's need.
        var tables = await Native.FrameTablesAsync(identity, call).ConfigureAwait(false);
        var items = await Reviewer.ItemFactsAsync(state.Snapshot, call).ConfigureAwait(false);
        var catalog = Native is IDefinitionSource definitions
            ? await definitions.DefinitionCatalogAsync(identity, call).ConfigureAwait(false)
            : null;
        var medicalFacts = ColonyFramesReads.ColonyMedicalReserve(observed, tables, catalog);
        medicalFacts = medicalFacts with { Catalog = items };
        var medical = Pol.MedicalRules.ReviewMedicalReserve(medicalFacts, review.Latches.MedicalReserve, Reviewer.Policy.MedicalReserve);
        var projection = ColonyPlanningReads.Compose(observed, new ObservationIdentity
        {
            Context = observed.Context with { Identity = Boundary.Identity(state.Snapshot), NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase(state.Snapshot.Native.Value) },
        }, tables, catalog);
        projection = projection with { Facts = projection.Facts with { Items = items } };
        // The food plan reads the caravans on the census as present, the session's priced by the offers just recorded from this sheet.
        projection = projection with { Facts = projection.Facts with { Traders = [.. traders] } };
        Reviewer.PlanFood(ref projection);
        var seasonal = Reviewer.Seasonal(projection.Facts);
        var construction = await Native.ReadConstructionDeficitsAsync(identity, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(construction.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException($"selection: err != nil: {e.Message}");
        }
        var floors = Pol.TradePolicyRules.TradeFloors(seasonal, construction.StillNeed);
        // The economy/resource Need's floors (the wood floor, shortfall edges) are the catalog's trade demand: a caravan selling one buys it.
        var targets = Reviewer.ResourceTargets(state.Snapshot);
        // A wealth-driven sale keeps what the review's demand retains: the stock the trade detector held the sale to.
        var retained = Reviewer.Demand.Get(state.Snapshot).Retained;
        // Restore parts no bench can fabricate are bought.
        var partDemand = await ColonyReview.SurgeryPartDemandAsync(Native, identity, projection.Facts.MedicalPawns, ColonyPlanningRules.SurgeryContext(projection.Facts), call).ConfigureAwait(false);
        var parts = partDemand.Parts;
        var benches = partDemand.Benches;
        // A harvested organ sells while the silver runway is short.
        var saleArt = await SaleArtAsync(identity, state.Snapshot, call).ConfigureAwait(false);
        // Unreserved art under negative wealth headroom is shed_art.
        var headroom = projection.Facts.WealthBudget();
        var artCount = saleArt?.Count ?? throw new Pol.RequiredFactException("sculpture sale facts");
        // Surplus animals sell while the silver runway is short; the race catalog is the herd plan's, the
        // catalog's race rows the projection decoded.
        if (catalog is null)
        {
            throw new ControlException("selection: no definition catalog");
        }
        var rows = TradeSheetRowFacts(sheet.Lines, catalog);
        // The plan counts the animals this trader offers as obtainable, so its wants (bought below) and its surplus (sold) agree.
        var planInput = projection.Facts.HerdPlanInput();
        planInput = planInput with { Offers = HerdPurchaseRules.HerdOffers(rows, projection.Facts.AnimalUpkeep.AnimalRaces) };
        var herd = HerdPlanRules.PlanHerd(planInput);
        var saleAnimals = HerdSaleRules.HerdSaleAnimals(projection.Facts.AnimalUpkeep.Animals, herd.Policy);
        var food = Pol.TradePolicyRules.TradeFood(projection.Facts, seasonal);
        var reviewed = Pol.TradePolicyRules.ReviewTradeNeed(projection.Facts.Items, medical, medicalFacts.ObservedStock(), targets, floors, projection.Facts.Census.Threat.Observed, retained, food);
        var exportSurplus = ExportOrdersRules.ExportSaleSurplus(projection.Facts.Items, reviewed, medicalFacts.ObservedStock(), projection.Facts.Census.ColonistCount, Reviewer.Exports.Products(state.Snapshot));
        var organSurplus = Pol.OrganHarvestRules.OrganSaleSurplus(projection.Facts.Items, exportSurplus, medicalFacts.ObservedStock(), projection.Facts.Census.ColonistCount, projection.Facts.Recipes);
        var reserved = Pol.OrganHarvestRules.ReserveSurgeryStock(organSurplus, projection.Facts.MedicalPawns);
        var surgery = Pol.SurgeryRules.SurgeryTradeNeed(reserved, Pol.SurgeryRules.SurgeryPurchaseParts(projection.Facts.MedicalPawns, ColonyPlanningRules.SurgeryContext(projection.Facts), parts, Pol.SurgeryRules.FabricableParts(benches)));
        var shed = Pol.TradePolicyRules.ShedArtNeed(surgery, headroom, artCount);
        var need = HerdSaleRules.AnimalSaleNeed(projection.Facts.Items, shed, saleAnimals, projection.Facts.Silver(), projection.Facts.Census.ColonistCount);
        // A resource is bought only as far as the supply plan opened this trader's offer for it.
        var planned = await PlannedPurchasesAsync(state, review, trader, call).ConfigureAwait(false);
        // An unread food plan opened no nutrition to buy.
        var plannedNutrition = projection.Facts.FoodPlan is { } foodPlan ? Pol.TradePolicyRules.PlannedTradeNutrition(foodPlan, trader) : 0;
        need = TradePolicyRules.RestrictToPlan(need, planned, plannedNutrition);
        var economic = Pol.TradePolicyRules.TradeTargets(projection.Facts.Items, need with { MedicineReplenish = 0, SurgeryParts = [] }, rows, targets, projection.Facts.Census.ColonistCount);
        var (colonySilver, traderSilver, silverKnown) = TradeSheetSilver(sheet.Lines);
        var facts = new Pol.TradeSelectionFacts { Complete = true, Rows = rows, Floors = floors, CropSurplusFloors = Pol.TradePolicyRules.CropSurplusFloors(need), ColonySilver = colonySilver, TraderSilver = traderSilver, SilverKnown = silverKnown };
        var waits = Reviewer.TradeWaits.Require(state.Snapshot, "selected trade waits");
        facts = facts with { StockNeeds = waits.Needs, StockMedicines = waits.Medicines };
        facts = facts with { MaxSilverSpend = Pol.TradePolicyRules.MaxSpend(facts.ColonySilver) };
        facts = facts with { Favor = sheet.CurrencyKind == RimGovernor.Brain.GameFacts.Defs.TradeCurrency.Favor };
        facts = facts with { FavorKeep = Pol.TradePolicyRules.FavorGoldKeep(targets, floors, retained) };
        facts = facts with { SaleArt = saleArt ?? [] };
        if (facts.Favor)
        {
            facts = facts with { FavorPrisoners = await FavorPrisonersAsync(state, review, call).ConfigureAwait(false) };
        }
        // Warehouse gear below the keep floors sells.
        var claims = Reviewer.Player.Journal.ZoneClaims(state.Snapshot, projection.Identity.Tick, call);
        var warehouses = new HashSet<string>();
        foreach (var z in claims)
        {
            if (z.Role == StockpileRolesConstants.GeneralRole || z.Role.StartsWith(StockpileRolesConstants.GeneralRole + ":", StringComparison.Ordinal))
            {
                warehouses.Add(z.Id);
            }
        }
        var saleGear = Pol.TradePolicyRules.SaleGear(rows, warehouses);
        // While the silver gap is open, gear above demand sells too, cheapest first until the prices cover the
        // gap (the economy/trade Need's sale path).
        var gap = Pol.SilverGapRules.SilverGap(projection.Facts.Items, need, projection.Facts.Silver(), projection.Facts.Census.ColonistCount);
        if (gap > 0)
        {
            var (surplus, surplusOk) = Reviewer.Exports.Surplus(state.Snapshot);
            if (surplusOk)
            {
                foreach (var id in GearSurplusRules.SaleGearSurplus(rows, warehouses, surplus, gap))
                {
                    saleGear.Add(id);
                }
            }
        }
        facts = facts with { SaleGear = saleGear };
        facts = facts with { HerdWants = [.. HerdPurchaseRules.HerdWants(herd), .. AnimalAcquireRules.PlannedAnimalPurchases(projection.Facts.FoodPlan ?? throw new Pol.RequiredFactException("food plan"), trader)] };
        if (need.SurplusAnimals > 0)
        {
            facts = facts with { SaleAnimals = saleAnimals };
        }
        var h = headroom;
        facts = facts with { ArtFirst = h < 0 };
        var capacity = Pol.TradePolicyRules.PopulationTradeNeed(new Pol.TradeNeed(), Pol.PopulationRules.JoinerCapacity(projection.Facts.JoinerCapacity())).Population;
        return (economic, facts, capacity);
    }

    /// <summary>
    /// Reads the prisoner census through the routine reading (the one the negotiator choice uses) and returns the
    /// surplus prisoners a favor session sells.
    /// </summary>
    private async Task<IReadOnlySet<PawnId>> FavorPrisonersAsync(ControlState state, Sto.ReviewState review, CancellationToken call)
    {
        var expected = await StepScope.StepScopeAsync(Reviewer.Native, call).ConfigureAwait(false);
        if (!BuildingPlanner.BuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("favorPrisoners: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var read = await Reviewer.ObserveOwnedAsync((IReviewSource)Reviewer.Native, expected, call).ConfigureAwait(false);
        var shortage = Pol.SilverGapRules.SilverShort(read.Reading.Facts, Reviewer.Policy, review.Latches.MedicalReserve);
        return read.Reading.Facts.SurplusPrisoners(shortage);
    }

    private async Task<TradeResult> CancelAsync(ControlState state, Sto.NeedOwner owner, string trader, Domain.PawnId negotiator, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        var value = Trade.NewTradeEnd(trader, negotiator, TradeEndKind.Cancel, false);
        return await CommitAsync(state, owner, TradeOperationKind.End, trader, value, started, call, epoch).ConfigureAwait(false);
    }

    /// <summary>Records one phase as the caravan's Method of the Need: a plan of the trade action alone.</summary>
    private async Task<TradeResult> CommitAsync(ControlState state, Sto.NeedOwner owner, TradeOperationKind kind, string trader, Trade value, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        var p = Reviewer.Player;
        var method = TradeMethod(kind, trader);
        var id = PlanIds.MintPlanId();
        var tradeId = new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-trade"));
        if (kind != TradeOperationKind.Open)
        {
            var session = await Native.ReadTradeSessionAsync(Boundary.Identity(state.Snapshot), call).ConfigureAwait(false);
            if (session.Engagement is RimGovernor.Brain.GameFacts.Observations.TradeEngagement.Engaged engaged && TradeTarget.TraderOf(session) == trader)
            {
                value = value.WithParticipant(TradeTarget.TradeParticipantOf(engaged.Target));
            }
        }
        var action = DomainAction.NewTradeAction(tradeId, value);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var elapsed = Reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > Reviewer.MaxAge)
        {
            throw new ControlException("commit: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge");
        }
        var commit = p.Journal.CommitOwnerWork(owner, method, "", plan, call);
        if (commit.Outcome != Sto.ClaimOutcome.Admitted)
        {
            return new TradeResult { Verdict = BuildingPlanner.OwnerCommitVerdict(commit), Trader = trader, Phase = kind };
        }
        // An accept or end settles the caravan: its offers no longer describe a session the plan can use.
        if (kind == TradeOperationKind.Accept || kind == TradeOperationKind.End)
        {
            Reviewer.TradeOffers.Drop(trader);
        }
        return new TradeResult { Verdict = BuildingReasonAdmitted, Plan = id, Trader = trader, Phase = kind };
    }
}

partial class TradePlanner
{
    /// <summary>The MethodId of one caravan's phase: its trader and phase; a retry is the next attempt of the same MethodId.</summary>
    internal static MethodId TradeMethod(TradeOperationKind kind, string trader)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(trader));
        return new MethodId(string.Create(CultureInfo.InvariantCulture, $"trade-{Wire.Name(kind)}-{Convert.ToHexString(digest, 0, 8).ToLowerInvariant()}"));
    }

    /// <summary>
    /// The shared refusal budget's subject for opening a session with trader. An open ends without a session
    /// when the world moves under the walk -- the trader wandering off as the negotiator arrives, a path that
    /// could not complete -- and is walked again; only native's refusals of it spend the budget. A line staging,
    /// accept or end is attempted once.
    /// </summary>
    internal static string TradeOpenSubject(string trader)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(trader));
        return "trade-open-" + Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
    }

    /// <summary>An accept that is over, applied or refused: native closes the session either way, so the caravan is settled for the occurrence and the routine replans rather than reopening it.</summary>
    internal static bool TradeAcceptSpent(TradePhase accept) => accept.Found && !accept.Open;

    /// <summary>An open attempt that is over without a session or walk to show for it: refused, or applied and since ended.</summary>
    internal static bool TradeOpenSpent(TradePhase open, bool engaged) =>
        open.Found && !open.Open && (!open.Completed || !engaged);

    /// <summary>The goods stacks of the trader on the census.</summary>
    internal static long TraderStacks(IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.Trader> traders, string id)
    {
        foreach (var t in traders)
        {
            if (t.Key() == id)
            {
                return t.GoodsStacks;
            }
        }
        return 0;
    }

    /// <summary>
    /// The next phase of an open session: read from the live sheet, not the journal, so a session a save load
    /// carried over resumes where native holds it. Lines already staged on the sheet go to accept; a recorded
    /// line staging that failed cancels; otherwise the lines are staged.
    /// </summary>
    internal static TradeOperationKind TradeSessionPhase(TradePhase lines, int staged)
    {
        if (lines.Found && !lines.Open && !lines.Completed)
        {
            return TradeOperationKind.End;
        }
        if (staged > 0 || lines.Completed)
        {
            return TradeOperationKind.Accept;
        }
        return TradeOperationKind.SetLines;
    }

    /// <summary>Classifies a food line from the catalog's rows and the evaluated Nutrition stat; null for a line that is not food (a pawn or currency line never is).</summary>
    internal static Pol.TradeFoodGood? TradeFoodOf(RimGovernor.Brain.GameFacts.Observations.TradeLine row, Bridge.DefinitionCatalog catalog) =>
        row.Pawn is not null || row.Currency || row.DefName == "" ? null : catalog.TradeFood(row.DefName);

    internal static List<Pol.TradeSheetRowFact> TradeSheetRowFacts(IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.TradeLine> rows, Bridge.DefinitionCatalog catalog)
    {
        var output = new List<Pol.TradeSheetRowFact>(rows.Count);
        foreach (var row in rows)
        {
            output.Add(new Pol.TradeSheetRowFact
            {
                Food = TradeFoodOf(row, catalog),
                LineID = row.LineId,
                DefName = row.DefName,
                ColonyCount = row.ColonyCount,
                TraderCount = row.TraderCount,
                BuyPrice = row.BuyPrice,
                SellPrice = row.SellPrice,
                TraderWillTrade = row.TraderWillTrade,
                Currency = row.Currency,
                Pawn = row.Pawn is { } pawn ? TradeRowPawn(pawn) : null,
                ThingID = row.ThingId,
                Stuff = row.Stuff,
                Grade = row.Grade,
                ZoneID = row.ZoneId,
            });
        }
        return output;
    }

    internal static Pol.TradeRowPawn TradeRowPawn(RimGovernor.Brain.GameFacts.Observations.TradePawn pawn) => new()
    {
        Id = pawn.Id,
        Gender = pawn.Gender,
        Skills = [.. pawn.Skills],
        ViolenceCapable = pawn.ViolenceCapable,
        Guest = pawn.Guest,
        Downed = pawn.Downed,
        ExtraHomeFaction = pawn.ExtraHomeFactionId,
        ExtraHostFaction = pawn.ExtraHostFactionId,
    };

    /// <summary>Reads both sides' current silver from the sheet's own currency line.</summary>
    internal static (long Colony, long Trader, bool Known) TradeSheetSilver(IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.TradeLine> rows)
    {
        foreach (var row in rows)
        {
            if (row.Currency)
            {
                return (row.ColonyCount, row.TraderCount, true);
            }
        }
        return (0, 0, false);
    }

    internal static List<TradeLine> TradeLinesOf(Pol.TradeSelection selection)
    {
        var output = new List<TradeLine>(selection.Selected.Count);
        foreach (var line in selection.Selected)
        {
            output.Add(new TradeLine { LineId = line.LineID, AbsoluteCount = (int)line.Count });
        }
        return output;
    }

    /// <summary>Reads the non-currency rows the live sheet has staged, in sheet order, for comparison against a fresh selection.</summary>
    internal static List<TradeLine> TradeStagedLines(RimGovernor.Brain.GameFacts.Observations.TradeSheet sheet)
    {
        var output = new List<TradeLine>();
        foreach (var row in sheet.Lines)
        {
            if (row.TransferCount != 0 && !row.Currency)
            {
                output.Add(new TradeLine { LineId = row.LineId, AbsoluteCount = (int)row.TransferCount });
            }
        }
        return output;
    }

    /// <summary>Compares two line sets regardless of order: the sheet stages rows in sheet order, the selection in target order with any pawn buy last.</summary>
    internal static bool SameTradeLines(IReadOnlyList<TradeLine> left, IReadOnlyList<TradeLine> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }
        var counts = new Dictionary<TradeLine, int>();
        for (var i = 0; i < left.Count; i++)
        {
            counts[left[i]] = counts.GetValueOrDefault(left[i]) + 1;
            counts[right[i]] = counts.GetValueOrDefault(right[i]) - 1;
        }
        return counts.Values.All(n => n == 0);
    }
}
