using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The native census RoundsTradePlanner reads between phases: the trader census for the caravan and negotiator to
/// open with, the trade-session read for the walk or session native holds, a fresh colony facts read for the
/// medicine and resource stock the need is re-measured from, and the session-scoped sheet each decision is made
/// from. A phase is not previewed: native judges it when it applies, and a refusal fails its method.
/// </summary>
public interface IRoundsTradeSource : Observation.IColonySource
{
    Task<(ConstructionDeficitRead Read, BridgeResult Result)> ReadConstructionDeficitsAsync(C.Identity identity, CancellationToken ct);

    Task<(TradersRead Read, BridgeResult Result)> ListTradersAsync(C.Identity identity, CancellationToken ct);

    Task<(TradeSessionRead Read, BridgeResult Result)> ReadTradeSessionAsync(C.Identity identity, CancellationToken ct);

    Task<(TradeSheetRead Read, BridgeResult Result)> ReadTradeSheetAsync(C.Identity identity, CancellationToken ct);
}

public sealed class RoundsTradeResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }

    public string Trader { get; init; } = "";

    public TradeOperationKind Phase { get; init; }

    /// <summary>
    /// Asks for a clock window without a plan: a caravan is still walking to its trade spot, or a settled one
    /// has yet to leave, and only game time moves it.
    /// </summary>
    public uint NativeWorkTicks { get; init; }
}

/// <summary>
/// One caravan's latest recorded attempt at a phase and the settled stage of its trade action: open reports
/// in-flight work, and a terminal stage other than Completed is a failure of the attempt. Plans lists every
/// attempt's plan, for the refusal budget.
/// </summary>
internal readonly record struct TradePhase(bool Found, bool Open, bool Completed, int Attempt, IReadOnlyList<PlanId> Plans);

/// <summary>
/// Drives TradeWithCaravan one phase edge per step. RimWorld's trade sheet is session-scoped: the line ids and
/// counts a SetTradeLines action carries do not exist until Open has run, and the deal signature Accept requires
/// does not exist until the lines are staged. A committed plan is immutable, so each phase is its own incident
/// method -- open, then set lines or cancel, then accept or cancel -- each an intent naming the session's trader
/// and negotiator, which native checks. Trade is an intent-mode kind: a phase's applied receipt completes its
/// method and a refused one fails it, and what followed is read from live state: the trade-session read (who is
/// walking to or trading with whom) and the session sheet. Per caravan and TradeWithCaravan occurrence the
/// method ids are fixed, so a restart resumes where it left off. A caravan whose session reached accept or
/// cancel, or whose open the shared refusal budget barred (native refused it for good, or for the current
/// world), is settled for the occurrence: it closes when the caravan leaves or the next review measures nothing
/// left to trade.
///
/// Adjacency is native's: OpenTrade orders vanilla's TradeWithPawn job, which walks the negotiator after the
/// trader, and native opens the session where that job would open its dialog. While the session read shows the
/// walk, the routine lends the clock a short window; a walk the game interrupted reads as neither walk nor
/// session, and the open is sent again. Once open, the session binds its participants and the remaining phases
/// need no escort.
/// </summary>
public sealed partial class RoundsTradePlanner
{
    internal Rounder Reviewer { get; }

    internal IRoundsTradeSource Native { get; }

    public RoundsTradePlanner(Rounder reviewer, IRoundsTradeSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsTradePlanner: reviewer == nil || native == nil");
        }
        Reviewer = reviewer;
        Native = native;
    }

    private static bool Failed(TradePhase p) => p.Found && !p.Open && !p.Completed;

    private TradePhase Phase(Sto.IncidentState incident, TradeOperationKind kind, string trader, CancellationToken ct)
    {
        var output = new TradePhase(false, false, false, 0, []);
        var plans = new List<PlanId>();
        for (var attempt = 0; ; attempt++)
        {
            var want = TradeMethod(kind, trader, attempt);
            var method = incident.Methods.FirstOrDefault(m => m.Method == want);
            if (method is null)
            {
                return output with { Plans = plans };
            }
            plans.Add(method.Plan);
            var plan = Reviewer.Player.Journal.LoadPlan(method.Plan, ct);
            var found = false;
            foreach (var progress in plan.Progress)
            {
                if (progress.Action.Trade is not null)
                {
                    output = new TradePhase(true, StorePackage.PlanOpen(plan), progress.View.Stage == Stage.Completed, attempt, plans.ToArray());
                    found = true;
                }
            }
            if (!found)
            {
                throw new ControlException("phase: !found");
            }
        }
    }

    /// <summary>
    /// Whether the caravan's session is over for this occurrence: its last open attempt ended with no session or
    /// walk left and the refusal budget bars another, its accept applied or was refused, or an end phase exists
    /// and is no longer open. Engaged is whether native holds a walk or session with the trader.
    /// </summary>
    private bool TradeSettled(Sto.IncidentState incident, string trader, bool engaged, GenerationSnapshot world, CancellationToken ct)
    {
        var open = Phase(incident, TradeOperationKind.Open, trader, ct);
        if (TradeOpenSpent(open, engaged))
        {
            // An open that ended is walked again until native's refusals bar it: a permanent one for good, a
            // transient one until the world changes.
            var (_, ok) = AdmitSubject(Reviewer.Player.Journal, TradeOpenSubject(trader), open.Plans, world, ct);
            if (!ok)
            {
                return true;
            }
        }
        var accept = Phase(incident, TradeOperationKind.Accept, trader, ct);
        if (TradeAcceptSpent(accept))
        {
            return true;
        }
        var end = Phase(incident, TradeOperationKind.End, trader, ct);
        return end.Found && !end.Open;
    }

    internal async Task<RoundsTradeResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = Reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsTradeResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: !state.ObservationKnown || state.Snapshot.Validate() != nil");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsTradeResult { Verdict = BuildingReasonNoReview };
        }
        var (missionResult, missionHandled) = await MissionAsync(state, review, arbiter, call, epoch).ConfigureAwait(false);
        if (missionHandled)
        {
            return missionResult;
        }
        var (requestResult, requestHandled) = await RequestAsync(state, review, call, epoch).ConfigureAwait(false);
        if (requestHandled)
        {
            return requestResult;
        }
        var (incident, found) = IncidentDeficit(p.Journal, review, TradeWithCaravan, call);
        if (!found)
        {
            return new RoundsTradeResult { Verdict = BuildingReasonNoDeficit };
        }
        if (IncidentOpenWork(p.Journal, incident, call))
        {
            return new RoundsTradeResult { Verdict = BuildingReasonExistingWork };
        }
        var started = Reviewer.Clock.Now();
        var identity = Boundary.Identity(state.Snapshot);
        var (census, _) = await Native.ListTradersAsync(identity, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(census.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException($"step: err != nil || census.Context.GetTick() < int64(review.Tick): {e.Message}");
        }
        if ((census.Context?.Tick ?? 0) < (long)review.Tick.Value)
        {
            throw new ControlException("step: err != nil || census.Context.GetTick() < int64(review.Tick)");
        }
        var (session, _) = await Native.ReadTradeSessionAsync(identity, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(session.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException($"step: err != nil || session.Context.GetTick() < int64(review.Tick): {e.Message}");
        }
        if ((session.Context?.Tick ?? 0) < (long)review.Tick.Value)
        {
            throw new ControlException("step: err != nil || session.Context.GetTick() < int64(review.Tick)");
        }
        // A caravan native holds a session or walk with is driven first, tradeable or not: a session native
        // still holds must be settled, never left, and a walk only game time finishes.
        var traders = new List<Pol.TraderFacts>(census.Traders.Count);
        foreach (var row in census.Traders)
        {
            traders.Add(new Pol.TraderFacts { Participant = row.Participant, ID = row.Id, Kind = row.Kind, Faction = row.Faction, CanTrade = row.CanTrade, Travelling = row.Travelling, GoodsStacks = row.GoodsStacks });
        }
        var arriving = false;
        foreach (var row in census.Traders)
        {
            arriving = arriving || row.Travelling;
            if (row.Id != session.Trader)
            {
                continue;
            }
            if (TradeSettled(incident, row.Id, true, state.Snapshot, call))
            {
                continue;
            }
            if (!session.Open)
            {
                return new RoundsTradeResult { Verdict = BuildingReasonExistingWork, Trader = row.Id, Phase = TradeOperationKind.Open, NativeWorkTicks = TradeWalkTicks };
            }
            return await DriveAsync(state, incident, review, row.Id, traders, new Domain.PawnId(session.Negotiator), started, call, epoch).ConfigureAwait(false);
        }
        var settled = new HashSet<string>();
        var waiting = arriving;
        foreach (var row in census.Traders)
        {
            var over = TradeSettled(incident, row.Id, row.Id == session.Trader, state.Snapshot, call);
            if (over)
            {
                settled.Add(row.Id);
            }
            // A settled caravan is waited out: the occurrence closes when it leaves, and only game time takes it away.
            waiting = waiting || (over && row.CanTrade);
        }
        var (trader, ok) = Pol.PolicyFunctions.SelectTrader(traders, settled);
        if (!ok)
        {
            if (waiting)
            {
                return new RoundsTradeResult { Verdict = WaitFor(Pol.Cause.ExistingWork, "caravan_departure"), NativeWorkTicks = TradeArrivalTicks };
            }
            return new RoundsTradeResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "caravan_trade") };
        }
        if (census.Negotiators.Count == 0)
        {
            return new RoundsTradeResult { Verdict = NoWorker("negotiator") };
        }
        var negotiator = await NegotiatorAsync(state, review, census.Negotiators, call).ConfigureAwait(false);
        var open = Phase(incident, TradeOperationKind.Open, trader.ID, call);
        var attempt = open.Found ? open.Attempt + 1 : 0;
        return await OpenAsync(state, incident, trader.ID, negotiator, attempt, arbiter, started, trader.Participant, call, epoch).ConfigureAwait(false);
    }

    /// <summary>
    /// Picks the colonist to open with: TraderFor over the roster profiles, restricted to the pawns native listed
    /// as eligible (alive, undrafted, able to talk). Native's own first row (best trade price improvement) stands
    /// when the roster is unknown or no profile qualifies -- native has already vetted every listed row.
    /// </summary>
    private async Task<NegotiatorRead> NegotiatorAsync(ControlState state, Sto.Rounds review, IReadOnlyList<NegotiatorRead> eligible, CancellationToken call)
    {
        var expected = await StepScopeAsync(Reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("negotiator: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var read = await Reviewer.ObserveOwnedAsync((Observation.IRoundsSource)Reviewer.Native, expected, Unknown<EquatableArray<Pol.ConstructionClaim>>(), call).ConfigureAwait(false);
        if (!read.Projection.WorkPawns.TryGetValue(out var pawns))
        {
            return eligible[0];
        }
        var ids = eligible.Select(row => new Pol.PawnId(row.Id)).ToList();
        if (Pol.PolicyFunctions.TraderFor(Pol.PolicyFunctions.Among(Pol.PolicyFunctions.Profiles(pawns), ids)) is not { } chosen)
        {
            return eligible[0];
        }
        foreach (var row in eligible)
        {
            if (new Pol.PawnId(row.Id) == chosen)
            {
                return row;
            }
        }
        return eligible[0];
    }

    /// <summary>
    /// Commits the Open phase: the census already vetted the negotiator's eligibility and the trader's
    /// tradeability, and native checks reachability when the open applies and walks the negotiator over.
    /// </summary>
    private async Task<RoundsTradeResult> OpenAsync(ControlState state, Sto.IncidentState incident, string trader, NegotiatorRead negotiator, int attempt, StepArbiter arbiter, DateTimeOffset started, TradeParticipant participant, CancellationToken call, CancellationToken epoch)
    {
        if (!arbiter.TryClaim([], "pawn:" + negotiator.Id))
        {
            return new RoundsTradeResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "negotiator_claim") };
        }
        var value = Trade.NewTradeOpen(trader, new Domain.PawnId(negotiator.Id), false);
        value = value.WithParticipant(participant);
        return await CommitAsync(state, incident, TradeOperationKind.Open, attempt, trader, value, started, call, epoch).ConfigureAwait(false);
    }

    /// <summary>Advances one caravan's open session: stage the selected lines from its sheet, confirm and accept them, or cancel.</summary>
    private async Task<RoundsTradeResult> DriveAsync(ControlState state, Sto.IncidentState incident, Sto.Rounds review, string trader, IReadOnlyList<Pol.TraderFacts> traders, Domain.PawnId negotiator, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        var lines = Phase(incident, TradeOperationKind.SetLines, trader, call);
        var end = Phase(incident, TradeOperationKind.End, trader, call);
        if (end.Found)
        {
            // An end that failed leaves native holding the session; there is nothing further to propose, and
            // TradeSettled already reads it as over.
            return new RoundsTradeResult { Verdict = Refuse(Pol.Cause.RetriesSpent, "trade_end", ""), Trader = trader, Phase = TradeOperationKind.End };
        }
        var accept = Phase(incident, TradeOperationKind.Accept, trader, call);
        if (Failed(accept))
        {
            return await CancelAsync(state, incident, trader, negotiator, started, call, epoch).ConfigureAwait(false);
        }
        var identity = Boundary.Identity(state.Snapshot);
        var (sheet, _) = await Native.ReadTradeSheetAsync(identity, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(sheet.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException($"drive: err != nil: {e.Message}");
        }
        if (sheet.Trader != trader || sheet.Negotiator != negotiator.Value || sheet.GiftMode || !sheet.CanTradeNow)
        {
            return await CancelAsync(state, incident, trader, negotiator, started, call, epoch).ConfigureAwait(false);
        }
        await RecordOffersAsync(state, sheet, TraderStacks(traders, trader), call).ConfigureAwait(false);
        var staged = TradeStagedLines(sheet);
        var phase = TradeSessionPhase(lines, staged.Count);
        if (phase == TradeOperationKind.End)
        {
            return await CancelAsync(state, incident, trader, negotiator, started, call, epoch).ConfigureAwait(false);
        }
        var (economic, facts, capacity) = await SelectionAsync(state, review, sheet, trader, traders, call).ConfigureAwait(false);
        Pol.TradeSelection selection;
        if (facts.Favor)
        {
            // The tribute collector pays favor: only gold sells, with no silver budget, reserve or purchase.
            selection = Pol.PolicyFunctions.SelectFavorSale(facts, facts.FavorKeep);
        }
        else if (economic.Targets.Count == 0 && facts.SaleArt.Count + facts.SaleAnimals.Count + facts.SaleGear.Count == 0)
        {
            // Nothing to buy or sell by the resource catalog: only a pawn purchase can still stage, against the same silver reserve.
            var (currency, _) = Pol.PolicyFunctions.TradeCurrency(facts.Rows);
            selection = new Pol.TradeSelection { SilverReserve = Math.Max(economic.SilverReserve, facts.Floors.GetValueOrDefault(currency)) };
        }
        else
        {
            selection = Pol.PolicyFunctions.SelectTrade(economic, facts);
        }
        // A pawn buy is its own line beside the resource lines.
        if (!selection.Refused && facts.SilverKnown && !facts.Favor)
        {
            var (pawn, pawnOk) = Pol.PolicyFunctions.SelectPawnPurchase(capacity, facts.Rows, facts.ColonySilver, selection.SilverReserve, selection.Selected);
            if (pawnOk)
            {
                selection = selection with { Selected = [.. selection.Selected, pawn] };
            }
            // A wanted animal is a second purchase line under the same reserve; native needs no allow_pawns to buy a pawn row.
            var (animal, animalOk) = Pol.PolicyFunctions.SelectAnimalPurchase(facts.HerdWants, facts.Rows, facts.ColonySilver, selection.SilverReserve, selection.Selected);
            if (animalOk)
            {
                selection = selection with { Selected = [.. selection.Selected, animal] };
            }
        }
        if (phase == TradeOperationKind.SetLines)
        {
            if (selection.Refused || selection.Selected.Count == 0)
            {
                return await CancelAsync(state, incident, trader, negotiator, started, call, epoch).ConfigureAwait(false);
            }
            // Selling an animal is a pawn give-away native refuses without allow_pawns; only a selected pawn sale asks for it.
            var pawnRows = new Dictionary<string, bool>();
            foreach (var row in facts.Rows)
            {
                pawnRows[row.LineID] = row.PawnKnown && row.Pawn;
            }
            var sellsPawn = false;
            foreach (var line in selection.Selected)
            {
                sellsPawn = sellsPawn || (line.Count < 0 && pawnRows.GetValueOrDefault(line.LineID));
            }
            var setLines = Trade.NewTradeSetLines(trader, negotiator, TradeLinesOf(selection), sellsPawn);
            return await CommitAsync(state, incident, TradeOperationKind.SetLines, 0, trader, setLines, started, call, epoch).ConfigureAwait(false);
        }
        // Lines are staged: re-run the same selection over the live sheet and require an exact match, then
        // native's affordability and the silver reserve, before accepting. Any drift cancels.
        if (selection.Refused || !SameTradeLines(TradeLinesOf(selection), staged) || !sheet.ColonyCanAfford || !sheet.TraderHasSilver || sheet.DealSignature == "" || (!facts.Favor && !sheet.BalanceKnown))
        {
            return await CancelAsync(state, incident, trader, negotiator, started, call, epoch).ConfigureAwait(false);
        }
        if (!facts.Favor && (double)facts.ColonySilver + sheet.Balance < (double)selection.SilverReserve)
        {
            return await CancelAsync(state, incident, trader, negotiator, started, call, epoch).ConfigureAwait(false);
        }
        var value = Trade.NewTradeAccept(trader, negotiator, sheet.DealSignature, false, false);
        return await CommitAsync(state, incident, TradeOperationKind.Accept, 0, trader, value, started, call, epoch).ConfigureAwait(false);
    }

    /// <summary>Re-measures the need from a fresh colony read and turns it, with the live sheet, into SelectTrade's inputs.</summary>
    private async Task<(TradeEconomicPolicy Economic, Pol.TradeSelectionFacts Facts, bool Capacity)> SelectionAsync(ControlState state, Sto.Rounds review, TradeSheetRead sheet, string trader, IReadOnlyList<Pol.TraderFacts> traders, CancellationToken call)
    {
        var identity = Boundary.Identity(state.Snapshot);
        var (reply, _) = await Native.ReadColonyFactsAsync(identity, false, call).ConfigureAwait(false);
        var observed = reply.Outcome?.Observed;
        if (observed is null)
        {
            throw new ControlException("selection: observed == nil");
        }
        try
        {
            BridgeApi.ValidateColonyFacts(observed, identity);
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
        var catalog = Native is Observation.IDefinitionSource definitions
            ? await definitions.DefinitionCatalogAsync(identity, call).ConfigureAwait(false)
            : null;
        var medicalFacts = ObservationPackage.ColonyMedicalReserve(observed, tables, catalog);
        medicalFacts = medicalFacts with { Catalog = items };
        var medical = Pol.PolicyFunctions.ReviewMedicalReserve(medicalFacts, review.Latches.MedicalReserve, Reviewer.Policy.MedicalReserve);
        var projection = ObservationPackage.DecodeColony(reply, new Observation.Identity { Colony = state.Snapshot.Colony, Load = state.Snapshot.Load, Map = state.Snapshot.Map, Tick = new Tick(observed.Context?.Tick ?? 0) }, tables, catalog);
        projection = projection with { Facts = projection.Facts with { Items = items } };
        var zoneNative = Native as Observation.IZonesNative;
        projection = await ObservationPackage.FillZones(zoneNative, observed.Context?.Identity ?? identity,projection.Identity, projection, call).ConfigureAwait(false);
        // The food plan reads the caravans on the census as present, the session's priced by the offers just recorded from this sheet.
        projection = projection with { Facts = projection.Facts with { Traders = Known<EquatableArray<Pol.TraderFacts>>([.. traders]) } };
        Reviewer.PlanFood(ref projection);
        var seasonal = Reviewer.Seasonal(projection.Facts);
        var (construction, _) = await Native.ReadConstructionDeficitsAsync(identity, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(construction.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException($"selection: err != nil: {e.Message}");
        }
        var floors = Pol.PolicyFunctions.RoundsTradeFloors(seasonal, construction.StillNeed);
        // MaintainResource's floors (the wood floor, shortfall edges) are the catalog's trade demand: a caravan selling one buys it.
        var targets = Reviewer.ResourceTargets(state.Snapshot);
        // A wealth-driven sale keeps what the review's demand retains: the stock the trade detector held the sale to.
        var retained = Reviewer.Demand.Get(state.Snapshot).Retained;
        // Restore parts no bench can fabricate are bought.
        var partDemand = await SurgeryPartDemandAsync(Native, identity, projection.Facts.MedicalPawns, projection.SurgeryContext(), call).ConfigureAwait(false);
        var parts = partDemand.Parts;
        var benches = partDemand.Benches;
        // A harvested organ sells while the silver runway is short.
        var saleArt = await SaleArtAsync(identity, state.Snapshot, call).ConfigureAwait(false);
        // Unreserved art under negative wealth headroom is shed_art.
        var headroom = projection.Facts.WealthBudget();
        var artCount = Unknown<long>();
        if (saleArt is not null)
        {
            artCount = Known((long)saleArt.Count);
        }
        // Surplus animals sell while the silver runway is short; the race catalog is the herd plan's, the
        // catalog's race rows the projection decoded.
        if (catalog is null)
        {
            throw new ControlException("selection: no definition catalog");
        }
        var rows = TradeSheetRowFacts(sheet.Rows, catalog);
        // The plan counts the animals this trader offers as obtainable, so its wants (bought below) and its surplus (sold) agree.
        var planInput = projection.Facts.HerdPlanInput();
        planInput = planInput with { Offers = Pol.PolicyFunctions.HerdOffers(rows, projection.Facts.AnimalUpkeep.AnimalRaces) };
        var herd = Pol.PolicyFunctions.PlanHerd(planInput);
        var saleAnimals = Pol.PolicyFunctions.HerdSaleAnimals(projection.Facts.AnimalUpkeep.Animals, herd.Policy);
        var food = Pol.PolicyFunctions.RoundsTradeFood(projection.Facts, seasonal);
        var reviewed = Pol.PolicyFunctions.ReviewTradeNeed(projection.Facts.Items, medical, medicalFacts.Resources, targets, floors, projection.Facts.Wealth, retained, food);
        var exportSurplus = Pol.PolicyFunctions.ExportSaleSurplus(projection.Facts.Items, reviewed, medicalFacts.Resources, projection.Facts.Colonists, Reviewer.Exports.Products(state.Snapshot));
        var organSurplus = Pol.PolicyFunctions.OrganSaleSurplus(projection.Facts.Items, exportSurplus, medicalFacts.Resources, projection.Facts.Colonists, projection.Facts.Recipes);
        var reserved = Pol.PolicyFunctions.ReserveSurgeryStock(organSurplus, projection.Facts.MedicalPawns);
        var surgery = Pol.PolicyFunctions.SurgeryTradeNeed(reserved, Pol.PolicyFunctions.SurgeryPurchaseParts(projection.Facts.MedicalPawns, projection.SurgeryContext(), parts, Pol.PolicyFunctions.FabricableParts(benches)));
        var shed = Pol.PolicyFunctions.ShedArtNeed(surgery, headroom, artCount);
        if (!Pol.PolicyFunctions.AnimalSaleNeed(projection.Facts.Items, shed, saleAnimals, projection.Facts.Silver(), projection.Facts.Colonists).TryGetValue(out var need))
        {
            throw new ControlException("selection: !known");
        }
        // A resource is bought only as far as the supply plan opened this trader's offer for it.
        var planned = await PlannedPurchasesAsync(state, review, trader, call).ConfigureAwait(false);
        need = RestrictToPlan(need, planned, Pol.PolicyFunctions.PlannedTradeNutrition(projection.Facts.FoodPlan, trader));
        var economic = Pol.PolicyFunctions.RoundsTradeTargets(projection.Facts.Items, need, rows, targets, projection.Facts.Colonists);
        var (colonySilver, traderSilver, silverKnown) = TradeSheetSilver(sheet.Rows);
        var facts = new Pol.TradeSelectionFacts { Complete = true, Rows = rows, Floors = floors, CropSurplusFloors = Pol.PolicyFunctions.CropSurplusFloors(need), ColonySilver = colonySilver, TraderSilver = traderSilver, SilverKnown = silverKnown };
        facts = facts with { MaxSilverSpend = Math.Max(0, facts.ColonySilver) };
        facts = facts with { Favor = sheet.FavorCurrency };
        facts = facts with { FavorKeep = Pol.PolicyFunctions.FavorGoldKeep(targets, floors, retained) };
        facts = facts with { SaleArt = saleArt ?? [] };
        if (facts.Favor)
        {
            facts = facts with { FavorPrisoners = await FavorPrisonersAsync(state, review, call).ConfigureAwait(false) };
        }
        // Warehouse gear below the keep floors sells.
        var claims = Reviewer.Player.Journal.ZoneClaims(state.Snapshot, projection.Identity.Tick, call);
        var warehouses = new HashSet<string>();
        claims.TryGetValue(out var owned);
        foreach (var z in owned)
        {
            if (z.Role == DomainConstants.GeneralRole || z.Role.StartsWith(DomainConstants.GeneralRole + ":", StringComparison.Ordinal))
            {
                warehouses.Add(z.Id);
            }
        }
        var saleGear = Pol.PolicyFunctions.SaleGear(rows, warehouses);
        // While the silver gap is open, gear above demand sells too, cheapest first until the prices cover the
        // gap (MaintainTrade's sale path).
        if (Pol.PolicyFunctions.SilverGap(projection.Facts.Items, Known(need), projection.Facts.Silver(), projection.Facts.Colonists).TryGetValue(out var gap) && gap > 0)
        {
            var (surplus, surplusOk) = Reviewer.Exports.Surplus(state.Snapshot);
            if (surplusOk)
            {
                foreach (var id in Pol.PolicyFunctions.SaleGearSurplus(rows, warehouses, surplus, gap))
                {
                    saleGear.Add(id);
                }
            }
        }
        facts = facts with { SaleGear = saleGear };
        facts = facts with { HerdWants = [.. Pol.PolicyFunctions.HerdWants(herd), .. Pol.PolicyFunctions.PlannedAnimalPurchases(projection.Facts.FoodPlan, trader)] };
        if (need.SurplusAnimals > 0)
        {
            facts = facts with { SaleAnimals = saleAnimals };
        }
        var hk = headroom.TryGetValue(out var h);
        facts = facts with { ArtFirst = hk && h < 0 };
        Pol.PolicyFunctions.JoinerCapacity(projection.Facts.JoinerCapacity()).TryGetValue(out var capacity);
        return (economic, facts, capacity);
    }

    /// <summary>
    /// Reads the prisoner census through the routine reading (the one the negotiator choice uses) and returns the
    /// surplus prisoners a favor session sells.
    /// </summary>
    private async Task<IReadOnlySet<Pol.PawnId>> FavorPrisonersAsync(ControlState state, Sto.Rounds review, CancellationToken call)
    {
        var expected = await StepScopeAsync(Reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("favorPrisoners: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var read = await Reviewer.ObserveOwnedAsync((Observation.IRoundsSource)Reviewer.Native, expected, Unknown<EquatableArray<Pol.ConstructionClaim>>(), call).ConfigureAwait(false);
        Pol.PolicyFunctions.RoundsSilverShort(read.Projection.Facts, Reviewer.Policy, review.Latches.MedicalReserve).TryGetValue(out var shortage);
        return read.Projection.Facts.SurplusPrisoners(shortage);
    }

    private async Task<RoundsTradeResult> CancelAsync(ControlState state, Sto.IncidentState incident, string trader, Domain.PawnId negotiator, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        var value = Trade.NewTradeEnd(trader, negotiator, TradeEndKind.Cancel, false);
        return await CommitAsync(state, incident, TradeOperationKind.End, 0, trader, value, started, call, epoch).ConfigureAwait(false);
    }

    /// <summary>Records one phase as the caravan's fixed incident method: a plan of the trade action alone.</summary>
    private async Task<RoundsTradeResult> CommitAsync(ControlState state, Sto.IncidentState incident, TradeOperationKind kind, int attempt, string trader, Trade value, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        var p = Reviewer.Player;
        var method = TradeMethod(kind, trader, attempt);
        var id = PlanIds.MintPlanId();
        var tradeId = new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-trade"));
        if (kind != TradeOperationKind.Open)
        {
            var (session, _) = await Native.ReadTradeSessionAsync(Boundary.Identity(state.Snapshot), call).ConfigureAwait(false);
            if (session.Trader == trader && session.Target is not null)
            {
                value = value.WithParticipant(TradeParticipantOf(session.Target));
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
        p.Journal.CommitIncidentMethod(incident.Incident.Id, method, "", plan, call);
        // An accept or end settles the caravan: its offers no longer describe a session the plan can use.
        if (kind == TradeOperationKind.Accept || kind == TradeOperationKind.End)
        {
            Reviewer.TradeOffers.Drop(trader);
        }
        // The next phase lands in the stop after this window: a session left open under a full window outlasts the caravan's visit.
        var ticks = kind != TradeOperationKind.End ? TradeArrivalTicks : 0u;
        return new RoundsTradeResult { Verdict = BuildingReasonAdmitted, Plan = id, Trader = trader, Phase = kind, NativeWorkTicks = ticks };
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>The window lent while a caravan walks in or a settled one lingers: one game hour, after which the census is read again.</summary>
    internal const uint TradeArrivalTicks = GameTime.TicksPerHour;

    /// <summary>The window lent while a negotiator walks to open a session, after which the session is read again.</summary>
    internal const uint TradeWalkTicks = 250;

    /// <summary>The fixed method id of one caravan's phase attempt.</summary>
    internal static MethodId TradeMethod(TradeOperationKind kind, string trader, int attempt)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(trader));
        return new MethodId(string.Create(CultureInfo.InvariantCulture, $"trade-{Wire.Name(kind)}-{Convert.ToHexString(digest, 0, 8).ToLowerInvariant()}-{attempt}"));
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
    internal static long TraderStacks(IReadOnlyList<Pol.TraderFacts> traders, string id)
    {
        foreach (var t in traders)
        {
            if (t.ID == id)
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

    /// <summary>Classifies a food line from the catalog's rows and the evaluated Nutrition stat; a pawn or currency line is never food.</summary>
    internal static Fact<Pol.TradeFoodGood> TradeFoodFact(TradeSheetRow row, Bridge.DefinitionCatalog catalog)
    {
        if (row.Pawn || row.Currency || row.DefName == "")
        {
            return Unknown<Pol.TradeFoodGood>();
        }
        return catalog.TradeFood(row.DefName) is { } good ? Known(good) : Unknown<Pol.TradeFoodGood>();
    }

    internal static List<Pol.TradeSheetRowFact> TradeSheetRowFacts(IReadOnlyList<TradeSheetRow> rows, Bridge.DefinitionCatalog catalog)
    {
        var output = new List<Pol.TradeSheetRowFact>(rows.Count);
        foreach (var row in rows)
        {
            output.Add(new Pol.TradeSheetRowFact
            {
                Food = TradeFoodFact(row, catalog),
                LineID = row.LineId,
                DefName = row.DefName,
                ColonyCount = row.ColonyCount,
                TraderCount = row.TraderCount,
                BuyPrice = row.BuyPrice,
                BuyPriceKnown = row.BuyPriceKnown,
                SellPrice = row.SellPrice,
                SellPriceKnown = row.SellPriceKnown,
                TraderWillTrade = row.TraderWillTrade,
                TraderWillTradeKnown = row.TraderWillTradeKnown,
                Currency = row.Currency,
                CurrencyKnown = row.CurrencyKnown,
                Pawn = row.Pawn,
                PawnKnown = row.PawnKnown,
                ThingID = row.ThingId,
                Stuff = row.Stuff,
                HitPoints = row.HitPoints,
                HitPointsKnown = row.HitPointsKnown,
                Quality = row.Quality,
                QualityKnown = row.QualityKnown,
                ZoneID = row.ZoneId,
                PawnID = row.PawnId,
                PawnGender = row.PawnGender,
                Skills = TradePawnSkills(row.Skills),
                ViolenceCapable = row.ViolenceCapable,
                ViolenceCapableKnown = row.ViolenceCapableKnown,
                GuestStatus = row.GuestStatus,
                PrisonerSecure = row.PrisonerSecure,
                PrisonerSecureKnown = row.PrisonerSecureKnown,
                PawnDowned = row.PawnDowned,
                PawnDownedKnown = row.PawnDownedKnown,
                ExtraHomeFaction = row.ExtraHomeFaction,
                ExtraHostFaction = row.ExtraHostFaction,
            });
        }
        return output;
    }

    internal static List<Pol.ProfileSkill> TradePawnSkills(IReadOnlyList<TradeSheetSkill> skills)
    {
        var output = new List<Pol.ProfileSkill>();
        foreach (var skill in skills)
        {
            output.Add(new Pol.ProfileSkill { Name = skill.Name, Level = (int)skill.Level, Passion = skill.Passion, Disabled = skill.Disabled });
        }
        return output;
    }

    /// <summary>Reads both sides' current silver from the sheet's own currency row.</summary>
    internal static (long Colony, long Trader, bool Known) TradeSheetSilver(IReadOnlyList<TradeSheetRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.CurrencyKnown && row.Currency)
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
    internal static List<TradeLine> TradeStagedLines(TradeSheetRead sheet)
    {
        var output = new List<TradeLine>();
        foreach (var row in sheet.Rows)
        {
            if (row.TransferNow != 0 && !(row.CurrencyKnown && row.Currency))
            {
                output.Add(new TradeLine { LineId = row.LineId, AbsoluteCount = (int)row.TransferNow });
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
