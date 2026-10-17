using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>sleeping/selected-bedroom</c> (#3278): with every colonist bedded and no other demand, the layout plan's first
/// bedroom is an unstarted planned room the coupled portfolio assesses. The room reconcile holds it until a selection
/// round picks its sites, then places exactly those, and the started room continues to completion as before.
/// </summary>
internal static class SleepingSelectedBedroom
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "sleeping/selected-bedroom",
        Scope = "Issue #3278: on the tribal " + SustainedFoodCase.BaselineSave + " colony at Masonry with a fixture hut bedding everyone, "
            + "the planned bedroom is placed only after a coupled selection round selects its sites, and the bedroom shell plan "
            + "then completes in the game. Unit tests cover the gate and the selected operations; they cannot prove the native "
            + "blueprints, construction and the selection round's timing in a running colony.",
        Start = new FixtureStart
        {
            Op = LayoutFixture.GridPrepare,
            ArgsFrom = StarterSite.DefaultArgsAsync,
            Args = new JsonObject { ["sleepingSpots"] = 8, ["stoneBlocks"] = 400, ["builders"] = true },
            On = new SaveStart { Name = SustainedFoodCase.BaselineSave },
        },
        Keep = ["Food"],
        Serve = new ServeSpec
        {
            Families = ["shelter", "expansion", "sleeping"],
            NativeTimeout = TimeSpan.FromSeconds(30),
            Prefix = "sleeping-selected-bedroom",
        },
        Budget = TimeSpan.FromMinutes(4),
        Crew = new Crew { Size = 3 },
        Reason = "one watch: the selection round, then the placed and built bedroom shell",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        s.Report["fixture"] = s.Prepared?.DeepClone();
        _ = await SustainedFood.ObserveAsync(
            ObservedSession.Of(s),
            new SustainedObservation
            {
                Watch = new WatchConfig
                {
                    Watch = TimeSpan.FromMinutes(3),
                    Extra = [ShelterFamily.Sleeping.Value],
                    Until = sample => LayoutFixture.Planned(sample[ShelterFamily.Sleeping.Value].AsMap(),
                        m => m.StartsWith(SleepingBedrooms.BedroomShellPrefix, StringComparison.Ordinal), complete: true),
                },
            },
            cancellationToken).ConfigureAwait(false);
    }
}
