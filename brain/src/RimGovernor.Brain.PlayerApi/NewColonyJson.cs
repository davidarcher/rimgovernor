using System.Globalization;
using System.Text.Json;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.PlayerApi;

/// <summary>A scenario-part enum name the request could not map; the route answers 400 naming the part.</summary>
internal sealed class ScenarioPartException(string message) : Exception(message);

/// <summary>
/// The new-colony request body (Go <c>NewColonyRequestDTO</c>, <c>NewColonySpecDTO</c> and <c>NewColonyPartsDTO</c>) as
/// a wire <see cref="NewColonyRequest"/>. Syntax is <see cref="StrictJson"/>'s. Beyond it a field the DTO does not
/// name is refused, a missing field reads as its zero value (the shared bridge validation then refuses a required
/// one), a wrong JSON type is refused, and the optional numbers stay absent when absent. Range and presence rules
/// are <c>ValidateNewColonyRequest</c>'s, not this decoder's.
/// </summary>
internal static class NewColonyJson
{
    private static readonly string[] RequestKeys = ["requestId", "timeoutMs", "spec"];

    private static readonly string[] SpecKeys =
    [
        "scenario", "colonistCount", "seed", "biomes", "flatTile", "difficulty", "storyteller", "minTemperature", "maxTemperature",
        "worldTemperature", "mapSize", "planetCoverage", "saveName", "ideoligion", "governorIdeoligion",
        "startingThings", "forcedTraits", "forcedHediffs", "naked", "setNeedLevels", "permaGameConditions",
        "disallowedBuildings", "disabledIncidents", "playerFaction", "arrivalMethod",
    ];

    private static readonly string[] IdeoligionKeys = ["memes", "precepts", "fluid"];

    private static readonly string[] ThingKeys = ["placement", "thingDef", "stuff", "count", "quality", "allowRoofed"];

    private static readonly string[] TraitKeys = ["trait", "degree", "chance", "context"];

    private static readonly string[] HediffKeys = ["hediff", "severity", "chance", "context"];

    private static readonly string[] NakedKeys = ["chance", "context"];

    private static readonly string[] NeedKeys = ["need", "level", "chance", "context"];

    /// <summary>Decodes the request body; throws <see cref="StrictJsonException"/> for a malformed one and <see cref="ScenarioPartException"/> for a part enum name that maps to nothing.</summary>
    internal static NewColonyRequest Decode(byte[] body)
    {
        var root = Members.Of(StrictJson.Parse(body), RequestKeys);
        var spec = root.Child("spec");
        return new NewColonyRequest
        {
            RequestId = root.String("requestId") ?? "",
            TimeoutMs = root.UInt32("timeoutMs") ?? 0,
            Spec = Spec(spec is null ? Members.Of(EmptyObject, SpecKeys) : Members.Of(spec.Value, SpecKeys)),
        };
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private static NewColonySpec Spec(Members d)
    {
        var spec = new NewColonySpec
        {
            Scenario = d.String("scenario") ?? "",
            ColonistCount = d.UInt32("colonistCount") ?? 0,
            Seed = d.String("seed") ?? "",
            FlatTile = d.Bool("flatTile") ?? false,
            Difficulty = d.String("difficulty") ?? "",
            Storyteller = d.String("storyteller") ?? "",
            MapSize = d.UInt32("mapSize") ?? 0,
            PlanetCoverage = d.Single("planetCoverage") ?? 0,
            SaveName = d.String("saveName") ?? "",
        };
        spec = spec with { Biomes = [.. spec.Biomes, .. d.Strings("biomes")] };
        if (d.Single("minTemperature") is { } min)
        {
            spec = spec with { MinTemperature = min };
        }

        if (d.Single("maxTemperature") is { } max)
        {
            spec = spec with { MaxTemperature = max };
        }

        if (d.String("worldTemperature") is { Length: > 0 } world)
        {
            spec = spec with { WorldTemperature = world };
        }

        if (d.Child("ideoligion") is { } ideoligion)
        {
            var i = Members.Of(ideoligion, IdeoligionKeys);
            var design = new IdeoligionDesign();
            design = design with { Memes = [.. design.Memes, .. i.Strings("memes")] };
            design = design with { Precepts = [.. design.Precepts, .. i.Strings("precepts")] };
            if (i.Bool("fluid") is { } fluid)
            {
                design = design with { Fluid = fluid };
            }

            spec = spec with { Ideoligion = design };
        }

        if (d.Bool("governorIdeoligion") == true)
        {
            spec = spec with { GovernorIdeoligion = true };
        }

        return Parts(spec, d);
    }

    // Go applyNewColonyParts: a malformed enum name is an error; the rest is the bridge's validation.
    private static NewColonySpec Parts(NewColonySpec spec, Members d)
    {
        foreach (var element in d.Objects("startingThings"))
        {
            var t = Members.Of(element, ThingKeys);
            var thing = new StartingThing
            {
                Placement = EnumByName<StartingThingPlacement>(t.String("placement") ?? "", "STARTING_THING_PLACEMENT_", "placement"),
                ThingDef = t.String("thingDef") ?? "",
            };
            if (t.String("stuff") is { } stuff)
            {
                thing = thing with { Stuff = stuff };
            }

            if (t.UInt32("count") is { } count)
            {
                thing = thing with { Count = count };
            }

            if (t.String("quality") is { } quality)
            {
                thing = thing with { Quality = quality };
            }

            if (t.Bool("allowRoofed") is { } roofed)
            {
                thing = thing with { AllowRoofed = roofed };
            }

            spec = spec with { StartingThings = [.. spec.StartingThings, thing] };
        }

        foreach (var element in d.Objects("forcedTraits"))
        {
            var t = Members.Of(element, TraitKeys);
            var trait = new ForcedTrait { Trait = t.String("trait") ?? "", Context = PawnContext(t) };
            if (t.Int32("degree") is { } degree)
            {
                trait = trait with { Degree = degree };
            }

            if (t.Single("chance") is { } chance)
            {
                trait = trait with { Chance = chance };
            }

            spec = spec with { ForcedTraits = [.. spec.ForcedTraits, trait] };
        }

        foreach (var element in d.Objects("forcedHediffs"))
        {
            var h = Members.Of(element, HediffKeys);
            var hediff = new ForcedHediff { Hediff = h.String("hediff") ?? "", Context = PawnContext(h) };
            if (h.Single("severity") is { } severity)
            {
                hediff = hediff with { Severity = severity };
            }

            if (h.Single("chance") is { } chance)
            {
                hediff = hediff with { Chance = chance };
            }

            spec = spec with { ForcedHediffs = [.. spec.ForcedHediffs, hediff] };
        }

        foreach (var element in d.Objects("naked"))
        {
            var n = Members.Of(element, NakedKeys);
            var naked = new NakedPart { Context = PawnContext(n) };
            if (n.Single("chance") is { } chance)
            {
                naked = naked with { Chance = chance };
            }

            spec = spec with { Naked = [.. spec.Naked, naked] };
        }

        foreach (var element in d.Objects("setNeedLevels"))
        {
            var n = Members.Of(element, NeedKeys);
            var need = new SetNeedLevel { Need = n.String("need") ?? "", Context = PawnContext(n) };
            if (n.Single("level") is { } level)
            {
                need = need with { Level = level };
            }

            if (n.Single("chance") is { } chance)
            {
                need = need with { Chance = chance };
            }

            spec = spec with { SetNeedLevels = [.. spec.SetNeedLevels, need] };
        }

        spec = spec with { PermaGameConditions = [.. spec.PermaGameConditions, .. d.Strings("permaGameConditions")] };
        spec = spec with { DisallowedBuildings = [.. spec.DisallowedBuildings, .. d.Strings("disallowedBuildings")] };
        spec = spec with { DisabledIncidents = [.. spec.DisabledIncidents, .. d.Strings("disabledIncidents")] };
        if (d.String("playerFaction") is { Length: > 0 } faction)
        {
            spec = spec with { PlayerFaction = EnumByName<ScenarioPlayerFaction>(faction, "SCENARIO_PLAYER_FACTION_", "playerFaction") };
        }

        if (d.String("arrivalMethod") is { Length: > 0 } arrival)
        {
            spec = spec with { ArrivalMethod = EnumByName<ScenarioArrivalMethod>(arrival, "SCENARIO_ARRIVAL_METHOD_", "arrivalMethod") };
        }

        return spec;
    }

    private static ScenarioPawnContext PawnContext(Members m) =>
        EnumByName<ScenarioPawnContext>(m.String("context") ?? "", "SCENARIO_PAWN_CONTEXT_", "context");

    // Go enumByName: a lower-case wire name maps to the enum value of that prefixed upper-case name, never the zero value.
    private static T EnumByName<T>(string name, string prefix, string part)
        where T : struct, Enum
    {
        if (!Domain.Wire.TryParse<T>(prefix + name.ToUpperInvariant(), out var value) || EqualityComparer<T>.Default.Equals(value, default))
        {
            throw new ScenarioPartException($"{part}: unknown value \"{name}\"");
        }

        return value;
    }

    /// <summary>One JSON object whose members are checked against the names the DTO declares.</summary>
    private sealed class Members
    {
        private readonly JsonElement _object;

        private Members(JsonElement element) => _object = element;

        internal static Members Of(JsonElement element, string[] allowed)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new StrictJsonException("expected an object");
            }

            foreach (var property in element.EnumerateObject())
            {
                if (Array.IndexOf(allowed, property.Name) < 0)
                {
                    throw new StrictJsonException("unknown field");
                }
            }

            return new Members(element);
        }

        // A member that is absent or null reads as absent (Go leaves the zero value).
        private JsonElement? Find(string name) =>
            _object.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

        internal JsonElement? Child(string name) => Find(name);

        internal string? String(string name) => Find(name) is { } v ? StrictJson.String(v) : null;

        internal bool? Bool(string name) => Find(name) is { } v
            ? v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw new StrictJsonException("expected a boolean")
            : null;

        internal uint? UInt32(string name) => Find(name) is { } v
            ? v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var n) ? n : throw new StrictJsonException("expected an unsigned 32-bit integer")
            : null;

        internal int? Int32(string name) => Find(name) is { } v ? StrictJson.Int32(v) : null;

        internal float? Single(string name)
        {
            if (Find(name) is not { } v)
            {
                return null;
            }

            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var d) || Math.Abs(d) > float.MaxValue)
            {
                throw new StrictJsonException("expected a 32-bit float");
            }

            return (float)d;
        }

        internal IReadOnlyList<string> Strings(string name)
        {
            if (Find(name) is not { } v)
            {
                return [];
            }

            if (v.ValueKind != JsonValueKind.Array)
            {
                throw new StrictJsonException("expected an array");
            }

            return [.. v.EnumerateArray().Select(StrictJson.String)];
        }

        internal IReadOnlyList<JsonElement> Objects(string name)
        {
            if (Find(name) is not { } v)
            {
                return [];
            }

            if (v.ValueKind != JsonValueKind.Array)
            {
                throw new StrictJsonException("expected an array");
            }

            return [.. v.EnumerateArray()];
        }
    }
}

/// <summary>
/// Rhadamant's Sea Ice Community Challenge (Workshop 1565580939, 1.0 format) as scenario parts (Go
/// <c>RhadamantParts</c>): 800 silver, 50 survival meals, 300 wood, 450 steel, 30 components, 30 medicine and a rifle
/// at the start, 720 steel, 7 meals and 3 ship chunks scattered, forced Sanguine, Super-Immune and Jogger traits and
/// a permanent ColdSnap. Parts replace the base scenario's parts of their kind, so these lists are the whole of
/// them. Her cannibalism rules are not included.
/// </summary>
public static class ScenarioParts
{
    /// <summary>Returns <paramref name="spec"/> with the Rhadamant parts added.</summary>
    public static NewColonySpec ApplyRhadamant(NewColonySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var d = PolicyFunctions.RhadamantThings;
        var things = new List<StartingThing>(spec.StartingThings);
        var traits = new List<ForcedTrait>(spec.ForcedTraits);
        void Thing(StartingThingPlacement placement, string def, uint count) =>
            things.Add(new StartingThing { Placement = placement, ThingDef = def, Count = count });
        void Trait(string def, int degree) =>
            traits.Add(new ForcedTrait { Trait = def, Degree = degree, Chance = 1, Context = ScenarioPawnContext.PlayerStarter });

        const StartingThingPlacement start = StartingThingPlacement.StartWith;
        const StartingThingPlacement scattered = StartingThingPlacement.Scattered;
        Thing(start, d.Silver, 800);
        Thing(start, d.Meal, 50);
        Thing(start, d.Wood, 300);
        Thing(start, d.Steel, 450);
        Thing(start, d.Component, 30);
        Thing(start, d.Medicine, 30);
        Thing(start, d.Rifle, 1);
        Thing(scattered, d.Steel, 720);
        Thing(scattered, d.Meal, 7);
        Thing(scattered, d.ShipChunk, 3);
        Trait("NaturalMood", 2);
        Trait("Immunity", 1);
        Trait("SpeedOffset", 2);
        return spec with
        {
            StartingThings = [.. things],
            ForcedTraits = [.. traits],
            PermaGameConditions = [.. spec.PermaGameConditions, "ColdSnap"],
        };
    }
}
