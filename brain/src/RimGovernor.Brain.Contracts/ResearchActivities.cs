using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
namespace RimGovernor.Contracts.Wire.Observations;

/// <summary>
/// Native mechanics for a block of research work: every free colonist at every bench the current project can be
/// researched at. It names no chosen pawn and no project; the research Concern owns which project is current.
/// </summary>
public sealed record ResearchActivityRead
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<ResearchWorker>))]
    public required EquatableArray<ResearchWorker> Workers { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Findings { get; init; }
}

/// <summary>
/// One pawn at one bench. PointsPerTick is what the game adds to the current project per work tick (the pawn's
/// ResearchSpeed, the bench's ResearchSpeedFactor, the difficulty's research speed and the project's cost factor).
/// </summary>
public sealed record ResearchWorker(string Pawn, string Bench, Cell Location, string Project,
    string WorkGiver, string Job, double PointsPerTick)
{
    public ProductiveSkillWork? SkillWork { get; init; }
}
