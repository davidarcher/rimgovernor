using RimGovernor.Protocol.Common;
using RimGovernor.Protocol.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go world_progression_snapshot.go, world_progression.go (worldProgressionSelected), world_sites.go, quest_workers.go,
// quest_monument.go and quest_hack_gift.go: the validation and projection of a frame's world progression census.
public static partial class BridgeFunctions
{
    /// <summary>
    /// Go's <c>DecodeWorldProgressionSnapshot</c>: validates the world census already carried by a frame against
    /// that frame's exact identity.
    /// </summary>
    /// <exception cref="BridgeContractException">The census is malformed or names another world.</exception>
    public static WorldProgressionRead DecodeWorldProgressionSnapshot(WorldProgressionSnapshot? snapshot, Identity? identity) =>
        WorldProgressionSelected(snapshot, identity);

    private static bool ValidNonnegative(double value) => value >= 0 && double.IsFinite(value);

    private static bool ValidFiniteNonnegative(bool present, double value) => !present || ValidNonnegative(value);

    /// <summary>Go's <c>validQuestIdentities</c>: valid, distinct identifiers.</summary>
    private static bool ValidQuestIdentities(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!IsValidId(id) || !seen.Add(id))
            {
                return false;
            }
        }
        return true;
    }

    private static bool MonumentCellKnown(Cell? cell) => cell is not null && cell.HasX && cell.HasZ;

    private static bool FiniteBetween(double value, double low, double high) => double.IsFinite(value) && value >= low && value <= high;

    private static WorldProgressionRead WorldProgressionSelected(WorldProgressionSnapshot? v, Identity? identity)
    {
        if (v is null)
        {
            throw Contract("world progression snapshot missing");
        }
        ValidateContext(v.Context);
        if (!SameIdentity(v.Context.Identity, identity))
        {
            throw Contract("world progression world mismatch");
        }
        var maps = new List<WorldMap>(v.Maps.Count);
        var seenMapPawns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.Maps)
        {
            if (row is null || !row.HasId || !row.HasTile || row.Tile < 0 || !row.HasHome)
            {
                throw Contract("invalid world progression map");
            }
            var pawnIds = new List<string>(row.Pawns.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pawn in row.Pawns)
            {
                if (pawn is null || pawn.Pawn is null || !IsValidId(pawn.Pawn.Id) || !seen.Add(pawn.Pawn.Id))
                {
                    throw Contract("invalid or duplicate world progression map pawn");
                }
                // A pawn spawned on two maps at once is not a real game state; treat it as evidence the census
                // cannot be trusted rather than picking one map to believe.
                if (!seenMapPawns.Add(pawn.Pawn.Id))
                {
                    throw Contract("world progression pawn present on multiple maps");
                }
                pawnIds.Add(pawn.Pawn.Id);
            }
            maps.Add(new WorldMap { Id = row.Id, Tile = row.Tile, Home = row.Home, PawnIds = pawnIds, QuestWorkers = ValidatedQuestWorkers(row.QuestWorkers) });
        }
        var seenCaravans = new HashSet<string>(StringComparer.Ordinal);
        var caravans = new List<CaravanJourney>(v.Caravans.Count);
        foreach (var row in v.Caravans)
        {
            if (row is null || row.Caravan is null || !IsValidId(row.Caravan.Id) || !seenCaravans.Add(row.Caravan.Id))
            {
                throw Contract("invalid or duplicate world progression caravan");
            }
            if (!row.HasTile || row.Tile < 0 || row.Destination < 0)
            {
                throw Contract("invalid world progression caravan tile or crew");
            }
            var pawnIds = new List<string>(row.Pawns.Count);
            var pawns = new List<CaravanPawnFact>(row.Pawns.Count);
            var seenPawns = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pawn in row.Pawns)
            {
                if (pawn is null || pawn.Pawn is null || !IsValidId(pawn.Pawn.Id) || !seenPawns.Add(pawn.Pawn.Id))
                {
                    throw Contract("invalid or duplicate world progression caravan pawn");
                }
                pawnIds.Add(pawn.Pawn.Id);
                pawns.Add(new CaravanPawnFact(pawn.Pawn.Id, pawn.HasDead && pawn.Dead, pawn.HasDead, pawn.HasDowned && pawn.Downed, pawn.HasDowned));
            }
            var inventory = new Dictionary<string, long>(row.Inventory.Count, StringComparer.Ordinal);
            var seenDefs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in row.Inventory)
            {
                if (item is null || !item.HasDefName || !seenDefs.Add(item.DefName) || !item.HasUnits || item.Units < 0)
                {
                    throw Contract("invalid or duplicate world progression caravan inventory row");
                }
                inventory[item.DefName] = item.Units;
            }
            var routes = new List<WorldRouteFact>(row.HomeRoutes.Count);
            foreach (var route in row.HomeRoutes)
            {
                if (route is null || !route.HasDestination || route.Destination < 0 || !route.HasReachable)
                {
                    throw Contract("invalid world progression caravan home route");
                }
                var estimated = 0L;
                if (route.HasEstimatedTicks)
                {
                    if (route.EstimatedTicks < 0)
                    {
                        throw Contract("invalid world progression caravan home route ticks");
                    }
                    estimated = route.EstimatedTicks;
                }
                routes.Add(new WorldRouteFact("", route.Destination, route.Reachable, estimated, route.HasEstimatedTicks));
            }
            if (!CombatNumber(OptionalDouble(row.HasFoodDays, row.FoodDays), true))
            {
                throw Contract("invalid world progression caravan food days");
            }
            var settlementRoutes = new List<WorldRouteFact>();
            var seenSettlements = new HashSet<string>(StringComparer.Ordinal);
            foreach (var route in row.SettlementRoutes)
            {
                if (route is null
                    || !IsValidId(route.SettlementId)
                    || !seenSettlements.Add(route.SettlementId)
                    || !route.HasDestination
                    || route.Destination < 0
                    || !route.HasReachable
                    || (route.HasEstimatedTicks && route.EstimatedTicks < 0))
                {
                    throw Contract("invalid caravan settlement route");
                }
                settlementRoutes.Add(new WorldRouteFact(route.SettlementId, route.Destination, route.Reachable, route.EstimatedTicks, route.HasEstimatedTicks));
            }
            if (!CombatNumber(OptionalDouble(row.HasMassUsage, row.MassUsage), true)
                || !CombatNumber(OptionalDouble(row.HasMassCapacity, row.MassCapacity), true)
                || !CombatNumber(OptionalDouble(row.HasFoodRotDays, row.FoodRotDays), true))
            {
                throw Contract("invalid caravan pack facts");
            }
            caravans.Add(new CaravanJourney
            {
                Id = row.Caravan.Id,
                Tile = row.Tile,
                Moving = row.Moving,
                PawnIds = pawnIds,
                Pawns = pawns,
                HomeRoutes = routes,
                Inventory = inventory,
                SettlementRoutes = settlementRoutes,
                MassUsage = OptionalFact(row.HasMassUsage, row.MassUsage),
                MassCapacity = OptionalFact(row.HasMassCapacity, row.MassCapacity),
                FoodRotDays = OptionalFact(row.HasFoodRotDays, row.FoodRotDays),
                Destination = row.HasDestination ? row.Destination : null,
                FoodDays = row.FoodDays,
                FoodDaysKnown = row.HasFoodDays,
            });
        }
        var seenQuests = new HashSet<string>(StringComparer.Ordinal);
        var quests = new List<QuestOffer>(v.Quests.Count);
        foreach (var row in v.Quests)
        {
            quests.Add(DecodeQuest(row, seenQuests));
        }
        var factions = new List<FactionFact>(v.Factions.Count);
        foreach (var row in v.Factions)
        {
            if (row is null || !IsValidId(row.Id))
            {
                throw Contract("invalid world progression faction");
            }
            factions.Add(new FactionFact(row.Id, row.Player, row.Hostile));
        }
        var sites = ValidatedWorldSites(v.Sites);
        var assemblies = ValidatedCaravanAssemblies(v.Assemblies);
        return new WorldProgressionRead { Context = v.Context, Maps = maps, Caravans = caravans, Quests = quests, Factions = factions, Sites = sites, Assemblies = assemblies };
    }

    private static QuestOffer DecodeQuest(QuestState? row, HashSet<string> seenQuests)
    {
        if (row is null || !IsValidId(row.Id) || !seenQuests.Add(row.Id) || QuestStatusName(row.State) == "" || !row.HasRequiresAccepter || !row.HasCanAccept)
        {
            throw Contract("invalid or duplicate world progression quest");
        }
        if (row.Snapshot is null || row.Snapshot.EntityId != row.Id || !IsValidId(row.Snapshot.Token))
        {
            throw Contract("world progression quest CAS token unavailable");
        }
        var choices = new HashSet<uint>();
        var favor = new Dictionary<int, int>();
        foreach (var reward in row.Rewards)
        {
            if (reward is null || reward.Favor < 0 || reward.Psylink < 0 || reward.PermitPoints < 0)
            {
                throw Contract("invalid world progression quest reward");
            }
            var choice = -1;
            if (reward.HasChoiceIndex)
            {
                if (reward.ChoiceIndex > int.MaxValue)
                {
                    throw Contract("invalid quest choice index");
                }
                choice = (int)reward.ChoiceIndex;
                choices.Add(reward.ChoiceIndex);
            }
            if (reward.Favor > 0)
            {
                favor.TryGetValue(choice, out var current);
                if ((long)current + reward.Favor > int.MaxValue)
                {
                    throw Contract("quest favor overflow");
                }
                favor[choice] = current + reward.Favor;
            }
        }
        var eligible = new List<string>(row.EligiblePawns.Count);
        var seenPawns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pawn in row.EligiblePawns)
        {
            if (pawn is null || !IsValidId(pawn.Id) || !seenPawns.Add(pawn.Id))
            {
                throw Contract("invalid or duplicate world progression quest accepter");
            }
            eligible.Add(pawn.Id);
        }
        var departure = new List<string>();
        foreach (var id in row.DeparturePawnIds)
        {
            if (!IsValidId(id))
            {
                throw Contract("invalid quest departure pawn");
            }
            departure.Add(id);
        }
        if (row.HasAskerPawnId && !IsValidId(row.AskerPawnId))
        {
            throw Contract("invalid quest asker");
        }
        if (row.ChoicePartCount < 0)
        {
            throw Contract("invalid quest choice parts");
        }
        var rewards = new List<QuestRewardFact>(row.Rewards.Count);
        foreach (var reward in row.Rewards)
        {
            var items = new List<QuestTradeItemFact>(reward.Items.Count);
            foreach (var item in reward.Items)
            {
                if (item is null || !IsValidId(item.DefName) || !item.HasUnits || item.Units < 0)
                {
                    throw Contract("invalid quest reward item");
                }
                items.Add(new QuestTradeItemFact(item.DefName, item.Units));
            }
            var seenPermits = new HashSet<string>(StringComparer.Ordinal);
            foreach (var permit in reward.Permits)
            {
                if (!IsValidId(permit) || !seenPermits.Add(permit))
                {
                    throw Contract("invalid quest reward permit");
                }
            }
            if ((reward.TitleDef.Length != 0 && !IsValidId(reward.TitleDef)) || (reward.FactionId.Length != 0 && !IsValidId(reward.FactionId)))
            {
                throw Contract("invalid quest reward reference");
            }
            rewards.Add(new QuestRewardFact
            {
                Choice = reward.HasChoiceIndex ? (int)reward.ChoiceIndex : -1,
                Goodwill = reward.Goodwill,
                Psylink = reward.Psylink,
                PermitPoints = reward.PermitPoints,
                Permits = [.. reward.Permits],
                TitleDef = reward.TitleDef,
                FactionId = reward.FactionId,
                Items = items,
            });
        }
        var objectives = new List<QuestObjectiveFact>(row.Objectives.Count);
        foreach (var objective in row.Objectives)
        {
            objectives.Add(DecodeObjective(objective));
        }
        var shuttles = new List<QuestShuttleFact>(row.Shuttles.Count);
        var seenShuttles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var shuttle in row.Shuttles)
        {
            if (shuttle is null || !IsValidId(shuttle.ShuttleId) || !seenShuttles.Add(shuttle.ShuttleId) || shuttle.RequiredColonistCount < 0)
            {
                throw Contract("invalid quest shuttle");
            }
            foreach (var ids in new[] { shuttle.PawnIds, shuttle.LoadedPawnIds, shuttle.PendingPawnIds })
            {
                if (!ValidQuestIdentities(ids))
                {
                    throw Contract("invalid quest shuttle pawn");
                }
            }
            shuttles.Add(new QuestShuttleFact
            {
                Id = shuttle.ShuttleId,
                AutoloadAvailable = shuttle.HasAutoloadAvailable ? shuttle.AutoloadAvailable : null,
                Autoload = shuttle.HasAutoload ? shuttle.Autoload : null,
                Loading = shuttle.HasLoading ? shuttle.Loading : null,
                AllRequiredLoaded = shuttle.HasAllRequiredLoaded ? shuttle.AllRequiredLoaded : null,
                ManualLaunchAvailable = shuttle.HasManualLaunchAvailable ? shuttle.ManualLaunchAvailable : null,
                PawnIds = [.. shuttle.PawnIds],
                LoadedPawnIds = [.. shuttle.LoadedPawnIds],
                PendingPawnIds = [.. shuttle.PendingPawnIds],
                RequiredColonistCount = shuttle.HasRequiredColonistCount ? shuttle.RequiredColonistCount : null,
            });
        }
        var favors = favor.Select(pair => new QuestFavorFact(pair.Key, pair.Value)).OrderBy(fact => fact.Choice).ToList();
        var requests = new List<QuestTradeRequestFact>(row.TradeRequests.Count);
        foreach (var request in row.TradeRequests)
        {
            // A missing resource, count or destination on one row is not malformed evidence (native's own
            // QuestTradeRequest fields are all optional), only a negative count/destination is.
            if (request is null || request.Count < 0 || request.Destination < 0)
            {
                throw Contract("invalid world progression quest trade request");
            }
            requests.Add(new QuestTradeRequestFact(request.Resource, request.Count, request.Destination));
        }
        return new QuestOffer
        {
            Id = row.Id,
            ScriptDef = row.ScriptDef,
            State = QuestStatusName(row.State),
            RequiresAccepter = row.RequiresAccepter,
            CanAccept = row.CanAccept,
            ChoiceCount = choices.Count,
            EligiblePawnIds = eligible,
            SnapshotToken = row.Snapshot.Token,
            DeparturePawnIds = departure,
            FactionId = row.FactionId,
            MapKnown = row.HasMapId,
            MapId = row.MapId,
            ExpiresInTicks = row.HasExpiresInTicks ? row.ExpiresInTicks : null,
            Asker = row.AskerPawnId,
            AskerFactionPlayer = row.HasAskerFactionPlayer ? row.AskerFactionPlayer : null,
            ViolentQuestsAllowed = row.HasViolentQuestsAllowed ? row.ViolentQuestsAllowed : null,
            ThreatPoints = row.HasThreatPoints && ValidNonnegative(row.ThreatPoints) ? row.ThreatPoints : null,
            RewardChoiceParts = row.HasChoicePartCount ? row.ChoicePartCount : null,
            Rewards = rewards,
            Objectives = objectives,
            Shuttles = shuttles,
            Favor = favors,
            TradeRequests = requests,
        };
    }

    private static QuestObjectiveFact DecodeObjective(QuestObjective? objective)
    {
        if (objective is null
            || !objective.HasKind
            || objective.Kind < QuestObjectiveKind.Unknown
            || objective.Kind > QuestObjectiveKind.GiveItems
            || objective.Count < 0
            || objective.Produced < 0
            || objective.DeadlineTicks < 0
            || objective.DurationTicks < 0)
        {
            throw Contract("invalid quest objective");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in objective.PawnIds)
        {
            if (!IsValidId(id) || !seen.Add(id))
            {
                throw Contract("invalid quest objective pawn");
            }
        }
        var (hackTargets, gift, risk) = DecodeQuestHackGift(objective);
        var engine = ValidatedQuestGravEngine(objective.GravEngine);
        var survey = ValidatedQuestSurvey(objective.SurveyScanner);
        QuestWorkload? workload = null;
        if (objective.Workload is { } w)
        {
            if (!w.HasWork || !w.HasRateFactor || !ValidNonnegative(w.Work) || !ValidNonnegative(w.RateFactor))
            {
                throw Contract("invalid quest workload");
            }
            workload = w.Clone();
        }
        if (objective.HasMinimumMood && !FiniteBetween(objective.MinimumMood, 0, 1))
        {
            throw Contract("invalid quest mood threshold");
        }
        var moods = new List<QuestLodgerMoodFact>(objective.LodgerMoods.Count);
        var seenMoods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mood in objective.LodgerMoods)
        {
            if (mood is null || !IsValidId(mood.PawnId) || !seenMoods.Add(mood.PawnId) || (mood.HasMood && !FiniteBetween(mood.Mood, 0, 1)))
            {
                throw Contract("invalid quest lodger mood");
            }
            moods.Add(new QuestLodgerMoodFact(mood.PawnId, mood.HasMood ? mood.Mood : null));
        }
        return new QuestObjectiveFact
        {
            Kind = objective.Kind,
            Def = objective.Def,
            Stuff = objective.Stuff,
            Count = objective.HasCount ? objective.Count : null,
            Produced = objective.HasProduced ? objective.Produced : null,
            DeadlineTicks = objective.HasDeadlineTicks ? objective.DeadlineTicks : null,
            UnmetRequirement = objective.UnmetRequirement,
            PawnIds = [.. objective.PawnIds],
            Active = objective.HasActive ? objective.Active : null,
            MinimumMood = objective.HasMinimumMood ? objective.MinimumMood : null,
            Monument = ValidatedQuestMonument(objective.Monument),
            HackTargets = hackTargets,
            Gift = gift,
            HackRisk = risk,
            GravEngine = engine,
            DurationTicks = objective.HasDurationTicks ? objective.DurationTicks : null,
            SurveyScanner = survey,
            Workload = workload,
            LodgerMoods = moods,
        };
    }

    /// <summary>Go's <c>decodeQuestHackGift</c>.</summary>
    private static (List<QuestHackTarget> Targets, QuestGiftRequest? Gift, QuestHackRisk? Risk) DecodeQuestHackGift(QuestObjective row)
    {
        var targets = new List<QuestHackTarget>(row.HackTargets.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in row.HackTargets)
        {
            if (target is null
                || !IsValidId(target.Id)
                || !seen.Add(target.Id)
                || target.MapId < 0
                || (target.Spawned && !target.HasMapId)
                || !ValidQuestIdentities(target.EligiblePawnIds)
                || target.Hackable is null)
            {
                throw Contract("invalid quest hack target");
            }
            var hackable = target.Hackable;
            if ((hackable.HasProgressPercent && !(double.IsFinite(hackable.ProgressPercent) && hackable.ProgressPercent >= 0))
                || (hackable.HasDefence && !(double.IsFinite(hackable.Defence) && hackable.Defence >= 0)))
            {
                throw Contract("invalid quest hack state");
            }
            targets.Add(target.Clone());
        }
        QuestGiftRequest? giftFact = null;
        if (row.GiftRequest is { } gift)
        {
            if (!IsValidId(gift.RecipientId)
                || gift.Def.Length == 0
                || gift.Remaining < 0
                || gift.MapId < 0
                || !ValidQuestIdentities(gift.PawnIds)
                || !ValidQuestIdentities(gift.HaulingPawnIds)
                || !ValidQuestIdentities(gift.EligiblePawnIds)
                || (gift.EligiblePawnIds.Count > 0 && !gift.HasMapId))
            {
                throw Contract("invalid quest gift request");
            }
            giftFact = gift.Clone();
        }
        QuestHackRisk? riskFact = null;
        if (row.HackRisk is { } risk)
        {
            if (!IsValidId(risk.FactionId))
            {
                throw Contract("invalid quest hack faction");
            }
            riskFact = risk.Clone();
        }
        return (targets, giftFact, riskFact);
    }

    /// <summary>Go's <c>validatedQuestWorkers</c>.</summary>
    private static List<QuestWorker> ValidatedQuestWorkers(IEnumerable<QuestWorker> rows)
    {
        var output = new List<QuestWorker>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row is null || !IsValidId(row.PawnId) || !seen.Add(row.PawnId))
            {
                throw Contract("invalid quest worker");
            }
            if (row.SocialLevel < 0 || row.SocialLevel > 20 || (row.HasNegotiationAbility && !ValidNonnegative(row.NegotiationAbility)))
            {
                throw Contract("invalid quest diplomat");
            }
            if ((row.HasCarryCapacity && !ValidNonnegative(row.CarryCapacity)) || (row.HasCarriedMass && !ValidNonnegative(row.CarriedMass)))
            {
                throw Contract("invalid quest worker carry capacity");
            }
            var stats = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rate in row.Rates)
            {
                if (rate is null || rate.Stat.Length == 0 || !stats.Add(rate.Stat) || !rate.HasRate || !ValidNonnegative(rate.Rate))
                {
                    throw Contract("invalid quest work rate");
                }
            }
            output.Add(row.Clone());
        }
        return output;
    }

    /// <summary>Go's <c>validatedCaravanAssemblies</c>.</summary>
    private static List<CaravanAssemblyFact> ValidatedCaravanAssemblies(IEnumerable<CaravanAssembly> rows)
    {
        var output = new List<CaravanAssemblyFact>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row is null || !IsValidId(row.Id) || !seen.Add(row.Id) || !row.HasMapId || row.MapId < 0)
            {
                throw Contract("invalid caravan assembly");
            }
            var pawnIds = new List<string>();
            foreach (var pawn in row.Pawns)
            {
                if (pawn is null)
                {
                    throw Contract("invalid caravan assembly pawn");
                }
                pawnIds.Add(pawn.Id);
            }
            if (!ValidQuestIdentities(pawnIds))
            {
                throw Contract("invalid caravan assembly crew");
            }
            output.Add(new CaravanAssemblyFact(row.Id, row.MapId, pawnIds));
        }
        return output;
    }

    /// <summary>Go's <c>validatedWorldSites</c>.</summary>
    private static List<WorldSite> ValidatedWorldSites(IEnumerable<WorldSite> rows)
    {
        var output = new List<WorldSite>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row is null
                || !IsValidId(row.Id)
                || row.DefName.Length == 0
                || !seen.Add(row.Id)
                || !row.HasState
                || row.State < WorldSiteState.Unknown
                || row.State > WorldSiteState.Destroyed)
            {
                throw Contract("invalid world site identity or state");
            }
            if (row.Tile < 0 || row.LayerId < 0 || row.MapId < 0 || (row.State == WorldSiteState.MapLoaded && !row.HasMapId))
            {
                throw Contract("invalid world site location");
            }
            if (!ValidFiniteNonnegative(row.HasDistanceTiles, row.DistanceTiles) || !ValidFiniteNonnegative(row.HasThreatPoints, row.ThreatPoints) || row.TravelTicks < 0)
            {
                throw Contract("invalid world site estimate");
            }
            if (!ValidQuestIdentities(row.QuestIds) || !ValidQuestIdentities(row.RoutePawnIds))
            {
                throw Contract("invalid world site quest or route crew");
            }
            if (row.HasTravelTicks && (!row.HasReachable || !row.Reachable || row.RoutePawnIds.Count == 0))
            {
                throw Contract("world site estimate lacks reachable crew");
            }
            ValidSiteExtensions(row);
            output.Add(row.Clone());
        }
        return output;
    }

    /// <summary>Go's <c>validatedQuestSurvey</c>.</summary>
    private static QuestSurveyScanner? ValidatedQuestSurvey(QuestSurveyScanner? row)
    {
        if (row is null)
        {
            return null;
        }
        if (!IsValidId(row.SiteId) || (row.HasScannerId && !IsValidId(row.ScannerId)) || row.DurationTicks < 0 || row.EndTick < 0 || row.RaidTick < 0)
        {
            throw Contract("invalid quest survey scanner");
        }
        if (row.Alive && !row.HasScannerId)
        {
            throw Contract("live quest scanner lacks identity");
        }
        return row.Clone();
    }

    /// <summary>Go's <c>validSiteExtensions</c>.</summary>
    private static void ValidSiteExtensions(WorldSite row)
    {
        var miningIds = new List<string>();
        foreach (var m in row.MiningTargets)
        {
            if (m is null || m.Def.Length == 0 || !m.HasMapId || m.MapId < 0 || !MonumentCellKnown(m.Cell) || m.Cell.X < 0 || m.Cell.Z < 0)
            {
                throw Contract("invalid site mining target");
            }
            miningIds.Add(m.Id);
        }
        if (!ValidQuestIdentities(miningIds))
        {
            throw Contract("invalid site mining identity");
        }
        if (row.Security is { } s
            && (!ValidFiniteNonnegative(s.HasInitialPoints, s.InitialPoints)
                || !ValidFiniteNonnegative(s.HasPendingRaidPoints, s.PendingRaidPoints)
                || s.TrapCount < 0
                || s.DetectionTicksLeft < 0
                || s.RaidsSent < 0))
        {
            throw Contract("invalid site security");
        }
        if (row.PeaceTalks is { } p
            && (!IsValidId(p.FactionId) || p.CurrentGoodwill < -100 || p.CurrentGoodwill > 100 || p.WorstGoodwillLoss < 0 || p.BestGoodwillGain < 0))
        {
            throw Contract("invalid site diplomacy");
        }
        if (row.Extraction is not { } e)
        {
            return;
        }
        if (row.State != WorldSiteState.MapLoaded || !ValidQuestIdentities(e.CrewIds))
        {
            throw Contract("invalid site extraction crew");
        }
        if (!ValidFiniteNonnegative(e.HasCarryCapacity, e.CarryCapacity)
            || !ValidFiniteNonnegative(e.HasCarriedMass, e.CarriedMass)
            || !ValidFiniteNonnegative(e.HasInventoryFoodDays, e.InventoryFoodDays)
            || !ValidFiniteNonnegative(e.HasCrewNutritionPerDay, e.CrewNutritionPerDay))
        {
            throw Contract("invalid site extraction estimate");
        }
        var ids = new List<string>();
        foreach (var cargo in e.Cargo)
        {
            if (cargo is null || cargo.Def.Length == 0 || !cargo.HasCount || cargo.Count <= 0)
            {
                throw Contract("invalid site extraction cargo");
            }
            ids.Add(cargo.Id);
            if (!ValidFiniteNonnegative(cargo.HasUnitMass, cargo.UnitMass)
                || !ValidFiniteNonnegative(cargo.HasMarketValue, cargo.MarketValue)
                || !ValidFiniteNonnegative(cargo.HasNutrition, cargo.Nutrition))
            {
                throw Contract("invalid site cargo estimate");
            }
        }
        if (!ValidQuestIdentities(ids))
        {
            throw Contract("invalid site cargo identity");
        }
        var maps = new HashSet<int>();
        var cells = new HashSet<(int X, int Z)>();
        foreach (var cell in e.ExitCells)
        {
            if (!MonumentCellKnown(cell) || cell.X < 0 || cell.Z < 0 || !cells.Add((cell.X, cell.Z)))
            {
                throw Contract("invalid site exit cell");
            }
        }
        foreach (var route in e.HomeRoutes)
        {
            if (route is null
                || !route.HasMapId
                || !route.HasTile
                || route.MapId < 0
                || route.Tile < 0
                || !maps.Add(route.MapId)
                || route.TravelTicks < 0
                || (route.HasTravelTicks && !route.Reachable))
            {
                throw Contract("invalid site home route");
            }
        }
    }

    /// <summary>Go's <c>validatedQuestGravEngine</c>.</summary>
    private static QuestGravEngine? ValidatedQuestGravEngine(QuestGravEngine? row)
    {
        if (row is null)
        {
            return null;
        }
        if (!IsValidId(row.EngineId) || row.MapId < 0)
        {
            throw Contract("invalid quest grav engine");
        }
        if (row.Spawned && (!row.HasMapId || !MonumentCellKnown(row.Cell) || row.Cell.X < 0 || row.Cell.Z < 0))
        {
            throw Contract("spawned quest grav engine lacks location");
        }
        if (!ValidQuestIdentities(row.EligiblePawnIds) || !ValidQuestIdentities(row.InspectingPawnIds))
        {
            throw Contract("invalid quest grav engine worker");
        }
        return row.Clone();
    }

    /// <summary>Go's <c>validatedQuestMonument</c>: a partial marker identity cannot become an executable construction target, so it is dropped.</summary>
    private static QuestMonument? ValidatedQuestMonument(QuestMonument? m)
    {
        if (m is null
            || m.MarkerId.Length == 0
            || m.DefName.Length == 0
            || !m.HasMapId
            || m.MapId < 0
            || !m.HasPacked
            || !m.HasInstalled
            || (m.Packed == m.Installed && !(m.Offered && !m.Packed)))
        {
            return null;
        }
        if (m.Installed && !MonumentCellKnown(m.Cell))
        {
            return null;
        }
        foreach (var p in m.Pieces)
        {
            if (p is null || p.DefName.Length == 0 || !MonumentCellKnown(p.Offset) || !p.HasRotation || p.Rotation < 0 || p.Rotation > 3 || p.Footprint.Count == 0)
            {
                return null;
            }
            if (p.Footprint.Any(cell => !MonumentCellKnown(cell)))
            {
                return null;
            }
        }
        foreach (var r in m.Resources)
        {
            if (r is null || r.Id.Length == 0 || r.DefName.Length == 0 || !MonumentCellKnown(r.Cell))
            {
                return null;
            }
        }
        foreach (var rows in new[] { m.SuppliedResources, m.AvailableResources })
        {
            foreach (var q in rows)
            {
                if (q is null || q.DefName.Length == 0 || !q.HasUnits || q.Units < 0)
                {
                    return null;
                }
            }
        }
        foreach (var cell in m.InstallCells)
        {
            if (!MonumentCellKnown(cell) || cell.X < 0 || cell.Z < 0)
            {
                return null;
            }
        }
        return m.Clone();
    }
}
