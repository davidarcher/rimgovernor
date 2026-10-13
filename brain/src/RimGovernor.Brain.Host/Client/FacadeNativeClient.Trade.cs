using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using static RimGovernor.Brain.Bridge.Contracts.BridgeFunctions;
using Domain = RimGovernor.Brain.Domain;
using PbDefs = RimGovernor.Contracts.Wire.Defs;

namespace RimGovernor.Brain.Host;

// Go trade_acquisition.go, trade_session.go, trade_sheet.go, traders.go and the ValidateTradeTarget of trade_target.go.
internal sealed partial class FacadeNativeClient
{
    /// <summary>
    /// Go <c>Client.ReadTradeAcquisition</c>: the comms-console, passing-ship and trade-request census. Optional native
    /// facts stay absent: an absent cost, relation, eligibility or sampled timing is never a zero or permission to
    /// dispatch. <paramref name="pack"/> is the existing formation intent, used solely as a calculation input.
    /// </summary>
    internal async Task<(PbObservations.TradeAcquisition Value, BridgeResult Receipt)> ReadTradeAcquisitionAsync(PbCommon.Identity identity, PbOperations.FormCaravanIntent? pack, CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        var request = new PbObservations.TradeAcquisitionRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity }, Pack = pack };
        var reply = await Facade.ReadTradeAcquisitionAsync(request, cancellationToken).ConfigureAwait(false);
        BuildingUnknown(reply);
        var outcome = reply.Outcome;
        if (outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }

        if (outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }

        if (outcome?.Observed is { } observed)
        {
            TradeValidateAcquisition(observed, identity);
            cancellationToken.ThrowIfCancellationRequested();
            return (observed, NoReceipt);
        }

        throw Contract("trade acquisition outcome missing");
    }

    private static void TradeValidateAcquisition(PbObservations.TradeAcquisition? v, PbCommon.Identity identity)
    {
        if (v is null)
        {
            throw Contract("trade acquisition missing");
        }

        ValidateContext(v.Context);
        if (!SameIdentity(v.Context!.Identity, identity))
        {
            throw Contract("trade acquisition world mismatch");
        }

        if (v.Consoles.Count + v.PassingShips.Count + v.Requests.Count + v.Arrivals.Count + v.CommsWork.Count > TradersRead.MaximumRows)
        {
            throw Contract("trade acquisition exceeds bound");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.Consoles)
        {
            if (row is null || !IsValidId(row.Id ?? "") || !seen.Add(row.Id!))
            {
                throw Contract("invalid trade console");
            }

            TradeNegotiatorIds(row.NegotiatorIds);
        }

        foreach (var row in v.PassingShips)
        {
            if (row is null || !IsValidId(row.Id ?? "") || seen.Contains(row.Id!) || row.Trader is null
                || !Diagnostic(row.TraderKind is not null, row.TraderKind ?? "") || !Diagnostic(row.FactionId is not null, row.FactionId ?? "")
                || (row.DepartureTick is { } departure && departure < (v.Context.Tick ?? 0)))
            {
                throw Contract("invalid passing ship");
            }

            seen.Add(row.Id!);
            if (row.Trader != true && (row.TraderKind is not null || row.CanTrade is not null))
            {
                throw Contract("trader facts on non-trader ship");
            }
        }

        seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.Arrivals)
        {
            if (row is null || !IsValidId(row.FactionId ?? "") || !IsValidId(row.TraderKind ?? "")
                || (row.Kind != PbCommon.TradeRequestKind.Caravan && row.Kind != PbCommon.TradeRequestKind.Orbital)
                || row.ArrivalTick is null || row.ArrivalTick < 0 || (row.RetryTicks is { } retry && retry < 0))
            {
                throw Contract("invalid trade request arrival");
            }
        }

        foreach (var row in v.CommsWork)
        {
            if (row is null || !IsValidId(row.NegotiatorId ?? "") || !IsValidId(row.ConsoleId ?? "") || !IsValidId(row.FactionId ?? "") || row.JobId is null || row.JobId < 0)
            {
                throw Contract("invalid queued trade comms work");
            }
        }

        foreach (var row in v.Requests)
        {
            if (row is null || !IsValidId(row.FactionId ?? "") || !IsValidId(row.TraderKind ?? "")
                || (row.Kind != PbCommon.TradeRequestKind.Caravan && row.Kind != PbCommon.TradeRequestKind.Orbital))
            {
                throw Contract("invalid trade request option");
            }

            var key = (row.FactionId ?? "") + "/" + row.Kind + "/" + (row.TraderKind ?? "");
            if (!seen.Add(key))
            {
                throw Contract("duplicate trade request option");
            }

            if ((row.GoodwillCost is { } cost && cost < 0)
                || (row.CooldownRemainingTicks is { } cooldown && cooldown < 0)
                || (row.ArrivalMinTicks is { } minimum && minimum < 0)
                || (row.ArrivalMaxTicks is not null && row.ArrivalMaxTicks.GetValueOrDefault() < row.ArrivalMinTicks.GetValueOrDefault())
                || (row.Goodwill is { } goodwill && (goodwill < -100 || goodwill > 100)))
            {
                throw Contract("invalid trade request economics");
            }

            if (row.RelationAfterPayment is { } relation && relation != "Ally" && relation != "Neutral" && relation != "Hostile")
            {
                throw Contract("invalid trade request relation");
            }

            TradeNegotiatorIds(row.NegotiatorIds);
            if (row.Eligible == true
                && (row.GoodwillCost is null || row.RelationAfterPayment is null || row.RelationAfterPayment != "Ally"
                    || row.CooldownRemainingTicks is null || row.CooldownRemainingTicks != 0 || row.LastRequestTick is null
                    || row.NegotiatorIds.Count == 0 || v.Consoles.Count == 0
                    || (row.Kind == PbCommon.TradeRequestKind.Orbital && (v.OrbitalAvailable is null || v.OrbitalAvailable != true || v.PassingShips.Count != 0))))
            {
                throw Contract("unsafe trade request eligibility");
            }
        }

        if (v.Pack is { } estimate)
        {
            foreach (var value in new[] { estimate.MassUsage, estimate.MassCapacity, estimate.FoodDays, estimate.FoodRotDays })
            {
                if (value is { } number && (double.IsNaN(number) || double.IsInfinity(number) || number < 0))
                {
                    throw Contract("invalid trade pack estimate");
                }
            }

            foreach (var route in new[] { estimate.Outbound, estimate.Home })
            {
                if (route is not null && (route.Destination is null || route.Destination < 0 || (route.EstimatedTicks is { } ticks && ticks < 0)))
                {
                    throw Contract("invalid trade pack route");
                }
            }
        }
    }

    private static void TradeNegotiatorIds(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!IsValidId(id) || !seen.Add(id))
            {
                throw Contract("invalid trade negotiator IDs");
            }
        }
    }

    /// <summary>Go <c>ValidateTradeTarget</c>: the closed participant variants against the observation identity.</summary>
    private static void TradeValidateTarget(PbCommon.TradeTarget? target, PbCommon.Identity identity)
    {
        ValidateIdentity(identity);
        if (target is null)
        {
            throw Contract("trade target missing");
        }

        switch (target.Kind)
        {
            case PbCommon.TradeTargetKind.MapTraderCase map:
                if (map.Value is null || !IsValidId(map.Value.TraderId ?? ""))
                {
                    throw Contract("invalid map trade target");
                }

                break;
            case PbCommon.TradeTargetKind.SettlementCase settlement:
                if (settlement.Value is null || !IsValidId(settlement.Value.SettlementId ?? "") || !IsValidId(settlement.Value.CaravanId ?? "")
                    || (settlement.Value.SettlementId ?? "") == (settlement.Value.CaravanId ?? ""))
                {
                    throw Contract("invalid settlement trade target");
                }

                break;
            case PbCommon.TradeTargetKind.OrbitalShipCase ship:
                if (ship.Value is null || !IsValidId(ship.Value.ShipId ?? ""))
                {
                    throw Contract("invalid orbital trade target");
                }

                break;
            default:
                throw Contract("trade target arm missing");
        }
    }

    /// <summary>
    /// Go <c>Client.ReadTradeSession</c>: which negotiator is walking to or trading with which trader. The read issues no
    /// order: a walk the game ended simply reads as absent.
    /// </summary>
    internal async Task<(TradeSessionRead Value, BridgeResult Receipt)> ReadTradeSessionAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        var request = new PbObservations.TradeSessionRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } };
        var reply = await Facade.ReadTradeSessionAsync(request, cancellationToken).ConfigureAwait(false);
        BuildingUnknown(reply);
        var outcome = reply.Outcome;
        if (outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }

        if (outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }

        var session = outcome?.Observed ?? throw Contract("trade session outcome missing");
        ValidateContext(session.Context);
        if (!SameIdentity(session.Context!.Identity, identity))
        {
            throw Contract("trade session world mismatch");
        }

        var trader = BuildingRuntimePackage.TradeParticipantOf(session.Target).Key();
        var negotiator = session.NegotiatorId ?? "";
        var open = session.Open ?? false;
        if ((session.Target is null) != (session.NegotiatorId is null) || (open && session.Target is null))
        {
            throw Contract("trade session names half a pair");
        }

        if (session.Target is not null)
        {
            var valid = true;
            try
            {
                TradeValidateTarget(session.Target, identity);
            }
            catch (BridgeContractException)
            {
                valid = false;
            }

            if (!valid || !IsValidId(negotiator) || (trader.Length != 0 && trader == negotiator))
            {
                throw Contract("invalid trade session pair");
            }
        }

        return (new TradeSessionRead { Target = session.Target, Context = session.Context, Trader = trader, Negotiator = negotiator, Open = open }, NoReceipt);
    }

    /// <summary>
    /// Go <c>Client.ReadTradeSheet</c>: the whole trade sheet of the live session (always with untradeable rows and no name
    /// filter). A reply that is not complete and well formed is refused rather than returned partial.
    /// </summary>
    internal async Task<(TradeSheetRead Value, BridgeResult Receipt)> ReadTradeSheetAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        var request = new PbObservations.TradeSheetRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            IncludeUntradeable = true,
            OnlyChanged = false,
        };
        var reply = await Facade.ReadTradeSheetAsync(request, cancellationToken).ConfigureAwait(false);
        BuildingUnknown(reply);
        var outcome = reply.Outcome;
        if (outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }

        if (outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }

        var sheet = outcome?.Observed ?? throw Contract("trade sheet outcome missing");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<TradeSheetRow>();
        var header = TradeSheetPage(sheet, identity, seen, rows);
        cancellationToken.ThrowIfCancellationRequested();
        return (header with { Rows = rows }, NoReceipt);
    }

    /// <summary>Go <c>tradeSheetPage</c>: validates the sheet's header and appends its rows.</summary>
    private static TradeSheetRead TradeSheetPage(PbObservations.TradeSheet? v, PbCommon.Identity identity, HashSet<string> seen, List<TradeSheetRow> rows)
    {
        if (v is null || v.Snapshot is null)
        {
            throw Contract("trade sheet snapshot missing");
        }

        ValidateContext(v.Snapshot.Context);
        if (!SameIdentity(v.Snapshot.Context!.Identity, identity) || !IsValidId(v.SessionId ?? ""))
        {
            throw Contract("trade sheet world or token mismatch");
        }

        if (!Diagnostic(v.DealSignature is not null, v.DealSignature ?? ""))
        {
            throw Contract("trade sheet deal signature invalid");
        }

        TradeValidateTarget(v.Target, identity);
        var header = new TradeSheetRead
        {
            Context = v.Snapshot.Context,
            SessionId = v.SessionId ?? "",
            Target = v.Target,
            Trader = BuildingRuntimePackage.TradeParticipantOf(v.Target).Key(),
            Negotiator = v.Negotiator?.Id ?? "",
            GiftMode = v.GiftMode ?? false,
            CanTradeNow = v.CanTradeNow ?? false,
            Balance = v.Balance ?? 0,
            BalanceKnown = v.Balance is not null,
            ColonyCanAfford = v.ColonyCanAfford ?? false,
            TraderHasSilver = v.TraderHasEnoughSilver ?? false,
            DealSignature = v.DealSignature ?? "",
            FavorCurrency = v.CurrencyKind == PbDefs.TradeCurrency.Favor,
        };
        foreach (var line in v.Lines)
        {
            var row = TradeSheetRowOf(line);
            if (!seen.Add(row.LineId))
            {
                throw Contract("duplicate trade sheet line id");
            }

            rows.Add(row);
        }

        return header;
    }

    /// <summary>Go <c>tradeSheetRow</c>: one validated trade sheet line.</summary>
    private static TradeSheetRow TradeSheetRowOf(PbObservations.TradeLine? v)
    {
        // The favor currency row has no ThingDef, so its definition is empty.
        var favor = v is not null && v.Favor == true && v.Currency == true;
        if (v is null || !IsValidId(v.LineId ?? "") || v.Definition is null || (!IsValidId(v.Definition.DefName ?? "") && !(favor && (v.Definition.DefName ?? "") == "")))
        {
            throw Contract("invalid trade sheet line");
        }

        if (!Diagnostic(v.Stuff is not null, v.Stuff ?? ""))
        {
            throw Contract("trade sheet line text invalid");
        }

        if ((v.ColonyCount ?? 0) < 0 || (v.TraderCount ?? 0) < 0)
        {
            throw Contract("trade sheet line count negative");
        }

        var pawn = v.Pawn ?? false;
        if (!pawn && (v.Skills.Count != 0 || v.ViolenceCapable is not null || (v.PawnId ?? "") != "" || v.PawnGender is not null
            || v.GuestStatus is not null || v.PrisonerSecure is not null || v.PawnDowned is not null || v.ExtraHomeFaction is not null || v.ExtraHostFaction is not null))
        {
            throw Contract("pawn facts on a non-pawn trade line");
        }

        if ((pawn && (v.ThingId is not null || v.Quality is not null)) || !Diagnostic(v.ThingId is not null, v.ThingId ?? "")
            || (v.Quality is { } quality && !ValidQuality(quality)) || !Diagnostic(v.ZoneId is not null, v.ZoneId ?? "")
            || (v.HitPointsFraction is { } hitPoints && !(hitPoints >= 0 && hitPoints <= 1)))
        {
            throw Contract("invalid trade sheet thing facts");
        }

        if (!Diagnostic(v.PawnId is not null, v.PawnId ?? "") || !Diagnostic(v.PawnGender is not null, v.PawnGender ?? "") || !Diagnostic(v.GuestStatus is not null, v.GuestStatus ?? "")
            || (v.ExtraHomeFaction is not null && !IsValidId(v.ExtraHomeFaction.Id ?? "")) || (v.ExtraHostFaction is not null && !IsValidId(v.ExtraHostFaction.Id ?? "")))
        {
            throw Contract("trade sheet pawn id invalid");
        }

        var skills = new List<TradeSheetSkill>();
        foreach (var skill in v.Skills)
        {
            if (!IsValidId(skill.DefName ?? "") || skill.Level is null || (skill.Passion is { } passion && PassionName(passion).Length == 0))
            {
                throw Contract("invalid trade sheet pawn skill");
            }

            skills.Add(new TradeSheetSkill(skill.DefName!, skill.Level.Value, PassionName(skill.Passion.GetValueOrDefault()), skill.Disabled ?? false));
        }

        return new TradeSheetRow
        {
            PawnId = v.PawnId ?? "",
            PawnGender = v.PawnGender ?? "",
            Skills = skills,
            ViolenceCapable = v.ViolenceCapable ?? false,
            ViolenceCapableKnown = v.ViolenceCapable is not null,
            GuestStatus = v.GuestStatus ?? "",
            PrisonerSecure = v.PrisonerSecure ?? false,
            PrisonerSecureKnown = v.PrisonerSecure is not null,
            PawnDowned = v.PawnDowned ?? false,
            PawnDownedKnown = v.PawnDowned is not null,
            ExtraHomeFaction = v.ExtraHomeFaction?.Id ?? "",
            ExtraHostFaction = v.ExtraHostFaction?.Id ?? "",
            ThingId = v.ThingId ?? "",
            Quality = v.Quality ?? 0,
            QualityKnown = v.Quality is not null,
            HitPoints = v.HitPointsFraction ?? 0,
            HitPointsKnown = v.HitPointsFraction is not null,
            ZoneId = v.ZoneId ?? "",
            LineId = v.LineId!,
            DefName = v.Definition.DefName ?? "",
            Stuff = v.Stuff ?? "",
            ColonyCount = v.ColonyCount ?? 0,
            TraderCount = v.TraderCount ?? 0,
            BuyPrice = v.BuyPrice ?? 0,
            SellPrice = v.SellPrice ?? 0,
            MarketValue = v.MarketValue ?? 0,
            TransferNow = v.TransferCount ?? 0,
            MinimumCount = v.MinimumCount ?? 0,
            MaximumCount = v.MaximumCount ?? 0,
            BuyPriceKnown = v.BuyPrice is not null,
            SellPriceKnown = v.SellPrice is not null,
            TraderWillTrade = v.TraderWillTrade ?? false,
            TraderWillTradeKnown = v.TraderWillTrade is not null,
            Currency = v.Currency ?? false,
            CurrencyKnown = v.Currency is not null,
            Pawn = pawn,
            PawnKnown = v.Pawn is not null,
            Favor = favor,
        };
    }

    /// <summary>
    /// Go <c>Client.ListTraders</c>: the map's trader caravans and eligible negotiators, one complete unfiltered census.
    /// </summary>
    internal async Task<(TradersRead Value, BridgeResult Receipt)> ListTradersAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        var request = new PbObservations.TradersRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } };
        var reply = await Facade.ListTradersAsync(request, cancellationToken).ConfigureAwait(false);
        BuildingUnknown(reply);
        var outcome = reply.Outcome;
        if (outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }

        if (outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }

        var snapshot = outcome?.Observed ?? throw Contract("traders outcome missing");
        var decoded = TradeDecodeTraders(snapshot, identity);
        cancellationToken.ThrowIfCancellationRequested();
        return (decoded, NoReceipt);
    }

    /// <summary>Go <c>decodeTraders</c>: validates and decodes a complete trader census.</summary>
    internal static TradersRead TradeDecodeTraders(PbObservations.TradersSnapshot? snapshot, PbCommon.Identity identity)
    {
        if (snapshot is null)
        {
            throw Contract("traders snapshot missing");
        }

        ValidateContext(snapshot.Context);
        if (!SameIdentity(snapshot.Context!.Identity, identity))
        {
            throw Contract("traders world mismatch");
        }

        if (snapshot.Traders.Count + snapshot.Negotiators.Count > TradersRead.MaximumRows)
        {
            throw Contract("traders census exceeds bound");
        }

        var traders = new List<TraderRead>();
        var negotiators = new List<NegotiatorRead>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in snapshot.Traders)
        {
            if (row is null || row.Entity is null || !IsValidId(row.Entity.Id ?? ""))
            {
                throw Contract("invalid trader row");
            }

            if (!Diagnostic(row.Kind is not null, row.Kind ?? "") || !OptionalRef(row.Faction) || !Diagnostic(row.Reason is not null, row.Reason ?? "")
                || row.CanTrade is null || row.Travelling is null)
            {
                throw Contract("invalid trader row text");
            }

            var cell = row.Entity.Position;
            if (cell is null || cell.X is null || cell.Z is null || cell.X < 0 || cell.Z < 0)
            {
                throw Contract("trader position missing");
            }

            if (!seen.Add(row.Entity.Id!))
            {
                throw Contract("duplicate trader");
            }

            var participant = new Domain.TradeParticipant(row.Orbital == true ? Domain.TradeParticipantKind.Orbital : Domain.TradeParticipantKind.Map, row.Entity.Id!);
            traders.Add(new TraderRead
            {
                Participant = participant,
                Id = participant.Key(),
                Token = row.TraderSnapshot?.Token ?? "",
                Kind = row.Kind ?? "",
                Faction = row.Faction?.Id ?? "",
                CanTrade = row.CanTrade.Value,
                Travelling = row.Travelling.Value,
                Reason = row.Reason ?? "",
                GoodsStacks = row.GoodsStacks ?? 0,
                X = cell.X.Value,
                Z = cell.Z.Value,
            });
        }

        foreach (var row in snapshot.Negotiators)
        {
            if (row is null || !IsValidId(row.Id ?? ""))
            {
                throw Contract("invalid negotiator row");
            }

            if (!seen.Add(row.Id!))
            {
                throw Contract("duplicate negotiator");
            }

            negotiators.Add(new NegotiatorRead(row.Id!, row.Id!));
        }

        return new TradersRead(snapshot.Context, traders, negotiators);
    }
}
