namespace RimGovernor.Brain.BuildingRuntime;

public interface ITradeMissionNative
{
    (Bridge.WorldProgressionRead Read, BridgeResult Result) ReadWorldProgression(CancellationToken ct, CommonPb.Identity identity, bool full);

    (IReadOnlyDictionary<string, long> Cargo, BridgeResult Result) ReadPawnCargo(CancellationToken ct, CommonPb.Identity identity, IReadOnlyList<string> pawns);
}

public static partial class TradeMissions
{
    /// <summary>
    /// Requires actual cargo held by the returned native crew. An accept receipt, caravan disappearance, or
    /// existing home stock proves none.
    /// </summary>
    public static bool TradeMissionDelivered(TradeMission m, Bridge.WorldProgressionRead world, IReadOnlyDictionary<string, long> cargo)
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
    public static bool TradeMissionReturnSafe(TradeMission m, Bridge.CaravanJourney? caravan)
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
        return Pol.TradeMissionFacts.TradeMissionReturnSafe(input);
    }
}

internal static partial class BuildingRuntimePackage
{
    internal static Bridge.CaravanJourney? MissionCaravan(TradeMission m, Bridge.WorldProgressionRead world)
    {
        Bridge.CaravanJourney? found = null;
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

    internal static bool MissionAttempted(Sto.StandardState p, string method) =>
        p.History.Any(row => row.MethodId.Value.StartsWith(method + "-", StringComparison.Ordinal));

    internal static Fact<T> MissionOptional<T>(T v, bool known) => known ? Fact.Known(v) : Fact.Unknown<T>();

    internal static Fact<double> MissionCatalogStat(Bridge.DefinitionCatalog catalog, string def, string stat)
    {
        try
        {
            return Fact.Known((double)catalog.StatValue(def, "", stat));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Fact.Unknown<double>();
        }
    }
}

public sealed partial class RoundsTradePlanner
{
    internal (RoundsTradeResult Result, bool Handled) Mission(CancellationToken call, CancellationToken epoch, ControlState state, Sto.Rounds review, StepArbiter arbiter)
    {
        if (!state.Enabled)
        {
            return (new RoundsTradeResult { Verdict = BuildingReasonDisabled }, true);
        }
        if (Native is not ITradeMissionNative source)
        {
            return (new RoundsTradeResult(), false);
        }
        var player = Reviewer.Player;
        var projects = player.Journal.OpenStandards(call, Concerns.TradeMission);
        if (projects.Count == 0)
        {
            return CreateMission(call, state, review);
        }
        var project = projects[0];
        var m = project.Standard.Mission ?? throw new DomainException("standard carries no trade mission");
        var (world, _) = source.ReadWorldProgression(call, BoundaryApi.Identity(state.Snapshot), false);
        if (!TradeMissionCurrentRead(state.Snapshot, world))
        {
            throw new ControlException("mission: world progression read is not current");
        }
        project = player.Journal.ReviewStandard(call, project.Standard.Id, project.Revision, state.Snapshot, new Tick(world.Context?.Tick ?? 0), Finding.Unmet);
        // Shared Hands owns dispatch and uncertain work. Never create a second formation or purchase while its
        // method remains live.
        foreach (var method in project.Methods)
        {
            var plan = player.Journal.LoadPlan(call, method.Plan);
            if (Sto.PlanState.PlanOpen(plan))
            {
                return (new RoundsTradeResult { Verdict = BuildingReasonExistingWork, NativeWorkTicks = TradeWalkTicks }, true);
            }
        }
        var caravan = MissionCaravan(m, world);
        if (m.Phase == TradeMissionPhase.Planned)
        {
            var id = Reviewer.AdmitTradeMissionDeparture(call, epoch, state, project, world, arbiter);
            return (new RoundsTradeResult { Verdict = BuildingReasonAdmitted, Plan = id, NativeWorkTicks = TradeWalkTicks }, true);
        }
        if (m.Phase is TradeMissionPhase.Departing or TradeMissionPhase.Outbound)
        {
            var (phase, _, known) = TradeMissions.TradeMissionDepartureEvidence(m, world);
            if (known && phase != m.Phase)
            {
                m = m with { Phase = phase };
                return MissionRecord(call, project, m);
            }
        }
        if (caravan is null)
        {
            if (TradeMissionCrewAtHome(m, world))
            {
                var crew = m.Crew.Select(id => id.Value).ToList();
                var (cargo, _) = source.ReadPawnCargo(call, BoundaryApi.Identity(state.Snapshot), crew);
                if (TradeMissions.TradeMissionDelivered(m, world, cargo))
                {
                    m = m with { Phase = TradeMissionPhase.Delivered };
                }
                else if (!m.PurchaseCommitted)
                {
                    m = m with { Phase = TradeMissionPhase.Ended };
                }
                else
                {
                    // Vanilla unloads individually after home entry. If a read missed held cargo, delivery
                    // remains unknown; loose stock is no substitute.
                    return (new RoundsTradeResult { Verdict = BuildingReasonExistingWork }, true);
                }
                var (recorded, _) = MissionRecord(call, project, m);
                var saved = player.Journal.LoadStandard(call, project.Standard.Id);
                player.Journal.ReviewStandard(call, saved.Standard.Id, saved.Revision, state.Snapshot, new Tick(world.Context?.Tick ?? 0), Finding.Met);
                return (recorded, true);
            }
            // Crew explicitly dead or absent everywhere ends without delivery.
            var visible = false;
            foreach (var row in world.Maps)
            {
                visible = visible || m.Crew.Any(id => row.PawnIds.Contains(id.Value));
            }
            foreach (var row in world.Assemblies)
            {
                visible = visible || m.Crew.Any(id => row.PawnIds.Contains(id.Value));
            }
            foreach (var row in world.Caravans)
            {
                visible = visible || m.Crew.Any(id => row.PawnIds.Contains(id.Value));
            }
            if (!visible)
            {
                m = m with { Phase = TradeMissionPhase.Ended };
                var (recorded, _) = MissionRecord(call, project, m);
                var saved = player.Journal.LoadStandard(call, project.Standard.Id);
                player.Journal.ReviewStandard(call, saved.Standard.Id, saved.Revision, state.Snapshot, new Tick(world.Context?.Tick ?? 0), Finding.Met);
                return (recorded, true);
            }
            return (new RoundsTradeResult { Verdict = BuildingReasonExistingWork, NativeWorkTicks = TradeWalkTicks }, true);
        }
        if (m.Phase == TradeMissionPhase.Buying)
        {
            return MissionTrade(call, epoch, state, review, project, m, caravan);
        }
        if (m.Phase == TradeMissionPhase.Returning)
        {
            var (session, _) = Native.ReadTradeSession(call, BoundaryApi.Identity(state.Snapshot));
            var target = new TradeParticipant(TradeParticipantKind.Settlement, m.Settlement, caravan.Id);
            if (session.Trader == target.Key())
            {
                if (MissionAttempted(project, "trade-end"))
                {
                    return (new RoundsTradeResult { Verdict = BuildingReasonExistingWork }, true);
                }
                var value = Trade.NewTradeEnd(target.Key(), m.Negotiator, TradeEndKind.Cancel, false).WithParticipant(target);
                return MissionTradeCommit(call, epoch, state, project, m, value);
            }
            if (caravan.Moving && caravan.Destination == m.HomeTile)
            {
                // Enter uses UnloadIndividually: inventory survives entry, then pawn jobs unload it. Observe
                // each final travel tick before those jobs can erase the existing holder evidence.
                var ticks = TradeWalkTicks;
                foreach (var route in caravan.HomeRoutes)
                {
                    if (route.DestinationTile == m.HomeTile && route.EstimatedTicksKnown && route.EstimatedTicks <= TradeWalkTicks)
                    {
                        ticks = 1;
                    }
                }
                return (new RoundsTradeResult { Verdict = BuildingReasonExistingWork, NativeWorkTicks = ticks }, true);
            }
            if (!TradeMissions.TradeMissionReturnSafe(m, caravan))
            {
                return (new RoundsTradeResult { Verdict = BuildingReasonExistingWork, NativeWorkTicks = TradeWalkTicks }, true);
            }
            CaravanDeparture? departure;
            var departureValid = CaravanDeparture.TryCreate(m.Crew, [], m.HomeTile, out departure);
            if (MissionAttempted(project, "trade-return"))
            {
                return (new RoundsTradeResult { Verdict = BuildingReasonExistingWork, NativeWorkTicks = TradeWalkTicks }, true);
            }
            if (!departureValid)
            {
                throw new DomainException("invalid caravan departure");
            }
            var id = PlanIds.MintPlanId();
            var action = DomainAction.NewCaravanDepartureAction(new ActionId(id.Value + "-home"), departure!);
            return MissionCommit(call, epoch, state, project, m, id, action, "trade-return");
        }
        return (new RoundsTradeResult { Verdict = BuildingReasonExistingWork, NativeWorkTicks = TradeWalkTicks }, true);
    }

    internal (RoundsTradeResult Result, bool Handled) MissionRecord(CancellationToken ct, Sto.StandardState p, TradeMission m)
    {
        Reviewer.Player.Journal.RecordStandard(ct, p.Standard.Id, p.Revision, m);
        if (m.Phase is TradeMissionPhase.Delivered or TradeMissionPhase.Ended)
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
        return (new RoundsTradeResult { Verdict = BuildingReasonExistingWork, NativeWorkTicks = TradeWalkTicks }, true);
    }

    internal (RoundsTradeResult Result, bool Handled) MissionCommit(
        CancellationToken call,
        CancellationToken epoch,
        ControlState state,
        Sto.StandardState p,
        TradeMission m,
        PlanId id,
        DomainAction a,
        string method)
    {
        var plan = new PlanSpec(id, new PlanRevision(1), [a]);
        Reviewer.Player.Current(call, epoch);
        if (Reviewer.Player.Session.State() != state)
        {
            throw new ControlException("missionCommit: session state changed");
        }
        Reviewer.Player.Journal.CommitMethodRecord(call, p.Standard.Id, p.Revision, new MethodId(string.Create(CultureInfo.InvariantCulture, $"{method}-{p.Admitted}")), "", plan, m);
        return (new RoundsTradeResult { Verdict = BuildingReasonAdmitted, Plan = id, NativeWorkTicks = TradeWalkTicks }, true);
    }

    internal (RoundsTradeResult Result, bool Handled) MissionTradeCommit(CancellationToken call, CancellationToken epoch, ControlState state, Sto.StandardState p, TradeMission m, Trade value)
    {
        var id = PlanIds.MintPlanId();
        var a = DomainAction.NewTradeAction(new ActionId(id.Value + "-trade"), value);
        return MissionCommit(call, epoch, state, p, m, id, a, "trade-" + Wire.Name(value.Kind));
    }

    internal (RoundsTradeResult Result, bool Handled) MissionTrade(
        CancellationToken call,
        CancellationToken epoch,
        ControlState state,
        Sto.Rounds review,
        Sto.StandardState p,
        TradeMission m,
        Bridge.CaravanJourney caravan)
    {
        var target = new TradeParticipant(TradeParticipantKind.Settlement, m.Settlement, caravan.Id);
        var trader = target.Key();
        var (session, _) = Native.ReadTradeSession(call, BoundaryApi.Identity(state.Snapshot));
        if (m.PurchaseCommitted)
        {
            m = m with { Phase = TradeMissionPhase.Returning };
            return MissionRecord(call, p, m);
        }
        if (session.Trader.Length != 0 && session.Trader != trader)
        {
            return (new RoundsTradeResult(), false);
        }
        if (session.Trader != trader)
        {
            if (MissionAttempted(p, "trade-open"))
            {
                m = m with { Phase = TradeMissionPhase.Returning };
                return MissionRecord(call, p, m);
            }
            var (census, _) = Native.ListTraders(call, BoundaryApi.Identity(state.Snapshot));
            if (!census.Traders.Any(row => row.Id == trader && row.CanTrade))
            {
                m = m with { Phase = TradeMissionPhase.Returning };
                return MissionRecord(call, p, m);
            }
            var open = Trade.NewTradeOpen(trader, m.Negotiator, false).WithParticipant(target);
            return MissionTradeCommit(call, epoch, state, p, m, open);
        }
        if (!session.Open)
        {
            return (new RoundsTradeResult { Verdict = BuildingReasonExistingWork, NativeWorkTicks = TradeWalkTicks }, true);
        }
        var (sheet, _) = Native.ReadTradeSheet(call, BoundaryApi.Identity(state.Snapshot));
        IReadOnlyList<TradeLine> lines;
        IReadOnlyList<CargoItem> goods;
        try
        {
            (lines, goods) = MissionPurchases(call, state, review, m, sheet, caravan);
        }
        catch (Pol.TradeMissionFoodDemandUnknownException)
        {
            return (new RoundsTradeResult { Verdict = FieldUnavailable("food_plan"), NativeWorkTicks = TradeWalkTicks }, true);
        }
        if (sheet.Trader != trader || !sheet.CanTradeNow || lines.Count == 0)
        {
            m = m with { Phase = TradeMissionPhase.Returning };
            return MissionRecord(call, p, m);
        }
        if (TradeStagedLines(sheet).Count == 0)
        {
            if (MissionAttempted(p, "trade-set_lines"))
            {
                m = m with { Phase = TradeMissionPhase.Returning };
                return MissionRecord(call, p, m);
            }
            var set = Trade.NewTradeSetLines(trader, m.Negotiator, lines, false).WithParticipant(target);
            return MissionTradeCommit(call, epoch, state, p, m, set);
        }
        var (silver, _, known) = TradeSheetSilver(sheet.Rows);
        if (!SameTradeLines(lines, TradeStagedLines(sheet)) || !sheet.ColonyCanAfford
            || !Pol.TradeMissionFacts.TradeMissionPaymentSafe(m.SilverBudget, MissionOptional(silver, known), MissionOptional(sheet.Balance, sheet.BalanceKnown))
            || sheet.DealSignature.Length == 0)
        {
            m = m with { Phase = TradeMissionPhase.Returning };
            return MissionRecord(call, p, m);
        }
        m = m with { ReturnGoods = EquatableArray.From(goods), PurchaseCommitted = true };
        var accept = Trade.NewTradeAccept(trader, m.Negotiator, sheet.DealSignature, false, false).WithParticipant(target);
        return MissionTradeCommit(call, epoch, state, p, m, accept);
    }

    /// <summary>
    /// Feeds live priced candidates through the existing ranker. Demand comes from the current owning
    /// food/resource plans, bounded by saved authorization. It does not publish away goods into home supply
    /// candidates.
    /// </summary>
    internal (IReadOnlyList<TradeLine> Lines, IReadOnlyList<CargoItem> Goods) MissionPurchases(
        CancellationToken ct, ControlState state, Sto.Rounds review, TradeMission m, Bridge.TradeSheetRead sheet, Bridge.CaravanJourney caravan)
    {
        var (reply, _) = Native.ReadColonyFacts(ct, BoundaryApi.Identity(state.Snapshot), false);
        var tables = Native.FrameTables(ct, BoundaryApi.Identity(state.Snapshot));
        if (tables.Catalog is null)
        {
            throw new ControlException("missionPurchases: frame tables carry no catalog");
        }
        var projection = Observation.ColonyProjection.DecodeColony(
            reply,
            new Observation.Identity
            {
                Colony = state.Snapshot.Colony,
                Load = state.Snapshot.Load,
                Map = state.Snapshot.Map,
                Tick = new Tick(reply.Observed?.Context?.Tick ?? 0),
            },
            tables);
        Reviewer.PlanFood(projection);
        var targets = Reviewer.ResourceTargets(state.Snapshot);
        var nutrition = new Dictionary<string, Fact<double>>(StringComparer.Ordinal);
        foreach (var item in m.Demand)
        {
            nutrition[item.Definition] = MissionCatalogStat(tables.Catalog, item.Definition, "Nutrition");
        }
        var rows = new List<Pol.MissionPurchaseRow>();
        foreach (var row in sheet.Rows)
        {
            rows.Add(new Pol.MissionPurchaseRow
            {
                LineId = row.LineId,
                DefName = row.DefName,
                TraderCount = row.TraderCount,
                Pawn = row.Pawn,
                Currency = row.Currency,
                BuyPrice = MissionOptional(row.BuyPrice, row.BuyPriceKnown),
                WillTrade = MissionOptional(row.TraderWillTrade, row.TraderWillTradeKnown),
                UnitMass = MissionCatalogStat(tables.Catalog, row.DefName, "Mass"),
            });
        }
        var (silver, _, known) = TradeSheetSilver(sheet.Rows);
        return Pol.TradeMissionFacts.PlanTradeMissionPurchases(new Pol.MissionPurchaseRequest
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

    internal (RoundsTradeResult Result, bool Handled) CreateMission(CancellationToken ct, ControlState state, Sto.Rounds review)
    {
        foreach (var owner in new[] { "food", "resources" })
        {
            var proposal = Reviewer.TradeAcquisitionPlan(owner).Proposal;
            if (proposal is null || proposal.Option.Kind != Pol.TradeAcquisitionKind.Settlement)
            {
                continue;
            }
            TradeMission? m;
            lock (Reviewer.TradeAcquisition.Mu)
            {
                Reviewer.TradeAcquisition.Missions.TryGetValue(proposal.Option.Id, out m);
            }
            if (m is null || !Reviewer.ClaimTradeAcquisition(owner, proposal.Option.Id))
            {
                continue;
            }
            var id = new StandardId("trade-mission-" + PlanIds.MintPlanId().Value);
            var standard = Standard.NewTradeMissionStandard(id, 1, state.Snapshot, m);
            Reviewer.Player.Journal.SeedStandard(ct, standard);
            return (new RoundsTradeResult { Verdict = BuildingReasonExistingWork, NativeWorkTicks = TradeWalkTicks }, true);
        }
        return (new RoundsTradeResult(), false);
    }
}
