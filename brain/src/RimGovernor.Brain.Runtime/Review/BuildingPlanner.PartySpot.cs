using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.Runtime;

partial class BuildingPlanner
{
    internal const string PartySpotRetirePrefix = "party-spot-retire";
    internal const string PartySpotPlacePrefix = "party-spot-place";

    internal static MethodId RetireMethodId(string prefix, string id)
    {
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        return new MethodId($"{prefix}-{Convert.ToHexStringLower(sum.AsSpan(0, 8))}");
    }

    internal static MethodId PartySpotPlaceMethod(string room) => RetireMethodId(PartySpotPlacePrefix, "room:" + room);

    /// <summary>Whether a party-spot retire or place method was already spent on the id.</summary>
    internal static Func<bool, string, bool> PartySpotSpent(IEnumerable<MethodId> methods)
    {
        var used = methods.ToHashSet();
        return (retire, id) => retire ? used.Contains(RetireMethodId(PartySpotRetirePrefix, id)) : used.Contains(PartySpotPlaceMethod(id));
    }

    internal static PartySpotNeed PartySpotReview(ColonyReading facts, Func<bool, string, bool>? spent)
    {
        var sleeping = facts.Facts.Sleeping;
        return PartySpotRules.ReviewPartySpot(new PartySpotInput
        {
            Rooms = facts.Facts.Upkeep.Rooms,
            Qualities = sleeping.Rooms,
            Levels = facts.Facts.Impressiveness,
            Census = facts.Facts.CurrentConstruction,
            Cells = facts.PlanningWindow.Cells,
            Placeable = ComfortBuilderAvailable(facts, PartySpotRules.PartySpotDefinition),
            Spent = spent,
        });
    }
}

public sealed partial class ColonyReview
{
    /// <summary>Whether a party spot must be placed or retired, read against the rooms/party-spot Need's unretired Methods.</summary>
    internal static bool PartySpotOwed(NeedOwner? owner, ColonyReading facts)
    {
        var spent = BuildingPlanner.PartySpotSpent(owner?.Methods.Select(m => m.Method) ?? []);
        return BuildingPlanner.PartySpotReview(facts, spent).Owed;
    }
}

public sealed partial class BuildingPlanner
{
    /// <summary>Retires a stale party spot, or resolves the planner to place a new one; the planner is null when the step is already decided.</summary>
    internal async Task<(BuildingResult Result, BuildingPlanner? Placing)> PartySpotStepAsync(ControlState state, ReviewState review, NeedOwner goal, ColonyReading reading, CancellationToken call, CancellationToken epoch)
    {
        var need = BuildingPlanner.PartySpotReview(reading, BuildingPlanner.PartySpotSpent(goal.OpenMethods.Select(m => m.Method)));
        if (!need.Owed)
        {
            return (new BuildingResult { Verdict = BuildingReasonNoDeficit }, null);
        }

        if (need.Retire is { } b)
        {
            var retiring = b.Building ?? throw new InvalidOperationException("party spot retire: the retired building carries no building");
            var result = await RetireBuildingAsync(state, review, goal, reading, b.Id, retiring.Definition, b.Cells[0], BuildingPlanner.PartySpotRetirePrefix, "party_spot_retire_method", call, epoch).ConfigureAwait(false);
            return (result, null);
        }

        var placing = Copy();
        placing.Cells = need.Cells;
        placing.PartySpotRoom = need.Room;
        return (new BuildingResult(), placing);
    }
}
