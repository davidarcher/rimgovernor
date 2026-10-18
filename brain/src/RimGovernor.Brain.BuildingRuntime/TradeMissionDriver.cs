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
    /// Refreshes asymmetric home routes, current food/rot and capacity after trading. Unknown facts hold an
    /// order, never admit a trip.
    /// </summary>
    public static bool TradeMissionReturnSafe(TradeMission m, CaravanJourney? caravan)
    {
        if (caravan is null)
        {
            return false;
        }
        var input = new Pol.MissionReturnFacts
        {
            Mass = caravan.MassUsage,
            Capacity = caravan.MassCapacity,
            FoodDays = MissionOptional(caravan.FoodDays, caravan.FoodDaysKnown),
            RotDays = caravan.FoodRotDays,
        };
        foreach (var route in caravan.HomeRoutes)
        {
            if (route.DestinationTile == m.HomeTile && route.Reachable && route.EstimatedTicksKnown && route.EstimatedTicks >= 0)
            {
                input = input with { RouteTicks = Fact.Known(route.EstimatedTicks) };
                break;
            }
        }
        return Pol.TradePolicyRules.TradeMissionReturnSafe(input);
    }
}

partial class RoundsTradePlanner
{
    internal static CaravanJourney? MissionCaravan(TradeMission m, WorldProgressionRead world)
    {
        CaravanJourney? found = null;
        foreach (var row in world.Caravans)
        {
            var overlap = m.Crew.Any(id => row.PawnIds.Contains(id.Value));
            if (!overlap)
            {
                continue;
            }
            if (found is not null || row.PawnIds.Count != m.Crew.Count || m.Crew.Any(id => !row.PawnIds.Contains(id.Value)))
            {
                return null;
            }
            found = row;
        }
        return found;
    }

    internal static Fact<T> MissionOptional<T>(T v, bool known) => known ? Fact.Known(v) : Fact.Unknown<T>();

    internal static Fact<double> MissionCatalogStat(Bridge.DefinitionCatalog? catalog, string def, Bridge.CatalogStat stat)
    {
        if (catalog is null)
        {
            return Fact.Unknown<double>();
        }
        try
        {
            // A stat the game does not show for the def is unknown, not an exception per definition.
            var (value, shown) = catalog.ShownStatValue(def, "", stat);
            return shown ? Fact.Known((double)value) : Fact.Unknown<double>();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Fact.Unknown<double>();
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
            var (observedPhase, _, observed) = RoundsTradePlanner.TradeMissionDepartureEvidence(m, world);
            if (observed) return MissionRecord(step, m with { Phase = observedPhase }, call);
            return new RoundsTradeResult { Verdict = Verdict.AwaitingPlan("unsupported_forecast_caravan_formation",
                "Caravan packing and departure require shared forecast labor admission.") };
        }
        if (m.Phase is TradeMissionPhase.Departing or TradeMissionPhase.Outbound)
        {
            var (phase, _, known) = RoundsTradePlanner.TradeMissionDepartureEvidence(m, world);
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
            var visible = world.Maps.Any(row => m.Crew.Any(id => row.PawnIds.Contains(id.Value)))
                || world.Assemblies.Any(row => m.Crew.Any(id => row.PawnIds.Contains(id.Value)))
                || world.Caravans.Any(row => m.Crew.Any(id => row.PawnIds.Contains(id.Value)));
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
            var target = new TradeParticipant(TradeParticipantKind.Settlement, m.Settlement, caravan.Id);
            if (session.Trader == target.Key())
            {
                if (MissionAttempted(step, "trade-end", call))
                {
                    return new RoundsTradeResult { Verdict = BuildingReasonExistingWork };
                }
                var value = Trade.NewTradeEnd(target.Key(), m.Negotiator, TradeEndKind.Cancel, false).WithParticipant(target);
                return MissionTradeCommit(state, step, m, value, call, work.Epoch);
            }
            if (caravan.Moving && caravan.Destination == m.HomeTile)
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
            : new RoundsTradeResult { Verdict = RoundsBuildingPlanner.OwnerCommitVerdict(new Sto.WorkCommit(commit.Outcome, commit.Conflict)) };
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
        CaravanJourney caravan, CancellationToken call, CancellationToken epoch)
    {
        var target = new TradeParticipant(TradeParticipantKind.Settlement, m.Settlement, caravan.Id);
        var trader = target.Key();
        var session = await Native.ReadTradeSessionAsync(Boundary.Identity(state.Snapshot), call).ConfigureAwait(false);
        if (m.PurchaseCommitted)
        {
            return MissionRecord(step, m with { Phase = TradeMissionPhase.Returning }, call);
        }
        if (session.Trader.Length != 0 && session.Trader != trader)
        {
            // Native holds another session (a visiting caravan's, the economy/caravan Need's): the mission waits it out.
            return new RoundsTradeResult { Verdict = WaitFor(Pol.Cause.ExistingWork, "trade_session") };
        }
        if (session.Trader != trader)
        {
            if (MissionAttempted(step, "trade-open", call))
            {
                return MissionRecord(step, m with { Phase = TradeMissionPhase.Returning }, call);
            }
            var census = await Native.ListTradersAsync(Boundary.Identity(state.Snapshot), call).ConfigureAwait(false);
            if (!census.Traders.Any(row => row.Id == trader && row.CanTrade))
            {
                return MissionRecord(step, m with { Phase = TradeMissionPhase.Returning }, call);
            }
            var open = Trade.NewTradeOpen(trader, m.Negotiator, false).WithParticipant(target);
            return MissionTradeCommit(state, step, m, open, call, epoch);
        }
        if (!session.Open)
        {
            return new RoundsTradeResult { Verdict = BuildingReasonExistingWork };
        }
        var sheet = await Native.ReadTradeSheetAsync(Boundary.Identity(state.Snapshot), call).ConfigureAwait(false);
        IReadOnlyList<TradeLine> lines;
        IReadOnlyList<CargoItem> goods;
        try
        {
            (lines, goods) = await MissionPurchasesAsync(state, review, m, sheet, caravan, call).ConfigureAwait(false);
        }
        catch (Pol.TradeMissionFoodDemandUnknownException)
        {
            return new RoundsTradeResult { Verdict = FieldUnavailable("food_plan") };
        }
        if (sheet.Trader != trader || !sheet.CanTradeNow || lines.Count == 0)
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
        var (silver, _, known) = TradeSheetSilver(sheet.Rows);
        if (!SameTradeLines(lines, TradeStagedLines(sheet)) || !sheet.ColonyCanAfford
            || !Pol.TradePolicyRules.TradeMissionPaymentSafe(m.SilverBudget, MissionOptional(silver, known), MissionOptional(sheet.Balance, sheet.BalanceKnown))
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
    internal async Task<(IReadOnlyList<TradeLine> Lines, IReadOnlyList<CargoItem> Goods)> MissionPurchasesAsync(ControlState state, Sto.Rounds review, TradeMission m, TradeSheetRead sheet, CaravanJourney caravan, CancellationToken ct)
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
                Tick = new Tick(reply.Outcome?.Observed?.Context?.Tick ?? 0),
            },
            tables,
            catalog);
        Reviewer.PlanFood(ref projection);
        var targets = Reviewer.ResourceTargets(state.Snapshot);
        var nutrition = new Dictionary<string, Fact<double>>(StringComparer.Ordinal);
        foreach (var item in m.Demand)
        {
            nutrition[item.Definition] = MissionCatalogStat(catalog, item.Definition, CatalogStats.Nutrition);
        }
        var rows = new List<Pol.MissionPurchaseRow>();
        foreach (var row in sheet.Rows)
        {
            rows.Add(new Pol.MissionPurchaseRow
            {
                LineID = row.LineId,
                DefName = row.DefName,
                TraderCount = row.TraderCount,
                Pawn = row.Pawn is not null,
                Currency = row.Currency,
                BuyPrice = MissionOptional(row.BuyPrice, row.BuyPriceKnown),
                WillTrade = MissionOptional(row.TraderWillTrade, row.TraderWillTradeKnown),
                UnitMass = MissionCatalogStat(catalog, row.DefName, CatalogStats.Mass),
            });
        }
        var (silver, _, known) = TradeSheetSilver(sheet.Rows);
        return Pol.TradePolicyRules.PlanTradeMissionPurchases(new Pol.MissionPurchaseRequest
        {
            Mission = m,
            Targets = targets,
            Stock = projection.Facts.Resources,
            Food = projection.Facts.FoodPlan,
            FoodTargetDays = Reviewer.Policy.FoodTargetDays,
            Nutrition = nutrition,
            Rows = rows,
            Silver = MissionOptional(silver, known),
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
