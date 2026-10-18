using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>tools/foodchannels</c>: the core-only food source census on the lab. Staged berry bushes are forage, a core game
/// has no Odyssey fishable water, and a staged player cow reports its native milk fullness (#421).
/// </summary>
internal static class ToolsFoodChannels
{
    private const string Cow = "cow";

    internal static TypedCase Case() => new()
    {
        Name = "tools/foodchannels",
        Scope = "colony facts foodChannels on the core lab: staged Plant_Berry bushes list as forage, fishableWater is absent "
            + "without Odyssey, and a staged player Cow is a gatherable Milk row with its CompMilkable fullness in [0,1].",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 5,
                Height = 3,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 4,
                Radius = 30,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged],
            },
            Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 5, 3), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones] }],
            Plants = [new Fx.StagePlant { Name = "berries", Def = "Plant_Berry", Cells = [new(0, 0), new(1, 0), new(2, 0)] }],
            Pawns = [new Fx.StagePawn { Name = Cow, Kind = "Cow", Faction = Fx.StageFaction.Player, Cell = new(2, 2) }],
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var cow = s.Staged!.Pawns[Cow].Id;
        var reply = await s.CallAsync("food-channels", NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        var facts = TypedReads.Observed("food-channels", NativeOps.ReadColonyFacts, reply, reply.Outcome?.Observed);
        var channels = facts.FoodChannels?.Observed ?? throw new AcceptanceException($"food-channels: no observed food channels: {facts.FoodChannels}");
        s.RecordWire("food_channels", channels);
        if (channels.FishableWater is not null || !channels.Forage.Any(row => row.DefName == "Plant_Berry"))
        {
            throw new AcceptanceException($"food-channels: want Plant_Berry forage and no Odyssey fishable water, got forage [{string.Join(", ", channels.Forage.Select(row => row.DefName))}] water {channels.FishableWater}");
        }

        if (!channels.Gatherable.Any(row => row.PawnId == cow && row.Race == "Cow" && row.Resource == "Milk" && row.Fullness is >= 0 and <= 1))
        {
            throw new AcceptanceException($"food-channels: want a Milk row with fullness in [0,1] for {cow}, got {string.Join("; ", channels.Gatherable)}");
        }
    }
}
