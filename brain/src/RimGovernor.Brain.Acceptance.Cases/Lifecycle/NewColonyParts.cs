using System.Xml.Linq;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/new_colony_parts</c>: scenario parts on the new-colony op (#2753), each shown landed in the generated
/// save (read as XML: no observation reports a scenario part); an unknown defName in each part kind, and a part the
/// game cannot use, are refused before generation.
/// </summary>
internal static class LifecycleNewColonyParts
{
    private const string PartsSave = "newcolony-parts-accept";

    // Things the base scenario (Crashlanded) does not start with, so their map totals are exactly the parts' counts.
    // Crashlanded's base things the supplied ones replace.
    private static readonly string[] ReplacedBaseThings = ["Silver", "Gun_BoltActionRifle", "MealSurvivalPack"];

    private static readonly (Lifecycle.StartingThingPlacement Placement, string Def, uint Count)[] Things =
    [
        (Lifecycle.StartingThingPlacement.StartWith, "Pemmican", 11),
        (Lifecycle.StartingThingPlacement.StartNear, "Hay", 20),
        (Lifecycle.StartingThingPlacement.Scattered, "Kibble", 15),
    ];

    private const Lifecycle.ScenarioPawnContext Starter = Lifecycle.ScenarioPawnContext.PlayerStarter;
    private const Lifecycle.ScenarioPawnContext All = Lifecycle.ScenarioPawnContext.All;

    internal static TypedCase Case() => new()
    {
        Name = "lifecycle/new_colony_parts",
        Scope = "Scenario parts on the new-colony start (#2753): one colonist with a forced trait, a hediff, a need level, naked, start_with / start_near / scattered things, "
            + "a permanent ColdSnap (not a canBePermanent def), a disallowed building, a disabled incident and the new-tribe faction override; the colonist carries the trait "
            + "and the saved game holds each thing count on the map, the condition active and permanent, the building disallowed and every part in the scenario copy, the "
            + "base scenario's things replaced; an unknown defName in each part kind and an unusable part are INVALID_REQUEST before generation. Spends no ticks.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new OwnedStart(),
        ClaimsStart = new StartClaim { Launches = 1 },
        Run = RunAsync,
    };

    private static Lifecycle.NewColonySpec PartsSpec() => NewColonyStart.Spec() with
    {
        ColonistCount = 1,
        SaveName = PartsSave,
        Seed = "newcolony-parts-accept",
        StartingThings = [.. Things.Select(t => new Lifecycle.StartingThing { Placement = t.Placement, ThingDef = t.Def, Count = t.Count })],
        ForcedTraits = [new Lifecycle.ForcedTrait { Trait = "Jogger", Degree = 0, Chance = 1, Context = Starter }],
        ForcedHediffs = [new Lifecycle.ForcedHediff { Hediff = "ToxicBuildup", Severity = 0.1f, Chance = 1, Context = Starter }],
        Naked = [new Lifecycle.NakedPart { Chance = 1, Context = Starter }],
        SetNeedLevels = [new Lifecycle.SetNeedLevel { Need = "Rest", Level = 0.5f, Chance = 1, Context = Starter }],
        PermaGameConditions = ["ColdSnap"],
        DisallowedBuildings = ["Heater"],
        DisabledIncidents = ["WandererJoin"],
        PlayerFaction = Lifecycle.ScenarioPlayerFaction.NewTribe,
        ArrivalMethod = Lifecycle.ScenarioArrivalMethod.Standing,
    };

    private static Task RunAsync(ITypedSession s, CancellationToken cancellationToken) =>
        NewColonyStart.MenuAsync(
            s,
            "parts",
            async () =>
            {
                await RefusalsAsync(s, cancellationToken).ConfigureAwait(false);
                _ = await NewColonyStart.CompleteAsync(s, "parts", NewColonyStart.Request(TypedLifecycle.Stamp("newcolony-parts"), PartsSpec()), cancellationToken).ConfigureAwait(false);
                _ = await NewColonyStart.ForcedTraitAsync(s, "Jogger", cancellationToken).ConfigureAwait(false);
                CheckSave(s, await s.ReadSaveFileAsync(PartsSave, cancellationToken).ConfigureAwait(false));
            },
            cancellationToken);

    /// <summary>An unknown name in each part kind, and parts the game cannot use, are refused before generation.</summary>
    private static async Task RefusalsAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var spec = PartsSpec();
        var bad = new (string Name, Lifecycle.NewColonySpec Spec)[]
        {
            ("thing", spec with { StartingThings = [new() { Placement = Lifecycle.StartingThingPlacement.StartWith, ThingDef = "NoSuchThing", Count = 1 }] }),
            ("trait", spec with { ForcedTraits = [new() { Trait = "NoSuchTrait", Degree = 0, Chance = 1, Context = All }] }),
            ("trait-degree", spec with { ForcedTraits = [new() { Trait = "Jogger", Degree = 3, Chance = 1, Context = All }] }),
            ("hediff", spec with { ForcedHediffs = [new() { Hediff = "NoSuchHediff", Severity = 0.1f, Chance = 1, Context = All }] }),
            ("need", spec with { SetNeedLevels = [new() { Need = "NoSuchNeed", Level = 0.5f, Chance = 1, Context = All }] }),
            ("condition", spec with { PermaGameConditions = ["NoSuchCondition"] }),
            ("building", spec with { DisallowedBuildings = ["NoSuchBuilding"] }),
            ("incident", spec with { DisabledIncidents = ["NoSuchIncident"] }),
            ("stuffless-stuff", spec with { StartingThings = [new() { Placement = Lifecycle.StartingThingPlacement.StartWith, ThingDef = "Silver", Stuff = "Steel", Count = 1 }] }),
        };
        foreach (var (name, refused) in bad)
        {
            await NewColonyStart.RefusedAsync(s, "refuse-" + name, NewColonyStart.Request("newcolony-parts-bad-" + name, refused), cancellationToken).ConfigureAwait(false);
        }

        s.Record("refusals", NewColonyStart.Strings(bad.Select(b => b.Name)));
    }

    /// <summary>Asserts, from the saved game, that each part took effect.</summary>
    private static void CheckSave(ITypedSession s, XDocument save)
    {
        foreach (var (placement, def, want) in Things)
        {
            var total = NewColonyStart.ThingTotal(save, def);
            if (total != want)
            {
                throw new AcceptanceException($"{placement} {def}: {total} on the map, want {want}");
            }
        }

        // The permanent condition is active on the colony map.
        if (!save.Descendants("activeConditions").Elements("li").Any(c => (string?)c.Element("def") == "ColdSnap" && (string?)c.Element("permanent") == "True"))
        {
            throw new AcceptanceException("ColdSnap is not an active permanent condition in the save");
        }

        // The disallowed building is in force in the game rules.
        if (!save.Descendants("disallowedBuildings").Elements("li").Any(b => b.Value == "Heater"))
        {
            throw new AcceptanceException("the saved game rules do not disallow Heater");
        }

        // The scenario copy holds the parts and the overrides.
        var scenario = save.Descendants("scenario").FirstOrDefault() ?? throw new AcceptanceException("the save has no scenario");
        var parts = scenario.Descendants("li").Select(li => (string?)li.Attribute("Class")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var part in new[]
        {
            "ScenPart_ForcedTrait", "ScenPart_ForcedHediff", "ScenPart_Naked", "ScenPart_SetNeedLevel", "ScenPart_PermaGameCondition",
            "ScenPart_DisallowBuilding", "ScenPart_DisableIncident", "ScenPart_StartingThing_Defined", "ScenPart_ScatterThingsNearPlayerStart",
            "ScenPart_ScatterThingsAnywhere",
        })
        {
            if (!parts.Contains(part))
            {
                throw new AcceptanceException($"the saved scenario lacks a {part}");
            }
        }

        foreach (var (element, value) in new[]
        {
            ("trait", "Jogger"), ("hediff", "ToxicBuildup"), ("need", "Rest"), ("gameCondition", "ColdSnap"), ("building", "Heater"),
            ("incident", "WandererJoin"), ("factionDef", "PlayerTribe"),
        })
        {
            if (!scenario.Descendants(element).Any(e => e.Value == value))
            {
                throw new AcceptanceException($"the saved scenario has no {element} {value}");
            }
        }

        // Replace by kind: the supplied things replace Crashlanded's Silver, rifle and survival packs rather than layer under them.
        var things = scenario.Descendants("thingDef").Select(e => e.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in ReplacedBaseThings.Where(things.Contains))
        {
            throw new AcceptanceException($"the saved scenario still holds the base's {gone}; the supplied things should replace it");
        }

        s.Record("scenario_parts", NewColonyStart.Strings(parts.Order(StringComparer.Ordinal)));
    }
}
