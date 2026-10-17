using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>review/colony-week</c> (Go <c>colonyweek.go</c>): the colony review run. The governor plays a fresh pinned-seed map for
/// an in-game season while the game records hourly screenshots and colony facts for a person (or a model) to read
/// (<c>cmd/colonyreview</c> renders the report). It gates nothing.
/// </summary>
internal static class ReviewColonyWeek
{
    /// <summary>The native recorder op (scripts/fixtures/ColonyReviewFixture.cs).</summary>
    internal const string RecorderTool = "test/colony_review";

    /// <summary>Overrides the world seed; unset, the run plays <see cref="PinnedSeed"/>.</summary>
    internal const string SeedEnv = "RIMGOVERNOR_REVIEW_SEED";

    /// <summary>Overrides the in-game length in days (default <see cref="SeasonDays"/>).</summary>
    internal const string DaysEnv = "RIMGOVERNOR_REVIEW_DAYS";

    /// <summary>The nightly's fixed world seed: one run per seed, so a night's result compares with the last. It picks the biome.</summary>
    internal const string PinnedSeed = "review-pinned-1";

    /// <summary>One in-game quadrum, the default run length.</summary>
    internal const ulong SeasonDays = 15;

    // The wall-clock allowance per in-game day on a hosted runner, for the case's watch and budget.
    private const ulong MinutesPerDay = 12;

    // The starts a run picks from by seed: survivable without a specialised opening, different enough to show layout and
    // food problems.
    private static readonly string[] Biomes = ["TemperateForest", "AridShrubland", "BorealForest", "TropicalRainforest"];

    /// <summary><see cref="SeedEnv"/> or <see cref="PinnedSeed"/>.</summary>
    internal static string Seed() =>
        Environment.GetEnvironmentVariable(SeedEnv) is { Length: > 0 } seed ? seed : PinnedSeed;

    /// <summary><see cref="DaysEnv"/> or <see cref="SeasonDays"/>.</summary>
    internal static ulong Days() =>
        ulong.TryParse(Environment.GetEnvironmentVariable(DaysEnv), NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days > 0 ? days : SeasonDays;

    /// <summary>The run's start: Crashlanded's three colonists on a 250 map in a biome chosen by the seed.</summary>
    internal static Variant Variant(string seed)
    {
        // FNV-1a, 32 bits.
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(seed))
        {
            hash ^= b;
            hash *= 16777619u;
        }

        return new Variant
        {
            Save = "colony-review",
            Scenario = "Crashlanded",
            Count = 3,
            Seed = seed,
            Biome = Biomes[hash % (uint)Biomes.Length],
            Difficulty = "Medium",
            MapSize = 250,
            PlanetCoverage = 0.3,
        }.WithDefaults();
    }

    internal static AcceptanceCase Case()
    {
        var seed = Seed();
        var v = Variant(seed);
        var days = Days();
        return new AcceptanceCase
        {
            Name = "review/colony-week",
            Tier = CaseTier.Off,
            Scope = "Review run, not a gate: the governor plays a " + v.Scenario + " map (seed " + seed + ", " + v.Biome + ") for "
                + days.ToString(CultureInfo.InvariantCulture) + " in-game days under the storyteller while " + RecorderTool
                + " records an hourly colony screenshot into <output>/review beside the run timeline (colony census and standard states each in-game hour) for cmd/colonyreview. A "
                + "snapshot test cannot cover it: the point is what the colony looks like to a player after a season.",
            Start = new ScenarioStart { Spec = v.Start().Spec },
            Keep = [NeedDef.LiveNeeds.Value],
            Quiet = QuietMode.Loud,
            RequiredOps = [RecorderTool],

            // The recorder renders through a graphics device; the game is launched for this case alone.
            Graphics = true,
            NoKeep = true,
            Serve = new ServeSpec { NativeTimeout = TimeSpan.FromSeconds(15), StepStall = TimeSpan.FromSeconds(90), Prefix = "colony-review" },
            Reason = "a season of whole-colony play with the storyteller on is the thing under review",
            Budget = TimeSpan.FromMinutes((days * MinutesPerDay) + 10),
            Crew = new Crew { Size = 3 },
            Run = (s, ct) => RunAsync(s, seed, v, days, ct),
        };
    }

    private static async Task RunAsync(ISession s, string seed, Variant v, ulong days, CancellationToken cancellationToken)
    {
        var dir = Path.Combine(s.Config.Output, "review");
        s.Report["review_dir"] = dir;

        // What map this was, for cmd/colonyreview's header.
        s.Report["review"] = new JsonObject
        {
            ["seed"] = seed,
            ["scenario"] = v.Scenario,
            ["biome"] = v.Biome,
            ["colonists"] = v.Count,
            ["days"] = days,
        };
        _ = await SustainedFood.ObserveAsync(
            ObservedSession.Of(s),
            new SustainedObservation
            {
                Watch = new WatchConfig
                {
                    Watch = TimeSpan.FromMinutes(days * MinutesPerDay),
                    Window = days * 60000,
                    Poll = TimeSpan.FromSeconds(30),
                    PollTicks = 2500,
                    Concern = PolicyFunctions.FoodSupplyNeed.Value,
                    Extra = ColonyAudits.ColonyConcerns,

                    // A review records whatever happens; nothing ends it early but a stalled clock.
                    FailFast = new FailFast { Disabled = true },
                },
                Prepare = async (h, report, ct) =>
                {
                    var status = await h.CallAsync("review-start", RecorderTool, new JsonObject { ["action"] = "start", ["dir"] = dir }, ct).ConfigureAwait(false);
                    report["recorder"] = status.DeepClone();
                },
                Audit = async (h, report, ct) =>
                {
                    var status = await h.CallAsync("review-stop", RecorderTool, new JsonObject { ["action"] = "stop" }, ct).ConfigureAwait(false);
                    report["recorder"] = status.DeepClone();
                },
            },
            cancellationToken).ConfigureAwait(false);
    }
}
