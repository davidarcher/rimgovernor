using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/scenario-start-parts</c> (Go <c>scenario_start_parts.go</c>): a harness ScenarioStart carrying a
/// storyteller override and scenario parts (#2754): a one-colonist start under Randy with a forced trait and a
/// scattered thing, and the generated save shows each landed.
/// </summary>
internal static class LifecycleScenarioStartParts
{
    private const string Save = "scenario-start-parts-2754";
    private const string Storyteller = "Randy";
    private const string ThingDef = "Kibble";
    private const int ThingCount = 15;

    private static readonly Regex Teller = new(@"<storyteller>\s*<def>(\w+)</def>", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(10));

    internal static AcceptanceCase Case() => new()
    {
        Name = "lifecycle/scenario-start-parts",
        Scope = "Issue #2754: a harness ScenarioStart with Storyteller Randy, a forced Jogger trait and a scattered Kibble stack starts a one-colonist colony; "
            + "the colonist carries the trait, the saved game holds exactly the scattered count and names Randy as the storyteller.",
        Start = new ScenarioStart { Spec = StartSpec() },
        Quiet = QuietMode.Loud,
        Reason = "the storyteller override is the claim: the quiet swap would replace Randy",
        Budget = TimeSpan.FromMinutes(20),
        Crew = new Crew { Size = 1 },
        Run = RunAsync,
    };

    private static ScenarioSpec StartSpec() => new()
    {
        Scenario = DebugGame.DebugScenario,
        Count = 1,
        Seed = "scenario-start-parts-2754",
        Difficulty = DebugGame.DebugDifficulty,
        Storyteller = Storyteller,
        Size = new DebugStartSize { MapSize = DebugStartSize.DefaultMapSize, PlanetCoverage = DebugStartSize.DefaultPlanetCoverage },
        SaveName = Save,
        Parts = new JsonObject
        {
            ["forcedTraits"] = new JsonArray(new JsonObject { ["trait"] = "Jogger", ["degree"] = 0, ["chance"] = 1, ["context"] = "player_starter" }),
            ["startingThings"] = new JsonArray(new JsonObject { ["placement"] = "scattered", ["thingDef"] = ThingDef, ["count"] = ThingCount }),
        },
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var row = new JsonObject();
        s.Report["scenario_start_parts"] = row;
        await NewColonyKit.CheckPartsTraitAsync(s.Harness, row, cancellationToken).ConfigureAwait(false);
        var path = s.Config.ProfileSave(Save);
        if (!File.Exists(path))
        {
            throw new AcceptanceException("the completed start left no save");
        }

        var save = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var total = LifecycleNewColonyParts.ThingTotal(save, ThingDef);
        row["scattered"] = total;
        if (total != ThingCount)
        {
            throw new AcceptanceException($"scattered {ThingDef}: {total} on the map, want {ThingCount}");
        }

        var teller = Teller.Match(save);
        if (!teller.Success || teller.Groups[1].Value != Storyteller)
        {
            throw new AcceptanceException($"the saved storyteller is {(teller.Success ? teller.Groups[1].Value : "none")}, want {Storyteller}");
        }

        row["storyteller"] = teller.Groups[1].Value;
    }
}
