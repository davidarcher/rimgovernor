using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

partial record WorldProgressionRead
{

    /// <summary>Projects the world census a frame carries.</summary>
    public static WorldProgressionRead DecodeWorldProgressionSnapshot(WorldProgressionSnapshot v)
    {
        return new WorldProgressionRead
        {
            Context = v.Context,
            Maps = [.. v.Maps],
            Caravans = [.. v.Caravans],
            Quests = [.. v.Quests.Select(QuestOffer.DecodeQuest)],
            Factions = [.. v.Factions.Select(row => new FactionFact(row.Id, row.Player, row.Hostile))],
            Sites = [.. v.Sites],
            Assemblies = [.. v.Assemblies],
        };
    }
}

partial record QuestOffer
{

    internal static QuestOffer DecodeQuest(QuestState row)
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
                Items = [.. reward.Items.Select(item => new QuestTradeItemFact(item.DefName, item.Units))],
            });
        }
        return new QuestOffer
        {
            Id = row.Id ?? "",
            ScriptDef = row.ScriptDef ?? "",
            State = WireEnums.QuestStatusName(row.State.GetValueOrDefault()),
            RequiresAccepter = row.RequiresAccepter.GetValueOrDefault(),
            CanAccept = row.CanAccept.GetValueOrDefault(),
            ChoiceCount = choices.Count,
            EligiblePawnIds = [.. row.EligiblePawns.Select(pawn => pawn.Id ?? "")],
            DeparturePawnIds = [.. row.DeparturePawnIds],
            FactionId = row.FactionId ?? "",
            MapKnown = row.MapId is not null,
            MapId = row.MapId.GetValueOrDefault(),
            ExpiresInTicks = row.ExpiresInTicks,
            AskerFactionPlayer = row.AskerFactionPlayer,
            ViolentQuestsAllowed = row.ViolentQuestsAllowed,
            ThreatPoints = row.ThreatPoints,
            RewardChoiceParts = row.ChoicePartCount,
            Rewards = rewards,
            Objectives = [.. row.Objectives.Select(QuestObjectiveFact.DecodeObjective)],
            Shuttles = [.. row.Shuttles],
            Favor = [.. favor.Select(pair => new QuestFavorFact(pair.Key, pair.Value)).OrderBy(fact => fact.Choice)],
        };
    }
}

partial record QuestObjectiveFact
{

    internal static QuestObjectiveFact DecodeObjective(QuestObjective objective) => new()
    {
        Kind = objective.Kind,
        Def = objective.Def ?? "",
        Stuff = objective.Stuff ?? "",
        Count = objective.Count,
        Produced = objective.Produced,
        DeadlineTicks = objective.DeadlineTicks,
        UnmetRequirement = objective.UnmetRequirement ?? "",
        PawnIds = [.. objective.PawnIds],
        Active = objective.Active,
        MinimumMood = objective.MinimumMood,
        Monument = objective.Monument,
        HackTargets = [.. objective.HackTargets],
        Gift = objective.GiftRequest,
        HackRisk = objective.HackRisk,
        GravEngine = objective.GravEngine,
        DurationTicks = objective.DurationTicks,
        SurveyScanner = objective.SurveyScanner,
        Workload = objective.Workload,
        LodgerMoods = [.. objective.LodgerMoods.Select(mood => new QuestLodgerMoodFact(mood.Mood))],
    };
}
