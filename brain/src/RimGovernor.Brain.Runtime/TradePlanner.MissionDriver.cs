namespace RimGovernor.Brain.Runtime;

partial class TradePlanner
{
    /// <summary>
    /// Requires actual cargo held by the returned native crew. An accept receipt, caravan disappearance, or
    /// existing home stock proves none.
    /// </summary>
    public static bool TradeMissionDelivered(TradeMission m, RimGovernor.Brain.GameFacts.Observations.WorldProgressionSnapshot world, IReadOnlyDictionary<string, long> cargo)
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
    public static bool TradeMissionReturnSafe(TradeMission m, RimGovernor.Brain.GameFacts.Observations.CaravanState caravan)
    {
        foreach (var route in caravan.HomeRoutes)
        {
            if (route.Destination == m.HomeTile && route.Route is RimGovernor.Brain.GameFacts.Observations.PackRoute.Reachable { EstimatedTicks: >= 0 and var ticks })
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

partial class TradePlanner
{
    internal static RimGovernor.Brain.GameFacts.Observations.CaravanState? MissionCaravan(TradeMission m, RimGovernor.Brain.GameFacts.Observations.WorldProgressionSnapshot world)
    {
        RimGovernor.Brain.GameFacts.Observations.CaravanState? found = null;
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

public sealed partial class TradePlanner
{
    /// <summary>
    /// The economy/trade-mission Need advances the selected mission in <c>family/economy/trade_missions</c> one phase edge from the
    /// world progression read. A mission leaves the record once delivered or ended, which meets the Need.
    /// </summary>
    internal async Task<TradeResult> MissionAsync(NeedWork work, CancellationToken call)
    {
        var (state, current, verdict) = Current(call);
        if (current is not { } review)
        {
            return new TradeResult { Verdict = verdict };
        }
        if (Native is not GameClient source)
        {
            return new TradeResult { Verdict = FieldUnavailable("world_progression") };
        }
        var player = Reviewer.Player;
        var record = player.Journal.LoadTradeMissions(Player.PlayerWorld(state.Snapshot), call);
        if (record.Missions.Count == 0)
        {
            return new TradeResult { Verdict = BuildingReasonNoDeficit };
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
            return new TradeResult { Verdict = BuildingReasonExistingWork };
        }
        var caravan = MissionCaravan(m, world);
        if (m.Phase == TradeMissionPhase.Planned)
        {
            var (observedPhase, observed) = TradePlanner.TradeMissionDepartureEvidence(m, world);
            if (observed) return MissionRecord(step, m with { Phase = observedPhase }, call);
            var departureId = PlanIds.MintPlanId();
            return MissionCommit(state, step, m with { Phase = TradeMissionPhase.Departing }, departureId,
                DomainAction.NewCaravanDepartureAction(new ActionId(departureId + "-departure"), m.Departure(RimGovernor.Brain.Bridge.GameDefinitionSemantics.Instance)), "trade-depart", call, work.Epoch);
        }
        if (m.Phase is TradeMissionPhase.Departing or TradeMissionPhase.Outbound)
        {
            var (phase, known) = TradePlanner.TradeMissionDepartureEvidence(m, world);
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
                if (TradePlanner.TradeMissionDelivered(m, world, cargo))
                {
                    return MissionRecord(step, m with { Phase = TradeMissionPhase.Delivered }, call);
                }
                if (!m.PurchaseCommitted)
                {
                    return MissionRecord(step, m with { Phase = TradeMissionPhase.Ended }, call);
                }
                // Vanilla unloads individually after home entry. If a read missed held cargo, delivery
                // remains unknown; loose stock is no substitute.
                return new TradeResult { Verdict = BuildingReasonExistingWork };
            }
            // Crew explicitly dead or absent everywhere ends without delivery.
            var visible = world.Maps.Any(row => m.Crew.Any(row.Colonists().Contains))
                || world.Assemblies.Any(row => m.Crew.Any(row.Pawns.Contains))
                || world.Caravans.Any(row => m.Crew.Any(row.Crew().Contains));
            return visible
                ? new TradeResult { Verdict = BuildingReasonExistingWork }
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
                    return new TradeResult { Verdict = BuildingReasonExistingWork };
                }
                var value = Trade.NewTradeEnd(target.Key(), m.Negotiator, TradeEndKind.Cancel, false).WithParticipant(target);
                return MissionTradeCommit(state, step, m, value, call, work.Epoch);
            }
            if (caravan.Course is RimGovernor.Brain.GameFacts.Observations.CaravanCourse.Moving { Destination: var destination } && destination == m.HomeTile)
            {
                return new TradeResult { Verdict = BuildingReasonExistingWork };
            }
            if (!TradePlanner.TradeMissionReturnSafe(m, caravan))
            {
                return new TradeResult { Verdict = BuildingReasonExistingWork };
            }
            var departureValid = CaravanDeparture.TryCreate(m.Crew, [], m.HomeTile, out var departure);
            if (MissionAttempted(step, "trade-return", call))
            {
                return new TradeResult { Verdict = BuildingReasonExistingWork };
            }
            if (!departureValid)
            {
                throw new DomainException("invalid caravan departure");
            }
            var id = PlanIds.MintPlanId();
            var action = DomainAction.NewCaravanDepartureAction(new ActionId(id.Value + "-home"), departure! with { Mode = CaravanDepartureMode.ExistingRoute });
            return MissionCommit(state, step, m, id, action, "trade-return", call, work.Epoch);
        }
        return new TradeResult { Verdict = BuildingReasonExistingWork };
    }

    /// <summary>Whether the mission's <paramref name="phase"/> Method was committed in this load.</summary>
    private bool MissionAttempted(MissionStep step, string phase, CancellationToken ct) =>
        Reviewer.Player.Journal.OwnerMethodPlan(step.Owner, step.Method(phase), ct) is not null;

    /// <summary>Records the mission's new phase; one that is over leaves the record and releases its acquisition claim.</summary>
    internal TradeResult MissionRecord(MissionStep step, TradeMission m, CancellationToken ct)
    {
        Reviewer.Player.Journal.SaveTradeMissions(step.Record.With(step.Id, m), ct);
        return new TradeResult { Verdict = BuildingReasonExistingWork };
    }

    /// <summary>Commits one mission phase as the Need's Method, with the mission intent it carries in the same transaction.</summary>
    internal TradeResult MissionCommit(ControlState state, MissionStep step, TradeMission m, PlanId id, DomainAction a, string phase, CancellationToken call, CancellationToken epoch)
    {
        var plan = new PlanSpec(id, new PlanRevision(1), [a]);
        Reviewer.Player.Current(call, epoch);
        if (Reviewer.Player.Session.State() != state)
        {
            throw new ControlException("missionCommit: session state changed");
        }
        var commit = Reviewer.Player.Journal.CommitTradeMissionMethod(step.Owner.Need, state.Snapshot, step.Method(phase), "", plan, step.Record.With(step.Id, m), call);
        return commit.Outcome == Sto.ClaimOutcome.Admitted
            ? new TradeResult { Verdict = BuildingReasonAdmitted, Plan = id }
            : new TradeResult { Verdict = Verdict.Committed(commit) };
    }

    internal TradeResult MissionTradeCommit(ControlState state, MissionStep step, TradeMission m, Trade value, CancellationToken call, CancellationToken epoch)
    {
        var id = PlanIds.MintPlanId();
        var a = DomainAction.NewTradeAction(new ActionId(id.Value + "-trade"), value);
        return MissionCommit(state, step, m, id, a, "trade-" + Wire.Name(value.Kind), call, epoch);
    }

    internal async Task<TradeResult> MissionTradeAsync(
        ControlState state,
        Sto.ReviewState review,
        MissionStep step,
        TradeMission m,
        RimGovernor.Brain.GameFacts.Observations.CaravanState caravan, CancellationToken call, CancellationToken epoch)
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
            return new TradeResult { Verdict = WaitFor(Pol.Cause.ExistingWork, "trade_session") };
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
        if (session.Engagement is not RimGovernor.Brain.GameFacts.Observations.TradeEngagement.Engaged { Open: true })
        {
            return new TradeResult { Verdict = BuildingReasonExistingWork };
        }
        var sheet = await Native.ReadTradeSheetAsync(Boundary.Identity(state.Snapshot), call).ConfigureAwait(false);
        var (lines, goods) = await MissionPurchasesAsync(state, m, sheet, caravan, call).ConfigureAwait(false);
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
        if (!SameTradeLines(lines, TradeStagedLines(sheet)) || !known || sheet.Balance is not RimGovernor.Brain.GameFacts.Observations.TradeBalance.Counted { ColonyCanAfford: true } balance
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
    /// Feeds live priced rows through the shared purchase rule, bounded by the saved selected-wait authorization.
    /// It does not publish away goods into home supply
    /// candidates.
    /// </summary>
    internal async Task<(IReadOnlyList<TradeLine> Lines, IReadOnlyList<CargoItem> Goods)> MissionPurchasesAsync(ControlState state, TradeMission m, RimGovernor.Brain.GameFacts.Observations.TradeSheet sheet, RimGovernor.Brain.GameFacts.Observations.CaravanState caravan, CancellationToken ct)
    {
        var catalog = await Native.DefinitionCatalogAsync(Boundary.Identity(state.Snapshot), ct).ConfigureAwait(false);
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
            Medicines = catalog.ItemFacts().MedicineTiers(),
            Rows = rows,
            Silver = silver,
            Mass = caravan.MassUsage,
            Capacity = caravan.MassCapacity,
        });
    }

}
