using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go world_progression_snapshot.go, world_progression.go (worldProgressionSelected), world_sites.go, quest_workers.go,
// quest_monument.go and quest_hack_gift.go: the projection of a frame's world progression census.
public static partial class BridgeFunctions
{
    private static bool MonumentCellKnown([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] Cell? cell) => cell is not null && cell.X is not null && cell.Z is not null;

    /// <summary>Go's <c>DecodeWorldProgressionSnapshot</c>: projects the world census a frame carries.</summary>
    public static WorldProgressionRead DecodeWorldProgressionSnapshot(WorldProgressionSnapshot v)
    {
        var maps = new List<WorldMap>(v.Maps.Count);
        foreach (var row in v.Maps)
        {
            maps.Add(new WorldMap
            {
                Id = row.Id.GetValueOrDefault(),
                Tile = row.Tile.GetValueOrDefault(),
                Home = row.Home.GetValueOrDefault(),
                PawnIds = [.. row.Pawns.Select(pawn => pawn.Pawn.Id ?? "")],
                QuestWorkers = [.. row.QuestWorkers],
            });
        }
        var caravans = new List<CaravanJourney>(v.Caravans.Count);
        foreach (var row in v.Caravans)
        {
            var inventory = new Dictionary<string, long>(row.Inventory.Count, StringComparer.Ordinal);
            foreach (var item in row.Inventory)
            {
                inventory[item.DefName ?? ""] = item.Units.GetValueOrDefault();
            }
            caravans.Add(new CaravanJourney
            {
                Id = row.Caravan?.Id ?? "",
                Tile = row.Tile.GetValueOrDefault(),
                Moving = row.Moving.GetValueOrDefault(),
                PawnIds = [.. row.Pawns.Select(pawn => pawn.Pawn.Id ?? "")],
                HomeRoutes = [.. row.HomeRoutes.Select(route => new WorldRouteFact("", route.Destination.GetValueOrDefault(), route.Reachable.GetValueOrDefault(), route.EstimatedTicks.GetValueOrDefault(), route.EstimatedTicks is not null))],
                Inventory = inventory,
                SettlementRoutes = [.. row.SettlementRoutes.Select(route => new WorldRouteFact(route.SettlementId ?? "", route.Destination.GetValueOrDefault(), route.Reachable.GetValueOrDefault(), route.EstimatedTicks.GetValueOrDefault(), route.EstimatedTicks is not null))],
                MassUsage = OptionalFact(row.MassUsage is not null, row.MassUsage.GetValueOrDefault()),
                MassCapacity = OptionalFact(row.MassCapacity is not null, row.MassCapacity.GetValueOrDefault()),
                FoodRotDays = OptionalFact(row.FoodRotDays is not null, row.FoodRotDays.GetValueOrDefault()),
                Destination = row.Destination,
                FoodDays = row.FoodDays.GetValueOrDefault(),
                FoodDaysKnown = row.FoodDays is not null,
            });
        }
        return new WorldProgressionRead
        {
            Context = v.Context,
            Maps = maps,
            Caravans = caravans,
            Quests = [.. v.Quests.Select(DecodeQuest)],
            Factions = [.. v.Factions.Select(row => new FactionFact(row.Id ?? "", row.Player.GetValueOrDefault(), row.Hostile.GetValueOrDefault()))],
            Sites = [.. v.Sites],
            Assemblies = [.. v.Assemblies.Select(row => new CaravanAssemblyFact(row.Id ?? "", row.MapId.GetValueOrDefault(), [.. row.Pawns.Select(pawn => pawn.Id ?? "")]))],
        };
    }

    private static QuestOffer DecodeQuest(QuestState row)
    {
        var choices = new HashSet<uint>();
        var favor = new Dictionary<int, int>();
        foreach (var reward in row.Rewards)
        {
            var choice = -1;
            if (reward.ChoiceIndex is { } index)
            {
                choice = (int)index;
                choices.Add(index);
            }
            if (reward.Favor.GetValueOrDefault() > 0)
            {
                favor[choice] = favor.GetValueOrDefault(choice) + reward.Favor.GetValueOrDefault();
            }
        }
        var rewards = new List<QuestRewardFact>(row.Rewards.Count);
        foreach (var reward in row.Rewards)
        {
            rewards.Add(new QuestRewardFact
            {
                Choice = reward.ChoiceIndex is { } index ? (int)index : -1,
                Goodwill = reward.Goodwill.GetValueOrDefault(),
                Psylink = reward.Psylink.GetValueOrDefault(),
                PermitPoints = reward.PermitPoints.GetValueOrDefault(),
                Permits = [.. reward.Permits],
                TitleDef = reward.TitleDef ?? "",
                FactionId = reward.FactionId ?? "",
                Items = [.. reward.Items.Select(item => new QuestTradeItemFact(item.DefName ?? "", item.Units.GetValueOrDefault()))],
            });
        }
        var shuttles = new List<QuestShuttleFact>(row.Shuttles.Count);
        foreach (var shuttle in row.Shuttles)
        {
            shuttles.Add(new QuestShuttleFact
            {
                Id = shuttle.ShuttleId ?? "",
                AutoloadAvailable = shuttle.AutoloadAvailable,
                Autoload = shuttle.Autoload,
                Loading = shuttle.Loading,
                AllRequiredLoaded = shuttle.AllRequiredLoaded,
                ManualLaunchAvailable = shuttle.ManualLaunchAvailable,
                PawnIds = [.. shuttle.PawnIds],
                LoadedPawnIds = [.. shuttle.LoadedPawnIds],
                PendingPawnIds = [.. shuttle.PendingPawnIds],
                RequiredColonistCount = shuttle.RequiredColonistCount,
            });
        }
        return new QuestOffer
        {
            Id = row.Id ?? "",
            ScriptDef = row.ScriptDef ?? "",
            State = QuestStatusName(row.State.GetValueOrDefault()),
            RequiresAccepter = row.RequiresAccepter.GetValueOrDefault(),
            CanAccept = row.CanAccept.GetValueOrDefault(),
            ChoiceCount = choices.Count,
            EligiblePawnIds = [.. row.EligiblePawns.Select(pawn => pawn.Id ?? "")],
            SnapshotToken = row.Snapshot?.Token ?? "",
            DeparturePawnIds = [.. row.DeparturePawnIds],
            FactionId = row.FactionId ?? "",
            MapKnown = row.MapId is not null,
            MapId = row.MapId.GetValueOrDefault(),
            ExpiresInTicks = row.ExpiresInTicks,
            Asker = row.AskerPawnId ?? "",
            AskerFactionPlayer = row.AskerFactionPlayer,
            ViolentQuestsAllowed = row.ViolentQuestsAllowed,
            ThreatPoints = row.ThreatPoints,
            RewardChoiceParts = row.ChoicePartCount,
            Rewards = rewards,
            Objectives = [.. row.Objectives.Select(DecodeObjective)],
            Shuttles = shuttles,
            Favor = [.. favor.Select(pair => new QuestFavorFact(pair.Key, pair.Value)).OrderBy(fact => fact.Choice)],
            TradeRequests = [.. row.TradeRequests.Select(request => new QuestTradeRequestFact(request.Resource ?? "", request.Count.GetValueOrDefault(), request.Destination.GetValueOrDefault()))],
        };
    }

    private static QuestObjectiveFact DecodeObjective(QuestObjective objective) => new()
    {
        Kind = objective.Kind.GetValueOrDefault(),
        Def = objective.Def ?? "",
        Stuff = objective.Stuff ?? "",
        Count = objective.Count,
        Produced = objective.Produced,
        DeadlineTicks = objective.DeadlineTicks,
        UnmetRequirement = objective.UnmetRequirement ?? "",
        PawnIds = [.. objective.PawnIds],
        Active = objective.Active,
        MinimumMood = objective.MinimumMood,
        Monument = QuestMonumentTarget(objective.Monument),
        HackTargets = [.. objective.HackTargets],
        Gift = objective.GiftRequest,
        HackRisk = objective.HackRisk,
        GravEngine = objective.GravEngine,
        DurationTicks = objective.DurationTicks,
        SurveyScanner = objective.SurveyScanner,
        Workload = objective.Workload,
        LodgerMoods = [.. objective.LodgerMoods.Select(mood => new QuestLodgerMoodFact(mood.Mood))],
    };

    /// <summary>
    /// Go's <c>validatedQuestMonument</c>: a marker that is neither packed nor installed (and not an offered unpacked
    /// one), or an installed marker without a cell, is no executable construction target, so it is dropped.
    /// </summary>
    private static QuestMonument? QuestMonumentTarget(QuestMonument? m)
    {
        if (m is null
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
        return m;
    }
}
