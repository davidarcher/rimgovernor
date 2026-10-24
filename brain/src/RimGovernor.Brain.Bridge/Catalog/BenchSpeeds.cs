using RimWorld;
using Verse;
using Domain = RimGovernor.Brain.Domain;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// The research speed a bench gets from the room it stands in (#3800), from the ResearchSpeedFactor stat's parts.

public sealed partial class DefinitionCatalog
{
    /// <summary>
    /// Per research bench def (a Building_ResearchBench), its StatDefOf.ResearchSpeedFactor as the def data states it: the
    /// def's statBases value (else the stat's defaultBaseValue), the stat's minValue and its parts in order. Empty without the stat.
    /// </summary>
    public IReadOnlyDictionary<string, P.BenchSpeedModel> ResearchBenchSpeeds()
    {
        var speeds = new Dictionary<string, P.BenchSpeedModel>(StringComparer.Ordinal);
        if (DefRow<StatDef>(nameof(StatDefOf.ResearchSpeedFactor)) is not { } stat)
        {
            return speeds;
        }

        foreach (var (name, row) in _thingDefs.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (IsClass(row, typeof(Building_ResearchBench)))
            {
                speeds[name] = new(StatBaseEntry(row, stat.defName) ?? stat.defaultBaseValue, stat.minValue,
                    Domain.EquatableArray.From(stat.parts.Seq().Select(part => BenchPart(row, part))));
            }
        }

        return speeds;
    }

    // One stat part of a bench def: the parts that read the bench's room, the ones held at 1, and any other class unread.
    private static P.BenchStatPart BenchPart(ThingDef row, StatPart part)
    {
        switch (part)
        {
            case StatPart_Outdoors outdoors:
                return new P.BenchOutdoorsPart(GameMembers.FactorIndoors(outdoors), GameMembers.FactorOutdoors(outdoors));
            case StatPart_WorkTableRoomRole:
                // StatPart_WorkTableRoomRole applies nothing to a def without a workTableRoomRole.
                if (row.building?.workTableRoomRole is not { } role)
                {
                    return new P.BenchHeldPart();
                }

                return Wire.TryParse<P.RoomRole>(role.defName, out var roomRole)
                    ? new P.BenchRoomRolePart(roomRole, row.building.workTableNotInRoomRoleFactor)
                    : new P.BenchUnreadPart(nameof(StatPart_WorkTableRoomRole) + ":" + role.defName);
            case StatPart_WorkTableTemperature:
                return new P.BenchHeldPart();
            case StatPart_RoomStat roomStat:
                var stat = GameMembers.RoomStat(roomStat);
                if (stat.workerClass == typeof(RoomStatWorker_FromStatByCurve) && stat.inputStat?.defName == nameof(RoomStatDefOf.Cleanliness)
                    && stat.curve is { } curve && curve.Points.Count > 0)
                {
                    return new P.BenchCleanlinessPart(Domain.EquatableArray.From(curve.Points.Select(point => new P.CurvePoint(point.x, point.y))), stat.roomlessScore);
                }

                // RoomStatWorker_ReadingBonus scores 1 in a room without a bookcase, which a planned room never holds.
                return stat.workerClass == typeof(RoomStatWorker_ReadingBonus) && stat.roomlessScore == 1
                    ? new P.BenchHeldPart()
                    : new P.BenchUnreadPart(nameof(StatPart_RoomStat) + ":" + stat.defName);
            default:
                return new P.BenchUnreadPart(part.GetType().Name);
        }
    }
}
