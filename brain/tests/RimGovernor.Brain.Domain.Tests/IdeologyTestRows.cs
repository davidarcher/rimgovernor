using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests;

internal static class IdeologyTestRows
{
    internal static RimGovernor.Brain.GameFacts.Observations.IdeoBuilding Building => new()
    {
        Building = "Altar", Applies = false, Present = false, Satisfied = false,
        MinArea = 0, MinImpressiveness = 0, FloorTags = [], Things = [], ForbiddenBuildingTags = [], Unread = [],
    };
    internal static RimGovernor.Brain.GameFacts.Observations.IdeoPrecept Precept(string id, string def) => new() { Id = id, DefName = def };
    internal static RimGovernor.Brain.GameFacts.Observations.IdeoRole Role(string id, string def, bool active, IReadOnlyList<PawnId> pawns, bool converts = false, bool ready = false) =>
        new() { Id = id, DefName = def, Active = active, Pawns = [.. pawns], Converts = converts, ConvertReady = ready };
    internal static RimGovernor.Brain.GameFacts.Observations.IdeoRitual Ritual(string id, string pattern) => new()
    {
        Id = id, Pattern = pattern, LastFinishedTick = 0, ActiveObligations = 0, RepeatPenaltyActive = false, Running = false,
    };
}
