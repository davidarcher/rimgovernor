using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
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
    internal static Func<bool, string, bool> PartySpotSpent(IReadOnlyList<Method> methods)
    {
        var used = methods.Select(m => m.MethodId).ToHashSet();
        return (retire, id) => retire ? used.Contains(RetireMethodId(PartySpotRetirePrefix, id)) : used.Contains(PartySpotPlaceMethod(id));
    }

    internal static Fact<PartySpotNeed> PartySpotReview(ColonyProjection facts, Func<bool, string, bool>? spent)
    {
        var qualities = facts.Facts.Sleeping.TryGetValue(out var sleeping) ? sleeping.Rooms : Unknown<EquatableArray<UpkeepRoom>>();
        return PolicyFunctions.ReviewPartySpot(new PartySpotInput
        {
            Rooms = facts.Rooms,
            Qualities = qualities,
            Levels = facts.Impressiveness,
            Census = facts.Facts.CurrentConstruction,
            Cells = facts.Cells,
            Placeable = ComfortBuilderAvailable(facts, PolicyConstants.PartySpotDefinition),
            Spent = spent,
        });
    }
}

public sealed partial class Rounder
{
    /// <summary>Whether a party spot must be placed or retired, read against the previous review's comfort methods.</summary>
    internal Fact<bool> PartySpotOwed(Rounds previous, ColonyProjection facts, CancellationToken ct)
    {
        Func<bool, string, bool>? spent = null;
        foreach (var binding in previous.Standards)
        {
            if (binding.Concern != PolicyFunctions.EnsureComfort)
            {
                continue;
            }

            var state = Player.Journal.LoadStandard(binding.Standard, ct);
            if (state.Standard.Finding == Finding.Unmet)
            {
                spent = BuildingRuntimePackage.PartySpotSpent(state.Methods);
            }
        }

        if (!BuildingRuntimePackage.PartySpotReview(facts, spent).TryGetValue(out var need))
        {
            return Unknown<bool>();
        }

        return Known(need.Owed);
    }
}

public sealed partial class RoundsBuildingPlanner
{
    /// <summary>Retires a stale party spot, or resolves the planner to place a new one; the planner is null when the step is already decided.</summary>
    internal async Task<(RoundsBuildingResult Result, RoundsBuildingPlanner? Placing)> PartySpotStepAsync(ControlState state, Rounds review, StandardState goal, ColonyReading reading, CancellationToken call, CancellationToken epoch)
    {
        if (!BuildingRuntimePackage.PartySpotReview(reading.Projection, BuildingRuntimePackage.PartySpotSpent(goal.Methods)).TryGetValue(out var need) || !need.Owed)
        {
            return (new RoundsBuildingResult { Verdict = BuildingReasonNoDeficit }, null);
        }

        if (need.Retire is { } b)
        {
            var retiring = b.Building ?? throw new InvalidOperationException("party spot retire: the retired building carries no building");
            var result = await RetireBuildingAsync(state, review, goal, reading, b.Id, retiring.Definition, b.Cells[0], BuildingRuntimePackage.PartySpotRetirePrefix, "party_spot_retire_method", call, epoch).ConfigureAwait(false);
            return (result, null);
        }

        var placing = Copy();
        placing.Cells = need.Cells;
        placing.PartySpotRoom = need.Room;
        return (new RoundsBuildingResult(), placing);
    }
}
