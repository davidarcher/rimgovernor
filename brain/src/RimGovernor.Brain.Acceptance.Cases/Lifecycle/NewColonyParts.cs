using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/new_colony_parts</c> (Go <c>new_colony_parts.go</c>): scenario parts on the new-colony op (#2753), each
/// shown landed in the generated save; unknown defNames are refused before generation.
/// </summary>
internal static class LifecycleNewColonyParts
{
    private const string PartsSave = "newcolony-parts-accept";

    // Things the base scenario (Crashlanded) does not start with, so their map totals are exactly the parts' counts.
    private static readonly (string Placement, string Def, int Count)[] Things =
    [
        ("start_with", "Pemmican", 11),
        ("start_near", "Hay", 20),
        ("scattered", "Kibble", 15),
    ];

    private static readonly Regex StackCount = new(@"<stackCount>(\d+)</stackCount>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(10));
    private static readonly Regex ScenarioXml = new("<scenario>.*?</scenario>", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(10));
    private static readonly Regex Conditions = new("<activeConditions>(.*?)</activeConditions>", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(10));
    private static readonly Regex Disallowed = new("<disallowedBuildings>.*?<li>Heater</li>", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(10));

    internal static AcceptanceCase Case() => new()
    {
        Name = "lifecycle/new_colony_parts",
        Scope = "Scenario parts on the new-colony start (#2753): one colonist with a forced trait, a hediff, a need level, naked, start_with / start_near / scattered things, "
            + "a permanent ColdSnap (not a canBePermanent def), a disallowed building, a disabled incident and the new-tribe faction override; the save shows the trait on the pawn, "
            + "each thing count on the map, the condition active and permanent, and every part in the scenario copy; unknown defNames and an unusable part are refused with the field named before generation.",
        Start = new OwnedStart(),
        Reason = "the start needs a fresh main menu process; the case opens and retires its own game",
        NoKeep = true,
        Budget = TimeSpan.FromMinutes(20),
        Crew = new Crew { Size = 1 },
        Run = RunAsync,
    };

    /// <summary>The regex over one thing block of the save: <c>&lt;thing Class="..."&gt;&lt;def&gt;DEF&lt;/def&gt;(...)&lt;/thing&gt;</c>; shared with the scenario-start-parts case.</summary>
    internal static Regex ThingBlock(string def) =>
        new("<thing Class=\"[^\"]*\">\\s*<def>" + Regex.Escape(def) + "</def>(.*?)</thing>", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(10));

    /// <summary>The sum of a def's stacks in the save text (an omitted stackCount is 1).</summary>
    internal static int ThingTotal(string save, string def)
    {
        var total = 0;
        foreach (Match m in ThingBlock(def).Matches(save))
        {
            var count = StackCount.Match(m.Groups[1].Value);
            total += count.Success ? int.Parse(count.Groups[1].Value, CultureInfo.InvariantCulture) : 1;
        }

        return total;
    }

    private static JsonObject PartsSpec()
    {
        var spec = NewColonyKit.Spec();
        spec["colonistCount"] = 1;
        spec["saveName"] = PartsSave;
        spec["seed"] = "newcolony-parts-accept";
        spec["startingThings"] = new JsonArray([.. Things.Select(t => (JsonNode?)new JsonObject { ["placement"] = t.Placement, ["thingDef"] = t.Def, ["count"] = t.Count })]);
        spec["forcedTraits"] = new JsonArray(new JsonObject { ["trait"] = "Jogger", ["degree"] = 0, ["chance"] = 1, ["context"] = "player_starter" });
        spec["forcedHediffs"] = new JsonArray(new JsonObject { ["hediff"] = "ToxicBuildup", ["severity"] = 0.1, ["chance"] = 1, ["context"] = "player_starter" });
        spec["naked"] = new JsonArray(new JsonObject { ["chance"] = 1, ["context"] = "player_starter" });
        spec["setNeedLevels"] = new JsonArray(new JsonObject { ["need"] = "Rest", ["level"] = 0.5, ["chance"] = 1, ["context"] = "player_starter" });
        spec["permaGameConditions"] = new JsonArray("ColdSnap");
        spec["disallowedBuildings"] = new JsonArray("Heater");
        spec["disabledIncidents"] = new JsonArray("WandererJoin");
        spec["playerFaction"] = "new_tribe";
        spec["arrivalMethod"] = "standing";
        return spec;
    }

    private static Task RunAsync(ISession s, CancellationToken cancellationToken) =>
        NewColonyKit.MenuAsync(
            s,
            "parts",
            NewColonyKit.MenuTools,
            async (h, row) =>
            {
                await RefusalsAsync(h, row, cancellationToken).ConfigureAwait(false);
                _ = await NewColonyKit.RunAsync(h, $"newcolony-parts-{LifecycleCases.Stamp()}", PartsSpec(), "parts", cancellationToken).ConfigureAwait(false);
                await NewColonyKit.CheckPartsTraitAsync(h, row, cancellationToken).ConfigureAwait(false);
                var path = s.Config.ProfileSave(PartsSave);
                if (!File.Exists(path))
                {
                    throw new AcceptanceException("the completed start left no save");
                }

                CheckSave(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), row);
            },
            cancellationToken);

    /// <summary>An unknown name in each part kind, and parts the game cannot use, are refused before generation, naming the field.</summary>
    private static async Task RefusalsAsync(Harness h, JsonObject row, CancellationToken cancellationToken)
    {
        var refusals = new JsonObject();
        row["refusals"] = refusals;
        var cases = new (string Name, string Field, JsonNode Value, string Contains)[]
        {
            ("thing", "startingThings", new JsonArray(new JsonObject { ["placement"] = "start_with", ["thingDef"] = "NoSuchThing", ["count"] = 1 }), "starting_things[0].thing_def"),
            ("trait", "forcedTraits", new JsonArray(new JsonObject { ["trait"] = "NoSuchTrait", ["degree"] = 0, ["chance"] = 1, ["context"] = "all" }), "forced_traits[0].trait"),
            ("trait degree", "forcedTraits", new JsonArray(new JsonObject { ["trait"] = "Jogger", ["degree"] = 3, ["chance"] = 1, ["context"] = "all" }), "forced_traits[0].degree"),
            ("hediff", "forcedHediffs", new JsonArray(new JsonObject { ["hediff"] = "NoSuchHediff", ["severity"] = 0.1, ["chance"] = 1, ["context"] = "all" }), "forced_hediffs[0].hediff"),
            ("need", "setNeedLevels", new JsonArray(new JsonObject { ["need"] = "NoSuchNeed", ["level"] = 0.5, ["chance"] = 1, ["context"] = "all" }), "set_need_levels[0].need"),
            ("condition", "permaGameConditions", new JsonArray("NoSuchCondition"), "perma_game_conditions[0]"),
            ("building", "disallowedBuildings", new JsonArray("NoSuchBuilding"), "disallowed_buildings[0]"),
            ("incident", "disabledIncidents", new JsonArray("NoSuchIncident"), "disabled_incidents[0]"),
            ("stuff on a stuffless thing", "startingThings", new JsonArray(new JsonObject { ["placement"] = "start_with", ["thingDef"] = "Silver", ["stuff"] = "Steel", ["count"] = 1 }), "starting_things[0].stuff"),
        };
        foreach (var (name, field, value, contains) in cases)
        {
            var spec = PartsSpec();
            spec[field] = value;
            var reply = await h.WireAsync(
                "refuse-" + name,
                "lifecycle_new_colony",
                NewColonyKit.Request("newcolony-parts-bad-" + name.Replace(' ', '-'), spec),
                cancellationToken).ConfigureAwait(false);
            try
            {
                NewColonyKit.WantFailure(reply, "FAILURE_CODE_INVALID_REQUEST", contains);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"bad {name}: {e.Message}", e);
            }

            refusals[name] = true;
        }
    }

    /// <summary>Asserts, from the saved game, that each part took effect.</summary>
    private static void CheckSave(string save, JsonObject row)
    {
        // Thing counts: the sum of a def's stacks (an omitted stackCount is 1).
        var counts = new JsonObject();
        foreach (var (placement, def, want) in Things)
        {
            var total = ThingTotal(save, def);
            counts[def] = total;
            if (total != want)
            {
                throw new AcceptanceException($"{placement} {def}: {total} on the map, want {want}");
            }
        }

        row["thing_counts"] = counts;

        // The permanent condition is active on the colony map.
        var active = string.Join('\n', Conditions.Matches(save).Select(m => m.Groups[1].Value));
        if (!active.Contains("<def>ColdSnap</def>", StringComparison.Ordinal) || !active.Contains("<permanent>True</permanent>", StringComparison.Ordinal))
        {
            throw new AcceptanceException("ColdSnap is not an active permanent condition in the save");
        }

        // The scenario copy holds the parts and the overrides.
        var scenarioMatch = ScenarioXml.Match(save);
        if (!scenarioMatch.Success)
        {
            throw new AcceptanceException("the save has no scenario");
        }

        var scenario = scenarioMatch.Value;
        foreach (var want in new[]
        {
            "Class=\"ScenPart_ForcedTrait\"", "<trait>Jogger</trait>",
            "Class=\"ScenPart_ForcedHediff\"", "<hediff>ToxicBuildup</hediff>",
            "Class=\"ScenPart_Naked\"",
            "Class=\"ScenPart_SetNeedLevel\"", "<need>Rest</need>",
            "Class=\"ScenPart_PermaGameCondition\"", "<gameCondition>ColdSnap</gameCondition>",
            "Class=\"ScenPart_DisallowBuilding\"", "<building>Heater</building>",
            "Class=\"ScenPart_DisableIncident\"", "<incident>WandererJoin</incident>",
            "Class=\"ScenPart_StartingThing_Defined\"", "Class=\"ScenPart_ScatterThingsNearPlayerStart\"", "Class=\"ScenPart_ScatterThingsAnywhere\"",
            "<factionDef>PlayerTribe</factionDef>",
        })
        {
            if (!scenario.Contains(want, StringComparison.Ordinal))
            {
                throw new AcceptanceException($"the saved scenario lacks {want}");
            }
        }

        // The disallowed building is in force.
        if (!Disallowed.IsMatch(save))
        {
            throw new AcceptanceException("the saved game rules do not disallow Heater");
        }

        // Replace by kind: the base's own things and its absence of the supplied kinds' parts mean Crashlanded's Silver
        // and rifle are gone, not layered under.
        foreach (var gone in new[] { "<thingDef>Silver</thingDef>", "<thingDef>Gun_BoltActionRifle</thingDef>", "<thingDef>MealSurvivalPack</thingDef>" })
        {
            if (scenario.Contains(gone, StringComparison.Ordinal))
            {
                throw new AcceptanceException($"the saved scenario still holds the base's {gone}; the supplied things should replace them");
            }
        }

        row["scenario_parts"] = true;
    }
}
