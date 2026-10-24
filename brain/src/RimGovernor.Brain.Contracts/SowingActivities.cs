using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Observations;

/// <summary>
/// The cells native sowing would accept now, per free colonist able to sow and growing zone or planter that has a crop set. It
/// carries no chosen pawn, count or value; policy proposes the sow tasks and native re-checks each cell when it offers the step.
/// <see cref="Findings"/> names each grower without a crop.
/// </summary>
public sealed record SowingActivityRead
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<SowingWorker>))]
    public required EquatableArray<SowingWorker> Workers { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Findings { get; init; }
}

/// <summary>
/// One pawn's sowing of one zone: <paramref name="Zone"/> is the zone's (or planter's) unique load id, <paramref name="Cells"/> the cells
/// that pawn can sow now, nearest to it first, <paramref name="ApproachTicks"/> its walk to the first. <paramref name="TicksPerCell"/> is
/// the crop's sow work over the pawn's plant work speed; a pawn whose speed is 0 cannot sow and has no row.
/// </summary>
public sealed record SowingWorker(string Pawn, string Zone, string WorkGiver, double ApproachTicks,
    double TicksPerCell,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))] EquatableArray<Cell> Cells)
{
    public required ProductiveSkillWork SkillWork { get; init; }
}
