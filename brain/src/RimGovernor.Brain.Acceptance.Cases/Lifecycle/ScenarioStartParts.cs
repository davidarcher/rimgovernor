using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/scenario-start-parts</c>: the harness ScenarioStart carrying a storyteller override and scenario parts
/// (#2754): a one-colonist start under Randy with a forced trait and a scattered thing, and the generated save shows
/// each landed. The runner opens the start; the start itself is the claim, so the quiet swap is skipped.
/// </summary>
internal static class LifecycleScenarioStartParts
{
    private const string Save = "scenario-start-parts-2754";
    private const string Storyteller = "Randy";
    private const string ThingDef = "Kibble";
    private const uint ThingCount = 15;

    internal static TypedCase Case() => new()
    {
        Name = "lifecycle/scenario-start-parts",
        Scope = "Issue #2754: a harness ScenarioStart with Storyteller Randy, a forced Jogger trait and a scattered Kibble stack starts a one-colonist colony; "
            + "the colonist carries the trait, and the saved game holds exactly the scattered count and names Randy as the storyteller. Spends no ticks.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new ScenarioStart { Spec = StartSpec() },
        ClaimsStart = new StartClaim { KeepsStoryteller = true },
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
        Parts = new Lifecycle.NewColonySpec
        {
            ForcedTraits = [new Lifecycle.ForcedTrait { Trait = "Jogger", Degree = 0, Chance = 1, Context = Lifecycle.ScenarioPawnContext.PlayerStarter }],
            StartingThings = [new Lifecycle.StartingThing { Placement = Lifecycle.StartingThingPlacement.Scattered, ThingDef = ThingDef, Count = ThingCount }],
        },
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        _ = await NewColonyStart.ForcedTraitAsync(s, "Jogger", cancellationToken).ConfigureAwait(false);
        var save = await s.ReadSaveFileAsync(Save, cancellationToken).ConfigureAwait(false);
        var total = NewColonyStart.ThingTotal(save, ThingDef);
        var teller = NewColonyStart.Storyteller(save);
        s.Record("scattered", total);
        s.Record("storyteller", teller);
        if (total != ThingCount || teller != Storyteller)
        {
            throw new AcceptanceException($"the save holds {total} scattered {ThingDef} under storyteller {teller ?? "none"}; want {ThingCount} under {Storyteller}");
        }
    }
}
