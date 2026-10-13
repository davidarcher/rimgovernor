using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;

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

    private static bool MonumentCellKnown([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] Cell? cell) => cell is not null && cell.X is not null && cell.Z is not null;

    private static bool FiniteBetween(double value, double low, double high) => double.IsFinite(value) && value >= low && value <= high;

    private static WorldProgressionRead WorldProgressionSelected(WorldProgressionSnapshot? v, Identity? identity)
    {
        if (v is null)
        {
            throw Contract("world progression snapshot missing");
        }
        ValidateContext(v.Context);
        if (!SameIdentity(v.Context?.Identity, identity))
        {
            throw Contract("world progression world mismatch");
        }
        var maps = new List<WorldMap>(v.Maps.Count);
        var seenMapPawns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.Maps)
        {
            if (row is null || row.Id is null || row.Tile is null || row.Tile.GetValueOrDefault() < 0 || row.Home is null)
            {
                throw Contract("invalid world progression map");
            }
            var pawnIds = new List<string>(row.Pawns.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pawn in row.Pawns)
            {
                if (pawn is null || pawn.Pawn is null || !IsValidId((pawn.Pawn.Id ?? "")) || !seen.Add((pawn.Pawn.Id ?? "")))
                {
                    throw Contract("invalid or duplicate world progression map pawn");
                }
                // A pawn spawned on two maps at once is not a real game state; treat it as evidence the census
                // cannot be trusted rather than picking one map to believe.
                if (!seenMapPawns.Add((pawn.Pawn.Id ?? "")))
                {
                    throw Contract("world progression pawn present on multiple maps");
                }
                pawnIds.Add((pawn.Pawn.Id ?? ""));
            }
            maps.Add(new WorldMap { Id = row.Id.GetValueOrDefault(), Tile = row.Tile.GetValueOrDefault(), Home = row.Home.GetValueOrDefault(), PawnIds = pawnIds, QuestWorkers = ValidatedQuestWorkers(row.QuestWorkers) });
        }
        var seenCaravans = new HashSet<string>(StringComparer.Ordinal);
        var caravans = new List<CaravanJourney>(v.Caravans.Count);
        foreach (var row in v.Caravans)
        {
            if (row is null || row.Caravan is null || !IsValidId((row.Caravan.Id ?? "")) || !seenCaravans.Add((row.Caravan.Id ?? "")))
            {
                throw Contract("invalid or duplicate world progression caravan");
            }
            if (row.Tile is null || row.Tile.GetValueOrDefault() < 0 || row.Destination.GetValueOrDefault() < 0)
            {
                throw Contract("invalid world progression caravan tile or crew");
            }
            var pawnIds = new List<string>(row.Pawns.Count);
            var pawns = new List<CaravanPawnFact>(row.Pawns.Count);
            var seenPawns = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pawn in row.Pawns)
            {
                if (pawn is null || pawn.Pawn is null || !IsValidId((pawn.Pawn.Id ?? "")) || !seenPawns.Add((pawn.Pawn.Id ?? "")))
                {
                    throw Contract("invalid or duplicate world progression caravan pawn");
                }
                pawnIds.Add((pawn.Pawn.Id ?? ""));
                pawns.Add(new CaravanPawnFact((pawn.Pawn.Id ?? ""), pawn.Dead is not null && pawn.Dead.GetValueOrDefault(), pawn.Dead is not null, pawn.Downed is not null && pawn.Downed.GetValueOrDefault(), pawn.Downed is not null));
            }
            var inventory = new Dictionary<string, long>(row.Inventory.Count, StringComparer.Ordinal);
            var seenDefs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in row.Inventory)
            {
                if (item is null || item.DefName is null || !seenDefs.Add((item.DefName ?? "")) || item.Units is null || item.Units.GetValueOrDefault() < 0)
                {
                    throw Contract("invalid or duplicate world progression caravan inventory row");
                }
                inventory[(item.DefName ?? "")] = item.Units.GetValueOrDefault();
            }
            var routes = new List<WorldRouteFact>(row.HomeRoutes.Count);
            foreach (var route in row.HomeRoutes)
            {
                if (route is null || route.Destination is null || route.Destination.GetValueOrDefault() < 0 || route.Reachable is null)
                {
                    throw Contract("invalid world progression caravan home route");
                }
                var estimated = 0L;
                if (route.EstimatedTicks is not null)
                {
                    if (route.EstimatedTicks.GetValueOrDefault() < 0)
                    {
                        throw Contract("invalid world progression caravan home route ticks");
                    }
                    estimated = route.EstimatedTicks.GetValueOrDefault();
                }
                routes.Add(new WorldRouteFact("", route.Destination.GetValueOrDefault(), route.Reachable.GetValueOrDefault(), estimated, route.EstimatedTicks is not null));
            }
            if (!CombatNumber(OptionalDouble(row.FoodDays is not null, row.FoodDays.GetValueOrDefault()), true))
            {
                throw Contract("invalid world progression caravan food days");
            }
            var settlementRoutes = new List<WorldRouteFact>();
            var seenSettlements = new HashSet<string>(StringComparer.Ordinal);
            foreach (var route in row.SettlementRoutes)
            {
                if (route is null
                    || !IsValidId((route.SettlementId ?? ""))
                    || !seenSettlements.Add((route.SettlementId ?? ""))
                    || route.Destination is null
                    || route.Destination.GetValueOrDefault() < 0
                    || route.Reachable is null
                    || (route.EstimatedTicks is not null && route.EstimatedTicks.GetValueOrDefault() < 0))
                {
                    throw Contract("invalid caravan settlement route");
                }
                settlementRoutes.Add(new WorldRouteFact((route.SettlementId ?? ""), route.Destination.GetValueOrDefault(), route.Reachable.GetValueOrDefault(), route.EstimatedTicks.GetValueOrDefault(), route.EstimatedTicks is not null));
            }
            if (!CombatNumber(OptionalDouble(row.MassUsage is not null, row.MassUsage.GetValueOrDefault()), true)
                || !CombatNumber(OptionalDouble(row.MassCapacity is not null, row.MassCapacity.GetValueOrDefault()), true)
                || !CombatNumber(OptionalDouble(row.FoodRotDays is not null, row.FoodRotDays.GetValueOrDefault()), true))
            {
                throw Contract("invalid caravan pack facts");
            }
            caravans.Add(new CaravanJourney
            {
                Id = (row.Caravan.Id ?? ""),
                Tile = row.Tile.GetValueOrDefault(),
                Moving = row.Moving.GetValueOrDefault(),
                PawnIds = pawnIds,
                Pawns = pawns,
                HomeRoutes = routes,
                Inventory = inventory,
                SettlementRoutes = settlementRoutes,
                MassUsage = OptionalFact(row.MassUsage is not null, row.MassUsage.GetValueOrDefault()),
                MassCapacity = OptionalFact(row.MassCapacity is not null, row.MassCapacity.GetValueOrDefault()),
                FoodRotDays = OptionalFact(row.FoodRotDays is not null, row.FoodRotDays.GetValueOrDefault()),
                Destination = row.Destination is not null ? row.Destination.GetValueOrDefault() : null,
                FoodDays = row.FoodDays.GetValueOrDefault(),
                FoodDaysKnown = row.FoodDays is not null,
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
            if (row is null || !IsValidId((row.Id ?? "")))
            {
                throw Contract("invalid world progression faction");
            }
            factions.Add(new FactionFact((row.Id ?? ""), row.Player.GetValueOrDefault(), row.Hostile.GetValueOrDefault()));
        }
        var sites = ValidatedWorldSites(v.Sites);
        var assemblies = ValidatedCaravanAssemblies(v.Assemblies);
        return new WorldProgressionRead { Context = v.Context, Maps = maps, Caravans = caravans, Quests = quests, Factions = factions, Sites = sites, Assemblies = assemblies };
    }

    private static QuestOffer DecodeQuest(QuestState? row, HashSet<string> seenQuests)
    {
        if (row is null || !IsValidId((row.Id ?? "")) || !seenQuests.Add((row.Id ?? "")) || QuestStatusName(row.State.GetValueOrDefault()) == "" || row.RequiresAccepter is null || row.CanAccept is null)
        {
            throw Contract("invalid or duplicate world progression quest");
        }
        if (row.Snapshot is null || (row.Snapshot.EntityId ?? "") != (row.Id ?? "") || !IsValidId((row.Snapshot.Token ?? "")))
        {
            throw Contract("world progression quest CAS token unavailable");
        }
        var choices = new HashSet<uint>();
        var favor = new Dictionary<int, int>();
        foreach (var reward in row.Rewards)
        {
            if (reward is null || reward.Favor.GetValueOrDefault() < 0 || reward.Psylink.GetValueOrDefault() < 0 || reward.PermitPoints.GetValueOrDefault() < 0)
            {
                throw Contract("invalid world progression quest reward");
            }
            var choice = -1;
            if (reward.ChoiceIndex is not null)
            {
                if (reward.ChoiceIndex.GetValueOrDefault() > int.MaxValue)
                {
                    throw Contract("invalid quest choice index");
                }
                choice = (int)reward.ChoiceIndex.GetValueOrDefault();
                choices.Add(reward.ChoiceIndex.GetValueOrDefault());
            }
            if (reward.Favor.GetValueOrDefault() > 0)
            {
                favor.TryGetValue(choice, out var current);
                if ((long)current + reward.Favor.GetValueOrDefault() > int.MaxValue)
                {
                    throw Contract("quest favor overflow");
                }
                favor[choice] = current + reward.Favor.GetValueOrDefault();
            }
        }
        var eligible = new List<string>(row.EligiblePawns.Count);
        var seenPawns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pawn in row.EligiblePawns)
        {
            if (pawn is null || !IsValidId((pawn.Id ?? "")) || !seenPawns.Add((pawn.Id ?? "")))
            {
                throw Contract("invalid or duplicate world progression quest accepter");
            }
            eligible.Add((pawn.Id ?? ""));
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
        if (row.AskerPawnId is not null && !IsValidId((row.AskerPawnId ?? "")))
        {
            throw Contract("invalid quest asker");
        }
        if (row.ChoicePartCount.GetValueOrDefault() < 0)
        {
            throw Contract("invalid quest choice parts");
        }
        var rewards = new List<QuestRewardFact>(row.Rewards.Count);
        foreach (var reward in row.Rewards)
        {
            var items = new List<QuestTradeItemFact>(reward.Items.Count);
            foreach (var item in reward.Items)
            {
                if (item is null || !IsValidId((item.DefName ?? "")) || item.Units is null || item.Units.GetValueOrDefault() < 0)
                {
                    throw Contract("invalid quest reward item");
                }
                items.Add(new QuestTradeItemFact((item.DefName ?? ""), item.Units.GetValueOrDefault()));
            }
            var seenPermits = new HashSet<string>(StringComparer.Ordinal);
            foreach (var permit in reward.Permits)
            {
                if (!IsValidId(permit) || !seenPermits.Add(permit))
                {
                    throw Contract("invalid quest reward permit");
                }
            }
            if (((reward.TitleDef ?? "").Length != 0 && !IsValidId((reward.TitleDef ?? ""))) || ((reward.FactionId ?? "").Length != 0 && !IsValidId((reward.FactionId ?? ""))))
            {
                throw Contract("invalid quest reward reference");
            }
            rewards.Add(new QuestRewardFact
            {
                Choice = reward.ChoiceIndex is not null ? (int)reward.ChoiceIndex.GetValueOrDefault() : -1,
                Goodwill = reward.Goodwill.GetValueOrDefault(),
                Psylink = reward.Psylink.GetValueOrDefault(),
                PermitPoints = reward.PermitPoints.GetValueOrDefault(),
                Permits = [.. reward.Permits],
                TitleDef = (reward.TitleDef ?? ""),
                FactionId = (reward.FactionId ?? ""),
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
            if (shuttle is null || !IsValidId((shuttle.ShuttleId ?? "")) || !seenShuttles.Add((shuttle.ShuttleId ?? "")) || shuttle.RequiredColonistCount.GetValueOrDefault() < 0)
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
                Id = (shuttle.ShuttleId ?? ""),
                AutoloadAvailable = shuttle.AutoloadAvailable is not null ? shuttle.AutoloadAvailable.GetValueOrDefault() : null,
                Autoload = shuttle.Autoload is not null ? shuttle.Autoload.GetValueOrDefault() : null,
                Loading = shuttle.Loading is not null ? shuttle.Loading.GetValueOrDefault() : null,
                AllRequiredLoaded = shuttle.AllRequiredLoaded is not null ? shuttle.AllRequiredLoaded.GetValueOrDefault() : null,
                ManualLaunchAvailable = shuttle.ManualLaunchAvailable is not null ? shuttle.ManualLaunchAvailable.GetValueOrDefault() : null,
                PawnIds = [.. shuttle.PawnIds],
                LoadedPawnIds = [.. shuttle.LoadedPawnIds],
                PendingPawnIds = [.. shuttle.PendingPawnIds],
                RequiredColonistCount = shuttle.RequiredColonistCount is not null ? shuttle.RequiredColonistCount.GetValueOrDefault() : null,
            });
        }
        var favors = favor.Select(pair => new QuestFavorFact(pair.Key, pair.Value)).OrderBy(fact => fact.Choice).ToList();
        var requests = new List<QuestTradeRequestFact>(row.TradeRequests.Count);
        foreach (var request in row.TradeRequests)
        {
            // A missing resource, count or destination on one row is not malformed evidence (native's own
            // QuestTradeRequest fields are all optional), only a negative count/destination is.
            if (request is null || request.Count.GetValueOrDefault() < 0 || request.Destination.GetValueOrDefault() < 0)
            {
                throw Contract("invalid world progression quest trade request");
            }
            requests.Add(new QuestTradeRequestFact((request.Resource ?? ""), request.Count.GetValueOrDefault(), request.Destination.GetValueOrDefault()));
        }
        return new QuestOffer
        {
            Id = row.Id ?? "",
            ScriptDef = row.ScriptDef ?? "",
            State = QuestStatusName(row.State.GetValueOrDefault()),
            RequiresAccepter = row.RequiresAccepter.GetValueOrDefault(),
            CanAccept = row.CanAccept.GetValueOrDefault(),
            ChoiceCount = choices.Count,
            EligiblePawnIds = eligible,
            SnapshotToken = (row.Snapshot.Token ?? ""),
            DeparturePawnIds = departure,
            FactionId = row.FactionId ?? "",
            MapKnown = row.MapId is not null,
            MapId = row.MapId.GetValueOrDefault(),
            ExpiresInTicks = row.ExpiresInTicks is not null ? row.ExpiresInTicks.GetValueOrDefault() : null,
            Asker = row.AskerPawnId ?? "",
            AskerFactionPlayer = row.AskerFactionPlayer is not null ? row.AskerFactionPlayer.GetValueOrDefault() : null,
            ViolentQuestsAllowed = row.ViolentQuestsAllowed is not null ? row.ViolentQuestsAllowed.GetValueOrDefault() : null,
            ThreatPoints = row.ThreatPoints is not null && ValidNonnegative(row.ThreatPoints.GetValueOrDefault()) ? row.ThreatPoints.GetValueOrDefault() : null,
            RewardChoiceParts = row.ChoicePartCount is not null ? row.ChoicePartCount.GetValueOrDefault() : null,
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
            || objective.Kind is null
            || objective.Kind.GetValueOrDefault() < QuestObjectiveKind.Unknown
            || objective.Kind.GetValueOrDefault() > QuestObjectiveKind.GiveItems
            || objective.Count.GetValueOrDefault() < 0
            || objective.Produced.GetValueOrDefault() < 0
            || objective.DeadlineTicks.GetValueOrDefault() < 0
            || objective.DurationTicks.GetValueOrDefault() < 0)
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
            if (w.Work is null || w.RateFactor is null || !ValidNonnegative(w.Work.GetValueOrDefault()) || !ValidNonnegative(w.RateFactor.GetValueOrDefault()))
            {
                throw Contract("invalid quest workload");
            }
            workload = w;
        }
        if (objective.MinimumMood is not null && !FiniteBetween(objective.MinimumMood.GetValueOrDefault(), 0, 1))
        {
            throw Contract("invalid quest mood threshold");
        }
        var moods = new List<QuestLodgerMoodFact>(objective.LodgerMoods.Count);
        var seenMoods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mood in objective.LodgerMoods)
        {
            if (mood is null || !IsValidId((mood.PawnId ?? "")) || !seenMoods.Add((mood.PawnId ?? "")) || (mood.Mood is not null && !FiniteBetween(mood.Mood.GetValueOrDefault(), 0, 1)))
            {
                throw Contract("invalid quest lodger mood");
            }
            moods.Add(new QuestLodgerMoodFact((mood.PawnId ?? ""), mood.Mood is not null ? mood.Mood.GetValueOrDefault() : null));
        }
        return new QuestObjectiveFact
        {
            Kind = objective.Kind.GetValueOrDefault(),
            Def = objective.Def ?? "",
            Stuff = objective.Stuff ?? "",
            Count = objective.Count is not null ? objective.Count.GetValueOrDefault() : null,
            Produced = objective.Produced is not null ? objective.Produced.GetValueOrDefault() : null,
            DeadlineTicks = objective.DeadlineTicks is not null ? objective.DeadlineTicks.GetValueOrDefault() : null,
            UnmetRequirement = objective.UnmetRequirement ?? "",
            PawnIds = [.. objective.PawnIds],
            Active = objective.Active is not null ? objective.Active.GetValueOrDefault() : null,
            MinimumMood = objective.MinimumMood is not null ? objective.MinimumMood.GetValueOrDefault() : null,
            Monument = ValidatedQuestMonument(objective.Monument),
            HackTargets = hackTargets,
            Gift = gift,
            HackRisk = risk,
            GravEngine = engine,
            DurationTicks = objective.DurationTicks is not null ? objective.DurationTicks.GetValueOrDefault() : null,
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
                || !IsValidId((target.Id ?? ""))
                || !seen.Add((target.Id ?? ""))
                || target.MapId.GetValueOrDefault() < 0
                || (target.Spawned.GetValueOrDefault() && target.MapId is null)
                || !ValidQuestIdentities(target.EligiblePawnIds)
                || target.Hackable is null)
            {
                throw Contract("invalid quest hack target");
            }
            var hackable = target.Hackable;
            if ((hackable.ProgressPercent is not null && !(double.IsFinite(hackable.ProgressPercent.GetValueOrDefault()) && hackable.ProgressPercent.GetValueOrDefault() >= 0))
                || (hackable.Defence is not null && !(double.IsFinite(hackable.Defence.GetValueOrDefault()) && hackable.Defence.GetValueOrDefault() >= 0)))
            {
                throw Contract("invalid quest hack state");
            }
            targets.Add(target);
        }
        QuestGiftRequest? giftFact = null;
        if (row.GiftRequest is { } gift)
        {
            if (!IsValidId((gift.RecipientId ?? ""))
                || (gift.Def ?? "").Length == 0
                || gift.Remaining.GetValueOrDefault() < 0
                || gift.MapId.GetValueOrDefault() < 0
                || !ValidQuestIdentities(gift.PawnIds)
                || !ValidQuestIdentities(gift.HaulingPawnIds)
                || !ValidQuestIdentities(gift.EligiblePawnIds)
                || (gift.EligiblePawnIds.Count > 0 && gift.MapId is null))
            {
                throw Contract("invalid quest gift request");
            }
            giftFact = gift;
        }
        QuestHackRisk? riskFact = null;
        if (row.HackRisk is { } risk)
        {
            if (!IsValidId((risk.FactionId ?? "")))
            {
                throw Contract("invalid quest hack faction");
            }
            riskFact = risk;
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
            if (row is null || !IsValidId((row.PawnId ?? "")) || !seen.Add((row.PawnId ?? "")))
            {
                throw Contract("invalid quest worker");
            }
            if (row.SocialLevel.GetValueOrDefault() < 0 || row.SocialLevel.GetValueOrDefault() > 20 || (row.NegotiationAbility is not null && !ValidNonnegative(row.NegotiationAbility.GetValueOrDefault())))
            {
                throw Contract("invalid quest diplomat");
            }
            if ((row.CarryCapacity is not null && !ValidNonnegative(row.CarryCapacity.GetValueOrDefault())) || (row.CarriedMass is not null && !ValidNonnegative(row.CarriedMass.GetValueOrDefault())))
            {
                throw Contract("invalid quest worker carry capacity");
            }
            var stats = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rate in row.Rates)
            {
                if (rate is null || (rate.Stat ?? "").Length == 0 || !stats.Add((rate.Stat ?? "")) || rate.Rate is null || !ValidNonnegative(rate.Rate.GetValueOrDefault()))
                {
                    throw Contract("invalid quest work rate");
                }
            }
            output.Add(row);
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
            if (row is null || !IsValidId((row.Id ?? "")) || !seen.Add((row.Id ?? "")) || row.MapId is null || row.MapId.GetValueOrDefault() < 0)
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
                pawnIds.Add((pawn.Id ?? ""));
            }
            if (!ValidQuestIdentities(pawnIds))
            {
                throw Contract("invalid caravan assembly crew");
            }
            output.Add(new CaravanAssemblyFact((row.Id ?? ""), row.MapId.GetValueOrDefault(), pawnIds));
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
                || !IsValidId((row.Id ?? ""))
                || (row.DefName ?? "").Length == 0
                || !seen.Add((row.Id ?? ""))
                || row.State is null
                || row.State.GetValueOrDefault() < WorldSiteState.Unknown
                || row.State.GetValueOrDefault() > WorldSiteState.Destroyed)
            {
                throw Contract("invalid world site identity or state");
            }
            if (row.Tile.GetValueOrDefault() < 0 || row.LayerId.GetValueOrDefault() < 0 || row.MapId.GetValueOrDefault() < 0 || (row.State.GetValueOrDefault() == WorldSiteState.MapLoaded && row.MapId is null))
            {
                throw Contract("invalid world site location");
            }
            if (!ValidFiniteNonnegative(row.DistanceTiles is not null, row.DistanceTiles.GetValueOrDefault()) || !ValidFiniteNonnegative(row.ThreatPoints is not null, row.ThreatPoints.GetValueOrDefault()) || row.TravelTicks.GetValueOrDefault() < 0)
            {
                throw Contract("invalid world site estimate");
            }
            if (!ValidQuestIdentities(row.QuestIds) || !ValidQuestIdentities(row.RoutePawnIds))
            {
                throw Contract("invalid world site quest or route crew");
            }
            if (row.TravelTicks is not null && (row.Reachable is null || !row.Reachable.GetValueOrDefault() || row.RoutePawnIds.Count == 0))
            {
                throw Contract("world site estimate lacks reachable crew");
            }
            ValidSiteExtensions(row);
            output.Add(row);
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
        if (!IsValidId((row.SiteId ?? "")) || (row.ScannerId is not null && !IsValidId((row.ScannerId ?? ""))) || row.DurationTicks.GetValueOrDefault() < 0 || row.EndTick.GetValueOrDefault() < 0 || row.RaidTick.GetValueOrDefault() < 0)
        {
            throw Contract("invalid quest survey scanner");
        }
        if (row.Alive.GetValueOrDefault() && row.ScannerId is null)
        {
            throw Contract("live quest scanner lacks identity");
        }
        return row;
    }

    /// <summary>Go's <c>validSiteExtensions</c>.</summary>
    private static void ValidSiteExtensions(WorldSite row)
    {
        var miningIds = new List<string>();
        foreach (var m in row.MiningTargets)
        {
            if (m is null || (m.Def ?? "").Length == 0 || m.MapId is null || m.MapId.GetValueOrDefault() < 0 || !MonumentCellKnown(m.Cell) || m.Cell.X.GetValueOrDefault() < 0 || m.Cell.Z.GetValueOrDefault() < 0)
            {
                throw Contract("invalid site mining target");
            }
            miningIds.Add((m.Id ?? ""));
        }
        if (!ValidQuestIdentities(miningIds))
        {
            throw Contract("invalid site mining identity");
        }
        if (row.Security is { } s
            && (!ValidFiniteNonnegative(s.InitialPoints is not null, s.InitialPoints.GetValueOrDefault())
                || !ValidFiniteNonnegative(s.PendingRaidPoints is not null, s.PendingRaidPoints.GetValueOrDefault())
                || s.TrapCount.GetValueOrDefault() < 0
                || s.DetectionTicksLeft.GetValueOrDefault() < 0
                || s.RaidsSent.GetValueOrDefault() < 0))
        {
            throw Contract("invalid site security");
        }
        if (row.PeaceTalks is { } p
            && (!IsValidId((p.FactionId ?? "")) || p.CurrentGoodwill.GetValueOrDefault() < -100 || p.CurrentGoodwill.GetValueOrDefault() > 100 || p.WorstGoodwillLoss.GetValueOrDefault() < 0 || p.BestGoodwillGain.GetValueOrDefault() < 0))
        {
            throw Contract("invalid site diplomacy");
        }
        if (row.Extraction is not { } e)
        {
            return;
        }
        if (row.State.GetValueOrDefault() != WorldSiteState.MapLoaded || !ValidQuestIdentities(e.CrewIds))
        {
            throw Contract("invalid site extraction crew");
        }
        if (!ValidFiniteNonnegative(e.CarryCapacity is not null, e.CarryCapacity.GetValueOrDefault())
            || !ValidFiniteNonnegative(e.CarriedMass is not null, e.CarriedMass.GetValueOrDefault())
            || !ValidFiniteNonnegative(e.InventoryFoodDays is not null, e.InventoryFoodDays.GetValueOrDefault())
            || !ValidFiniteNonnegative(e.CrewNutritionPerDay is not null, e.CrewNutritionPerDay.GetValueOrDefault()))
        {
            throw Contract("invalid site extraction estimate");
        }
        var ids = new List<string>();
        foreach (var cargo in e.Cargo)
        {
            if (cargo is null || (cargo.Def ?? "").Length == 0 || cargo.Count is null || cargo.Count.GetValueOrDefault() <= 0)
            {
                throw Contract("invalid site extraction cargo");
            }
            ids.Add((cargo.Id ?? ""));
            if (!ValidFiniteNonnegative(cargo.UnitMass is not null, cargo.UnitMass.GetValueOrDefault())
                || !ValidFiniteNonnegative(cargo.MarketValue is not null, cargo.MarketValue.GetValueOrDefault())
                || !ValidFiniteNonnegative(cargo.Nutrition is not null, cargo.Nutrition.GetValueOrDefault()))
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
            if (!MonumentCellKnown(cell) || cell.X.GetValueOrDefault() < 0 || cell.Z.GetValueOrDefault() < 0 || !cells.Add((cell.X.GetValueOrDefault(), cell.Z.GetValueOrDefault())))
            {
                throw Contract("invalid site exit cell");
            }
        }
        foreach (var route in e.HomeRoutes)
        {
            if (route is null
                || route.MapId is null
                || route.Tile is null
                || route.MapId.GetValueOrDefault() < 0
                || route.Tile.GetValueOrDefault() < 0
                || !maps.Add(route.MapId.GetValueOrDefault())
                || route.TravelTicks.GetValueOrDefault() < 0
                || (route.TravelTicks is not null && !route.Reachable.GetValueOrDefault()))
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
        if (!IsValidId((row.EngineId ?? "")) || row.MapId.GetValueOrDefault() < 0)
        {
            throw Contract("invalid quest grav engine");
        }
        if (row.Spawned.GetValueOrDefault() && (row.MapId is null || !MonumentCellKnown(row.Cell) || row.Cell.X.GetValueOrDefault() < 0 || row.Cell.Z.GetValueOrDefault() < 0))
        {
            throw Contract("spawned quest grav engine lacks location");
        }
        if (!ValidQuestIdentities(row.EligiblePawnIds) || !ValidQuestIdentities(row.InspectingPawnIds))
        {
            throw Contract("invalid quest grav engine worker");
        }
        return row;
    }

    /// <summary>Go's <c>validatedQuestMonument</c>: a partial marker identity cannot become an executable construction target, so it is dropped.</summary>
    private static QuestMonument? ValidatedQuestMonument(QuestMonument? m)
    {
        if (m is null
            || (m.MarkerId ?? "").Length == 0
            || (m.DefName ?? "").Length == 0
            || m.MapId is null
            || m.MapId.GetValueOrDefault() < 0
            || m.Packed is null
            || m.Installed is null
            || (m.Packed.GetValueOrDefault() == m.Installed.GetValueOrDefault() && !(m.Offered.GetValueOrDefault() && !m.Packed.GetValueOrDefault())))
        {
            return null;
        }
        if (m.Installed.GetValueOrDefault() && !MonumentCellKnown(m.Cell))
        {
            return null;
        }
        foreach (var p in m.Pieces)
        {
            if (p is null || (p.DefName ?? "").Length == 0 || !MonumentCellKnown(p.Offset) || p.Rotation is null || p.Rotation.GetValueOrDefault() < 0 || p.Rotation.GetValueOrDefault() > 3 || p.Footprint.Count == 0)
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
            if (r is null || (r.Id ?? "").Length == 0 || (r.DefName ?? "").Length == 0 || !MonumentCellKnown(r.Cell))
            {
                return null;
            }
        }
        foreach (var rows in new[] { m.SuppliedResources, m.AvailableResources })
        {
            foreach (var q in rows)
            {
                if (q is null || (q.DefName ?? "").Length == 0 || q.Units is null || q.Units.GetValueOrDefault() < 0)
                {
                    return null;
                }
            }
        }
        foreach (var cell in m.InstallCells)
        {
            if (!MonumentCellKnown(cell) || cell.X.GetValueOrDefault() < 0 || cell.Z.GetValueOrDefault() < 0)
            {
                return null;
            }
        }
        return m;
    }
}
