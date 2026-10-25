using System.Globalization;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>defense/threat</c> (#395): the typed colony facts' threat section follows the game's own wealth count. On the paused
/// lab the section reports every figure and raid points on the game's 35-point floor (the quiet storyteller's
/// threatScale is 0); then wood and pemmican worth well over 1000 silver are staged, the clock runs past WealthWatcher's
/// recount interval, and the section reports the larger item and total wealth and raid points that did not fall.
/// </summary>
internal static class DefenseThreat
{
    /// <summary>RimWorld's WealthWatcher recounts lazily once this many ticks have passed since its last count.</summary>
    private const int RecountTicks = 5000;

    /// <summary>The game's raid-point floor.</summary>
    private const double RaidPointFloor = 35;

    /// <summary>What the staged supplies add to the item wealth at least: 1200 wood and 300 pemmican are worth well over it.</summary>
    private const double StockedWealth = 1000;

    private static readonly Fx.StageSpec Stock = new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = 6,
            Height = 6,
            OffsetX = -15,
            OffsetZ = 10,
            Radius = 10,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unfogged],
        },
        Things =
        [
            new Fx.StageThing { Name = "wood", Def = "WoodLog", Count = 1200, Cell = new(0, 0), Near = true, Forbidden = true },
            new Fx.StageThing { Name = "pemmican", Def = "Pemmican", Count = 300, Cell = new(5, 5), Near = true, Forbidden = true },
        ],
    };

    internal static TypedCase Case() => new()
    {
        Name = "defense/threat",
        Scope = "The typed colony read's threat section (#395) reads the native WealthWatcher and DefaultThreatPointsNow figures: on the "
            + "paused lab every figure is present and raid points sit on the 35-point floor, and after wood and pemmican worth over 1000 "
            + "silver are staged and the clock passes WealthWatcher's recount interval, item and total wealth grow by at least that "
            + "while raid points do not fall.",
        Ticks = RecountTicks + 500,
        Tier = CaseTier.OnDemand,
        Ops = [FixtureStage.Tool],
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        async Task<Observations.ThreatFacts> ReadAsync(string label)
        {
            var facts = await s.CallAsync(label, NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
            var threat = facts.Threat.Observed;
            s.RecordWire(label, threat);
            return threat;
        }

        var baseline = await ReadAsync("threat-baseline").ConfigureAwait(false);
        if (baseline.RaidPoints < RaidPointFloor)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"baseline raid points {baseline.RaidPoints} below the game's {RaidPointFloor}-point floor"));
        }

        _ = await s.StageAsync("stock", Stock, cancellationToken).ConfigureAwait(false);
        await s.RunTicksAsync("recount", RecountTicks + 1, cancellationToken).ConfigureAwait(false);
        var stocked = await ReadAsync("threat-stocked").ConfigureAwait(false);
        var problems = new List<string>();
        if (stocked.WealthItems < baseline.WealthItems + StockedWealth)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"item wealth {baseline.WealthItems} before, {stocked.WealthItems} after the stock"));
        }

        if (stocked.WealthTotal < baseline.WealthTotal + StockedWealth)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"total wealth {baseline.WealthTotal} before, {stocked.WealthTotal} after the stock"));
        }

        if (stocked.RaidPoints < baseline.RaidPoints)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"raid points fell as wealth grew: {baseline.RaidPoints} before, {stocked.RaidPoints} after"));
        }

        if (problems.Count > 0)
        {
            throw new AcceptanceException(string.Join("; ", problems));
        }
    }
}
