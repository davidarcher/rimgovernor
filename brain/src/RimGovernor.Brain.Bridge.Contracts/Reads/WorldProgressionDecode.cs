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

    internal static QuestOffer DecodeQuest(QuestState row) => new()
    {
        Id = row.Id,
        ScriptDef = row.ScriptDef,
        State = WireEnums.QuestStatusName(row.State),
        RequiresAccepter = row.RequiresAccepter,
        CanAccept = row.CanAccept,
        EligiblePawnIds = [.. row.EligiblePawns.Select(pawn => pawn.Value)],
        DeparturePawnIds = [.. row.DeparturePawnIds.Select(pawn => pawn.Value)],
        FactionId = row.FactionId ?? "",
        Place = row.Place,
        Expiry = row.Expiry,
        Asker = row.Asker,
        ViolentQuestsAllowed = row.ViolentQuestsAllowed,
        Threat = row.Threat,
        RewardChoiceParts = row.ChoicePartCount,
        Rewards = [.. row.Rewards],
        Favor = row.Favor,
        Objectives = [.. row.Objectives.Select(QuestObjectiveFact.DecodeObjective)],
        Shuttles = [.. row.Shuttles],
    };
}

partial record QuestObjectiveFact
{

    internal static QuestObjectiveFact DecodeObjective(QuestObjective objective) => new()
    {
        Kind = objective.Kind,
        Goal = objective.Goal,
        Timing = objective.Timing,
        PawnIds = [.. objective.PawnIds.Select(pawn => pawn.Value)],
        Active = objective.Active,
        MinimumMood = objective.MinimumMood,
        Monument = objective.Monument,
        HackTargets = [.. objective.HackTargets],
        Gift = objective.GiftRequest,
        HackRisk = objective.HackRisk,
        GravEngine = objective.GravEngine,
        SurveyScanner = objective.SurveyScanner,
        Workload = objective.Workload,
        LodgerMoods = [.. objective.LodgerMoods],
    };
}
