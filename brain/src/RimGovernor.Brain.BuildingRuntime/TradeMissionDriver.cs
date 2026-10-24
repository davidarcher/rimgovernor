namespace RimGovernor.Brain.BuildingRuntime;

partial class RoundsTradePlanner
{
    /// <summary>
    /// Requires actual cargo held by the returned native crew. An accept receipt, caravan disappearance, or
    /// existing home stock proves none.
    /// </summary>
    public static bool TradeMissionDelivered(TradeMission m, WorldProgressionRead world, IReadOnlyDictionary<string, long> cargo)
    {
        if (!TradeMissionValid(m) || !m.PurchaseCommitted || m.ReturnGoods.Count == 0 || !TradeMissionCrewAtHome(m, world))
        {
            return false;
        }
        foreach (var item in m.ReturnGoods)
        {
            ulong packed = 0;
            foreach (var seed in m.Pack)
            {
                if (seed.Definition == item.Definition)
                {
                    packed = seed.Count;
                }
            }
            if (cargo.GetValueOrDefault(item.Definition) < (long)(item.Count + packed))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Refreshes asymmetric home routes, current food/rot and capacity after trading. An unreachable home route
    /// holds an order, never admits a trip.
    /// </summary>
    public static bool TradeMissionReturnSafe(TradeMission m, PbObservations.CaravanState caravan)
    {
        foreach (var route in caravan.HomeRoutes)
        {
            if (route.Destination == m.HomeTile && route.Route is PbObservations.PackRoute.Reachable { EstimatedTicks: >= 0 and var ticks })
            {
                return Pol.TradePolicyRules.TradeMissionReturnSafe(new Pol.MissionReturnFacts
                {
                    Mass = caravan.MassUsage, Capacity = caravan.MassCapacity, FoodDays = caravan.FoodDays, RotDays = caravan.FoodRotDays, RouteTicks = ticks,
                });
            }
        }
        return false;
    }
}

partial class RoundsTradePlanner
{
    internal static PbObservations.CaravanState? MissionCaravan(TradeMission m, WorldProgressionRead world)
    {
        PbObservations.CaravanState? found = null;
        foreach (var row in world.Caravans)
        {
            var crew = row.Crew();
            if (!m.Crew.Any(crew.Contains))
            {
                continue;
            }
            if (found is not null || crew.Count != m.Crew.Count || !m.Crew.All(crew.Contains))
            {
                return null;
            }
            found = row;
        }
        return found;
    }

    /// <summary>The stat the game shows for a definition; false when it shows none or cannot read it.</summary>
    internal static bool MissionCatalogStat(Bridge.DefinitionCatalog catalog, string def, Bridge.CatalogStat stat, out double value)
    {
        try
        {
            var (read, shown) = catalog.ShownStatValue(def, "", stat);
            value = read;
            return shown;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            value = 0;
            return false;
        }
    }
}

/// <summary>
/// The mission one economy/trade-mission step drives: the Need's owner, the record it was read from and the mission's
/// minted id. Its Methods are <c>&lt;phase&gt;-&lt;mission id&gt;</c>, once per mission phase in a load.
/// </summary>
internal sealed record MissionStep(Sto.NeedOwner Owner, Sto.TradeMissionsRecord Record, string Id)
{
    public MethodId Method(string phase) => new(phase + "-" + Id);
}

public sealed partial class RoundsTradePlanner
{
    /// <summary>
    /// The economy/trade-mission Need's work (#3316): with no mission under way, start the settlement trip the supply
    /// plan proposed; otherwise advance the mission in <c>family/economy/trade_missions</c> one phase edge from the
    /// world progression read. A mission leaves the record once delivered or ended, which meets the Need.
    /// </summary>
    internal async Task<RoundsTradeResult> MissionAsync(NeedWork work, CancellationToken call)
    {
        var (state, current, verdict) = Current(call);
        if (current is not { } review)
        {
            return new RoundsTradeResult { Verdict = verdict };
        }
        if (Native is not FacadeNativeClient source)
        {
            return new RoundsTradeResult { Verdict = FieldUnavailable("world_progression") };
        }
        var player = Reviewer.Player;
        var record = player.Journal.LoadTradeMissions(Player.PlayerWorld(state.Snapshot), call);
        if (record.Missions.Count == 0)
        {
            return CreateMission(record, call);
        }
        var step = new MissionStep(work.Owner, record, record.Missions[0].Id);
        var m = record.Missions[0].Mission;
        var world = await source.ReadWorldProgressionAsync(Boundary.Identity(state.Snapshot), call).ConfigureAwait(false);
        if (!TradeMissionCurrentRead(state.Snapshot, world))
        {
            throw new ControlException("mission: world progression read is not current");
        }
        // Shared Hands owns dispatch and uncertain work. Never create a second formation or purchase while its
        // method remains live.
        if (OpenWork(work.Owner, call))
        {
            return new RoundsTradeResult { Verdict = BuildingReasonExistingWork };
        }
        var caravan = MissionCaravan(m, world);
        if (m.Phase == TradeMissionPhase.Planned)
        {
            var (observedPhase, observed) = RoundsTradePlanner.TradeMissionDepartureEvidence(m, world);
            if (observed) return MissionRecord(step, m with { Phase = observedPhase }, call);
            return new RoundsTradeResult { Verdict = Verdict.AwaitingPlan("unsupported_forecast_caravan_formation",
                "Caravan packing and departure require shared forecast labor admission.") };
        }
        if (m.Phase is TradeMissionPhase.Departing or TradeMissionPhase.Outbound)
        {
            var (phase, known) = RoundsTradePlanner.TradeMissionDepartureEvidence(m, world);
            if (known && phase != m.Phase)
            {
                return MissionRecord(step, m with { Phase = phase }, call);
            }
        }
        if (caravan is null)
        {
            if (TradeMissionCrewAtHome(m, world))
            {
                var crew = m.Crew.Select(id => id.Value).ToList();
                var cargo = await source.ReadPawnCargoAsync(Boundary.Identity(state.Snapshot), crew, call).ConfigureAwait(false);
                if (RoundsTradePlanner.TradeMissionDelivered(m, world, cargo))
                {
                    return MissionRecord(step, m with { Phase = TradeMissionPhase.Delivered }, call);
                }
                if (!m.PurchaseCommitted)
                {
                    return MissionRecord(step, m with { Phase = TradeMissionPhase.Ended }, call);
                }
                // Vanilla unloads individually after home entry. If a read missed held cargo, delivery
                // remains unknown; loose stock is no substitute.
                return new RoundsTradeResult { Verdict = BuildingReasonExistingWork };
            }
            // Crew explicitly dead or absent everywhere ends without delivery.
            var visible = world.Maps.Any(row => m.Crew.Any(row.Colonists().Contains))
                || world.Assemblies.Any(row => m.Crew.Any(row.Pawns.Contains))
                || world.Caravans.Any(row => m.Crew.Any(row.Crew().Contains));
            return visible
                ? new RoundsTradeResult { Verdict = BuildingReasonExistingWork }
                : MissionRecord(step, m with { Phase = TradeMissionPhase.Ended }, call);
        }
        if (m.Phase == TradeMissionPhase.Buying)
        {
            return await MissionTradeAsync(state, review, step, m, caravan, call, work.Epoch).ConfigureAwait(false);
        }
        if (m.Phase == TradeMissionPhase.Returning)
        {
            var session = await Native.ReadTradeSessionAsync(Boundary.Identity(state.Snapshot), call).ConfigureAwait(false);
            var target = new TradeParticipant(TradeParticipantKind.Settlement, m.Settlement, caravan.Caravan.Id);
            if (TradeTarget.TraderOf(session) == target.Key())
            {
                if (MissionAttempted(step, "trade-end", call))
                {
                    return new RoundsTradeResult { Verdict = BuildingReasonExistingWork };
                }
                var value = Trade.NewTradeEnd(target.Key(), m.Negotiator, TradeEndKind.Cancel, false).WithParticipant(target);
                return MissionTradeCommit(state, step, m, value, call, work.Epoch);
            }
            if (caravan.Course is PbObservations.CaravanCourse.Moving { Destination: var destination } && destination == m.HomeTile)
            {
                return new RoundsTradeResult { Verdict = BuildingReasonExistingWork };
            }
            if (!RoundsTradePlanner.TradeMissionReturnSafe(m, caravan))
            {
                return new RoundsTradeResult { Verdict = BuildingReasonExistingWork };
            }
            var departureValid = CaravanDeparture.TryCreate(m.Crew, [], m.HomeTile, out var departure);
            if (MissionAttempted(step, "trade-return", call))
            {
                return new RoundsTradeResult { Verdict = BuildingReasonExistingWork };
            }
            if (!departureValid)
            {
                throw new DomainException("invalid caravan departure");
            }
            var id = PlanIds.MintPlanId();
            var action = DomainAction.NewCaravanDepartureAction(new ActionId(id.Value + "-home"), departure! with { Mode = CaravanDepartureMode.ExistingRoute });
            return MissionCommit(state, step, m, id, action, "trade-return", call, work.Epoch);
        }
        return new RoundsTradeResult { Verdict = BuildingReasonExistingWork };
    }

    /// <summary>Whether the mission's <paramref name="phase"/> Method was committed in this load.</summary>
    private bool MissionAttempted(MissionStep step, string phase, CancellationToken ct) =>
        Reviewer.Player.Journal.OwnerMethodPlan(step.Owner, step.Method(phase), ct) is not null;

    /// <summary>Records the mission's new phase; one that is over leaves the record and releases its acquisition claim.</summary>
    internal RoundsTradeResult MissionRecord(MissionStep step, TradeMission m, CancellationToken ct)
    {
        Reviewer.Player.Journal.SaveTradeMissions(step.Record.With(step.Id, m), ct);
        if (m.Over)
        {
            var memory = Reviewer.TradeAcquisition;
            lock (memory.Mu)
            {
                foreach (var owner in memory.Active.Where(kv => kv.Value.EndsWith(":settlement:" + m.Settlement, StringComparison.Ordinal)).Select(kv => kv.Key).ToList())
                {
                    memory.Active.Remove(owner);
                }
            }
        }
        return new RoundsTradeResult { Verdict = BuildingReasonExistingWork };
    }

    /// <summary>Commits one mission phase as the Need's Method, with the mission intent it carries in the same transaction.</summary>
    internal RoundsTradeResult MissionCommit(ControlState state, MissionStep step, TradeMission m, PlanId id, DomainAction a, string phase, CancellationToken call, CancellationToken epoch)
    {
        var plan = new PlanSpec(id, new PlanRevision(1), [a]);
        Reviewer.Player.Current(call, epoch);
        if (Reviewer.Player.Session.State() != state)
        {
            throw new ControlException("missionCommit: session state changed");
        }
        var commit = Reviewer.Player.Journal.CommitTradeMissionMethod(step.Owner.Need, state.Snapshot, step.Method(phase), "", plan, step.Record.With(step.Id, m), call);
        return commit.Outcome == Sto.ClaimOutcome.Admitted
            ? new RoundsTradeResult { Verdict = BuildingReasonAdmitted, Plan = id }
            : new RoundsTradeResult { Verdict = Verdict.Committed(commit) };
    }

    internal RoundsTradeResult MissionTradeCommit(ControlState state, MissionStep step, TradeMission m, Trade value, CancellationToken call, CancellationToken epoch)
    {
        var id = PlanIds.MintPlanId();
        var a = DomainAction.NewTradeAction(new ActionId(id.Value + "-trade"), value);
        return MissionCommit(state, step, m, id, a, "trade-" + Wire.Name(value.Kind), call, epoch);
    }

    internal async Task<RoundsTradeResult> MissionTradeAsync(
        ControlState state,
        Sto.Rounds review,
        MissionStep step,
        TradeMission m,
        PbObservations.CaravanState caravan, CancellationToken call, CancellationToken epoch)
    {
        var target = new TradeParticipant(TradeParticipantKind.Settlement, m.Settlement, caravan.Caravan.Id);
        var trader = target.Key();
        var session = await Native.ReadTradeSessionAsync(Boundary.Identity(state.Snapshot), call).ConfigureAwait(false);
        if (m.PurchaseCommitted)
        {
            return MissionRecord(step, m with { Phase = TradeMissionPhase.Returning }, call);
        }
        var engaged = TradeTarget.TraderOf(session);
        if (engaged.Length != 0 && engaged != trader)
        {
            // Native holds another session (a visiting caravan's, the economy/caravan Need's): the mission waits it out.
            return new RoundsTradeResult { Verdict = WaitFor(Pol.Cause.ExistingWork, "trade_session") };
        }
        if (engaged != trader)
        {
            if (MissionAttempted(step, "trade-open", call))
            {
                return MissionRecord(step, m with { Phase = TradeMissionPhase.Returning }, call);
            }
            var census = await Native.ListTradersAsync(Boundary.Identity(state.Snapshot), call).ConfigureAwait(false);
            if (!census.Traders.Any(row => row.Key() == trader && row.CanTrade))
            {
                return MissionRecord(step, m with { Phase = TradeMissionPhase.Returning }, call);
            }
            var open = Trade.NewTradeOpen(trader, m.Negotiator, false).WithParticipant(target);
            return MissionTradeCommit(state, step, m, open, call, epoch);
        }
        if (session.Engagement is not PbObservations.TradeEngagement.Engaged { Open: true })
        {
            return new RoundsTradeResult { Verdict = BuildingReasonExistingWork };
        }
        var sheet = await Native.ReadTradeSheetAsync(Boundary.Identity(state.Snapshot), call).ConfigureAwait(false);
        var (lines, goods) = await MissionPurchasesAsync(state, review, m, sheet, caravan, call).ConfigureAwait(false);
        if (TradeTarget.TradeParticipantOf(sheet.Target).Key() != trader || !sheet.CanTradeNow || lines.Count == 0)
        {
            return MissionRecord(step, m with { Phase = TradeMissionPhase.Returning }, call);
        }
        if (TradeStagedLines(sheet).Count == 0)
        {
            if (MissionAttempted(step, "trade-set_lines", call))
            {
                return MissionRecord(step, m with { Phase = TradeMissionPhase.Returning }, call);
            }
            var set = Trade.NewTradeSetLines(trader, m.Negotiator, lines, false).WithParticipant(target);
            return MissionTradeCommit(state, step, m, set, call, epoch);
        }
        var (silver, _, known) = TradeSheetSilver(sheet.Lines);
        if (!SameTradeLines(lines, TradeStagedLines(sheet)) || !known || sheet.Balance is not PbObservations.TradeBalance.Counted { ColonyCanAfford: true } balance
            || !Pol.TradePolicyRules.TradeMissionPaymentSafe(m.SilverBudget, silver, balance.Transfer)
            || sheet.DealSignature.Length == 0)
        {
            return MissionRecord(step, m with { Phase = TradeMissionPhase.Returning }, call);
        }
        m = m with { ReturnGoods = EquatableArray.From(goods), PurchaseCommitted = true };
        var accept = Trade.NewTradeAccept(trader, m.Negotiator, sheet.DealSignature, false, false).WithParticipant(target);
        return MissionTradeCommit(state, step, m, accept, call, epoch);
    }

    /// <summary>
    /// Feeds live priced candidates through the existing ranker. Demand comes from the current owning
    /// food/resource plans, bounded by saved authorization. It does not publish away goods into home supply
    /// candidates.
    /// </summary>
    internal async Task<(IReadOnlyList<TradeLine> Lines, IReadOnlyList<CargoItem> Goods)> MissionPurchasesAsync(ControlState state, Sto.Rounds review, TradeMission m, PbObservations.TradeSheet sheet, PbObservations.CaravanState caravan, CancellationToken ct)
    {
        var reply = await Native.ReadColonyFactsAsync(Boundary.Identity(state.Snapshot), false, ct).ConfigureAwait(false);
        var tables = await Native.FrameTablesAsync(Boundary.Identity(state.Snapshot), ct).ConfigureAwait(false);
        if (Native is not IDefinitionSource definitions)
        {
            throw new ControlException("missionPurchases: frame tables carry no catalog");
        }
        var catalog = await definitions.DefinitionCatalogAsync(Boundary.Identity(state.Snapshot), ct).ConfigureAwait(false);
        var projection = ColonyDecodeReads.DecodeColony(
            reply,
            new Observation.Identity
            {
                Colony = state.Snapshot.Colony,
                Load = state.Snapshot.Load,
                Map = state.Snapshot.Map,
                Tick = new Tick(reply.Context.Tick),
            },
            tables,
            catalog);
        Reviewer.PlanFood(ref projection);
        var targets = Reviewer.ResourceTargets(state.Snapshot);
        var nutrition = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in m.Demand)
        {
            if (MissionCatalogStat(catalog, item.Definition, CatalogStats.Nutrition, out var shown))
            {
                nutrition[item.Definition] = shown;
            }
        }
        var foodGap = 0.0;
        if (nutrition.Values.Any(n => n > 0))
        {
            // A food mission sizes its purchase from the food plan's gap.
            if (!projection.Facts.FoodPlan.TryGetValue(out var food))
            {
                throw new Pol.RequiredFactException("food plan");
            }
            foodGap = food.GapPerDay;
        }
        if (!projection.Facts.Resources.TryGetValue(out var stock))
        {
            throw new Pol.RequiredFactException("resource census");
        }
        var rows = new List<Pol.MissionPurchaseRow>();
        foreach (var row in sheet.Lines)
        {
            // An offer whose mass the game does not show cannot be fitted to the caravan.
            if (!MissionCatalogStat(catalog, row.DefName, CatalogStats.Mass, out var unitMass))
            {
                continue;
            }
            rows.Add(new Pol.MissionPurchaseRow
            {
                LineID = row.LineId,
                DefName = row.DefName,
                TraderCount = row.TraderCount,
                Pawn = row.Pawn is not null,
                Currency = row.Currency,
                BuyPrice = row.BuyPrice,
                WillTrade = row.TraderWillTrade,
                UnitMass = unitMass,
            });
        }
        var (silver, _, known) = TradeSheetSilver(sheet.Lines);
        if (!known)
        {
            return ([], []);
        }
        return Pol.TradePolicyRules.PlanTradeMissionPurchases(new Pol.MissionPurchaseRequest
        {
            Mission = m,
            Targets = targets,
            Stock = stock,
            FoodGapPerDay = foodGap,
            FoodTargetDays = Reviewer.Policy.FoodTargetDays,
            Nutrition = nutrition,
            Rows = rows,
            Silver = silver,
            Mass = caravan.MassUsage,
            Capacity = caravan.MassCapacity,
        });
    }

    /// <summary>
    /// Starts the settlement trip the supply plan proposed for food or resources: claims the acquisition and records
    /// the planned mission in <c>family/economy/trade_missions</c> under a minted id.
    /// </summary>
    internal RoundsTradeResult CreateMission(Sto.TradeMissionsRecord record, CancellationToken ct)
    {
        foreach (var owner in new[] { "food", "resources" })
        {
            if (ProposedMission(owner) is not { } proposed || !Reviewer.ClaimTradeAcquisition(owner, proposed.Option))
            {
                continue;
            }
            Reviewer.Player.Journal.SaveTradeMissions(record.With(PlanIds.MintPlanId().Value, proposed.Mission), ct);
            return new RoundsTradeResult { Verdict = BuildingReasonExistingWork };
        }
        return new RoundsTradeResult { Verdict = BuildingReasonNoDeficit };
    }

    /// <summary>
    /// Whether the supply plan proposes a settlement trip for food or resources with a planned mission: the
    /// economy/trade-mission Need's want while no mission is under way.
    /// </summary>
    internal bool MissionProposed() => ProposedMission("food") is not null || ProposedMission("resources") is not null;

    private (string Option, TradeMission Mission)? ProposedMission(string owner)
    {
        var proposal = Reviewer.TradeAcquisitionPlan(owner).Proposal;
        if (proposal is null || proposal.Option.Kind != Pol.TradeAcquisitionKind.Settlement)
        {
            return null;
        }
        lock (Reviewer.TradeAcquisition.Mu)
        {
            return Reviewer.TradeAcquisition.Missions.TryGetValue(proposal.Option.ID, out var m) && m is not null ? (proposal.Option.ID, m) : null;
        }
    }
}
