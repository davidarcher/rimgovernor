using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using WireLifecycle = RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Launcher;

/// <summary>One choice in a list. <see cref="Requires"/> names the DLC it needs (absent for the base game); native refuses a def the install lacks.</summary>
public sealed record Choice(string Value, string Label, string? Requires = null);

/// <summary>One trait degree: <see cref="Value"/> is the TraitDef, <see cref="Degree"/> the TraitDegreeData degree (0 for a trait without degrees).</summary>
public sealed record TraitOption(string Value, int Degree, string Label);

public sealed record StartingThingPart
{
    public string Placement { get; init; } = "";

    public string ThingDef { get; init; } = "";

    public string? Stuff { get; init; }

    public uint? Count { get; init; }

    public string? Quality { get; init; }

    public bool? AllowRoofed { get; init; }
}

public sealed record ForcedTraitPart
{
    public string Trait { get; init; } = "";

    public int? Degree { get; init; }

    public float? Chance { get; init; }

    public string Context { get; init; } = "";
}

public sealed record ForcedHediffPart
{
    public string Hediff { get; init; } = "";

    public float? Severity { get; init; }

    public float? Chance { get; init; }

    public string Context { get; init; } = "";
}

public sealed record NakedPawnPart
{
    public float? Chance { get; init; }

    public string Context { get; init; } = "";
}

public sealed record NeedLevelPart
{
    public string Need { get; init; } = "";

    public float? Level { get; init; }

    public float? Chance { get; init; }

    public string Context { get; init; } = "";
}

/// <summary>
/// The New colony form (Go <c>NewColonySpec</c>): the wire spec with the seed optional (empty asks for a random one,
/// drawn at Generate) and the temperature band optional, plus the scenario parts. The settings file, the page and the
/// game's HTTP call share this one JSON shape; lists are absent when empty.
/// </summary>
public sealed record NewColonySpec
{
    public string Scenario { get; init; } = "";

    public int ColonistCount { get; init; }

    public string Seed { get; init; } = "";

    public IReadOnlyList<string>? Biomes { get; init; }

    public bool FlatTile { get; init; }

    public string Difficulty { get; init; } = "";

    public string Storyteller { get; init; } = "";

    public float? MinTemperature { get; init; }

    public float? MaxTemperature { get; init; }

    public string WorldTemperature { get; init; } = "";

    public int MapSize { get; init; }

    public float PlanetCoverage { get; init; }

    public string SaveName { get; init; } = "";

    public IReadOnlyList<StartingThingPart>? StartingThings { get; init; }

    public IReadOnlyList<ForcedTraitPart>? ForcedTraits { get; init; }

    public IReadOnlyList<ForcedHediffPart>? ForcedHediffs { get; init; }

    public IReadOnlyList<NakedPawnPart>? Naked { get; init; }

    public IReadOnlyList<NeedLevelPart>? SetNeedLevels { get; init; }

    public IReadOnlyList<string>? PermaGameConditions { get; init; }

    public IReadOnlyList<string>? DisallowedBuildings { get; init; }

    public IReadOnlyList<string>? DisabledIncidents { get; init; }

    public string? PlayerFaction { get; init; }

    public string? ArrivalMethod { get; init; }

    /// <summary>The tribal-8 spec. <see cref="SaveName"/> is empty: Generate derives it and replaces any name a caller sends.</summary>
    public static NewColonySpec Default { get; } = new()
    {
        Scenario = "LostTribe", ColonistCount = 8, Biomes = ["TemperateForest"], Difficulty = "Medium",
        Storyteller = "Cassandra", WorldTemperature = "LittleBitColder", MapSize = 250, PlanetCoverage = 0.3f,
    };

    /// <summary>The spec as the game's HTTP call carries it: the chosen seed and a biomes list that is never absent.</summary>
    public NewColonySpec ForWire(string seed) => this with { Seed = seed, Biomes = Biomes ?? [] };
}

/// <summary>A named, complete form: selecting it fills every row.</summary>
public sealed record NewColonyPreset(string Name, string Label, NewColonySpec Spec);

/// <summary>The curated lists, in display order (Go <c>NewColonyOptions</c>).</summary>
public sealed record NewColonyOptions(
    IReadOnlyList<Choice> Scenarios,
    IReadOnlyList<Choice> Biomes,
    IReadOnlyList<Choice> Difficulties,
    IReadOnlyList<Choice> Storytellers,
    IReadOnlyList<Choice> WorldTemperatures,
    IReadOnlyList<Choice> MapSizes,
    IReadOnlyList<Choice> PlanetCoverages);

/// <summary>The lists the scenario composer rows draw from. Placement, context, faction and arrival values are the wire's lower-case enum names.</summary>
public sealed record ScenarioChoices(
    IReadOnlyList<Choice> Placements,
    IReadOnlyList<Choice> Contexts,
    IReadOnlyList<Choice> Things,
    IReadOnlyList<TraitOption> Traits,
    IReadOnlyList<Choice> Hediffs,
    IReadOnlyList<Choice> Conditions,
    IReadOnlyList<Choice> Buildings,
    IReadOnlyList<Choice> Incidents,
    IReadOnlyList<Choice> PlayerFactions,
    IReadOnlyList<Choice> ArrivalMethods);

/// <summary>Values that seed a new composer row.</summary>
public sealed record ScenarioPartDefaults(string Placement, uint Count, float Chance, string Context, float Severity);

/// <summary>The validation ranges the form shows.</summary>
public sealed record NewColonyRanges(int ColonistsMin, int ColonistsMax, double TemperatureAbs, int PartsPerKind, int ThingCountMax, int TraitDegreeAbs, double SeverityMax);

/// <summary>Generation states. Active ones: returning (a reused game to the main menu), restarting, connecting, generating.</summary>
public static class ColonyState
{
    public const string Idle = "idle";
    public const string Returning = "returning";
    public const string Restarting = "restarting";
    public const string Connecting = "connecting";
    public const string Generating = "generating";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// The last good progress. Phase, Detail, NativeElapsedMs and RerollCount are the newest native pending reply;
/// ElapsedMs is the launcher's own clock since Generate, so a page timer keeps ticking while native blocks. A failed
/// poll marks it Stale with StaleError and keeps the values; Error is the terminal failure or notice.
/// </summary>
public sealed record NewColonyProgress
{
    public string State { get; init; } = ColonyState.Idle;

    public string? RequestId { get; init; }

    public string? Phase { get; init; }

    public string? Detail { get; init; }

    public ulong NativeElapsedMs { get; init; }

    public uint RerollCount { get; init; }

    public long ElapsedMs { get; init; }

    public bool Stale { get; init; }

    public string? StaleError { get; init; }

    public string? SaveName { get; init; }

    public string? Seed { get; init; }

    public string? Error { get; init; }
}

/// <summary>What the page polls.</summary>
public sealed record NewColonyView(
    NewColonyOptions Options,
    ScenarioChoices Parts,
    ScenarioPartDefaults PartDefaults,
    IReadOnlyList<NewColonyPreset> Presets,
    NewColonyRanges Ranges,
    NewColonySpec Defaults,
    NewColonySpec Spec,
    NewColonyProgress Progress)
{
    /// <summary>A one-line notice when generation cannot run (Observe mode).</summary>
    public string? Unavailable { get; init; }
}

/// <summary>The form's lists and validation (Go <c>newcolony.go</c>, <c>newcolony_parts.go</c>).</summary>
public static partial class NewColony
{
    public const string SavePrefix = "RimGovernor-";

    public static NewColonyOptions Options { get; } = new(
        [
            new("Crashlanded", "Crashlanded"),
            new("LostTribe", "Lost Tribe"),
            new("TheRichExplorer", "The Rich Explorer"),
            new("NakedBrutality", "Naked Brutality"),
            new("Mechanitor", "Mechanitor", "Biotech"),
            new("Sanguophage", "Sanguophage", "Biotech"),
            new("TheAnomaly", "The Anomaly", "Anomaly"),
            new("TheGravship", "The Gravship", "Odyssey"),
        ],
        [
            new("TemperateForest", "Temperate forest"),
            new("TemperateSwamp", "Temperate swamp"),
            new("BorealForest", "Boreal forest"),
            new("Tundra", "Tundra"),
            new("ColdBog", "Cold bog"),
            new("IceSheet", "Ice sheet"),
            new("SeaIce", "Sea ice"),
            new("TropicalRainforest", "Tropical rainforest"),
            new("TropicalSwamp", "Tropical swamp"),
            new("AridShrubland", "Arid shrubland"),
            new("Desert", "Desert"),
            new("ExtremeDesert", "Extreme desert"),
            new("Grasslands", "Grassland", "Odyssey"),
            new("Glowforest", "Glowforest", "Odyssey"),
            new("GlacialPlain", "Glacial plain", "Odyssey"),
            new("Scarlands", "Scarlands", "Odyssey"),
            new("LavaField", "Lava field", "Odyssey"),
        ],
        [
            new("Peaceful", "Peaceful"),
            new("Easy", "Easy"),
            new("Medium", "Medium"),
            new("Rough", "Rough"),
            new("Hard", "Hard"),
            new("Extreme", "Extreme"),
        ],
        [
            new("Cassandra", "Cassandra Classic"),
            new("Phoebe", "Phoebe Chillax"),
            new("Randy", "Randy Random"),
            new("RimGovernorQuiet", "Quiet (no incidents)"),
        ],
        [
            new("VeryCold", "Very cold"),
            new("Cold", "Cold"),
            new("LittleBitColder", "A little bit colder"),
            new("Normal", "Normal"),
            new("LittleBitWarmer", "A little bit warmer"),
            new("Hot", "Hot"),
            new("VeryHot", "Very hot"),
        ],
        // Native map sizes are 200 to 325 in steps of 25, labelled by the MapSizeDesc key; 350 and 400 are test-only.
        [
            new("200", "200x200 (40000 cells)"),
            new("225", "225x225 (50625 cells)"),
            new("250", "250x250 (62500 cells)"),
            new("275", "275x275 (75625 cells)"),
            new("300", "300x300 (90000 cells)"),
            new("325", "325x325 (105625 cells)"),
        ],
        // Native planet coverages are 0.3, 0.5 and 1; 0.05 is dev mode only.
        [
            new("0.3", "30% (about 15 s to start)"),
            new("0.5", "50% (about 20 s to start)"),
            new("1", "100% (about 45 s to start)"),
        ]);

    public static ScenarioChoices Choices { get; } = new(
        [
            new("start_with", "Start with (carried)"),
            new("start_near", "Start near (on the ground)"),
            new("scattered", "Scattered over the map"),
        ],
        [
            new("player_starter", "Starting colonists"),
            new("all", "All pawns"),
            new("non_player", "Non-colonists"),
        ],
        ThingOptions(DefTablesRules.ScenarioThingChoices),
        [
            new("NaturalMood", 2, "Sanguine"),
            new("NaturalMood", 1, "Optimist"),
            new("NaturalMood", -1, "Pessimist"),
            new("NaturalMood", -2, "Depressive"),
            new("Immunity", 1, "Super-immune"),
            new("Immunity", -1, "Sickly"),
            new("SpeedOffset", 2, "Jogger"),
            new("SpeedOffset", 1, "Fast walker"),
            new("SpeedOffset", -1, "Slowpoke"),
            new("Nerves", 2, "Iron-willed"),
            new("Nerves", 1, "Steadfast"),
            new("Nerves", -1, "Nervous"),
            new("Nerves", -2, "Volatile"),
            new("Industriousness", 2, "Industrious"),
            new("Industriousness", 1, "Hard worker"),
            new("Industriousness", -1, "Lazy"),
            new("Industriousness", -2, "Slothful"),
            new("Neurotic", 1, "Neurotic"),
            new("Neurotic", 2, "Very neurotic"),
            new("ShootingAccuracy", 1, "Careful shooter"),
            new("ShootingAccuracy", -1, "Trigger-happy"),
            new("Beauty", 2, "Beautiful"),
            new("Beauty", -2, "Staggeringly ugly"),
        ],
        [
            new("Hypothermia", "Hypothermia"),
            new("Heatstroke", "Heatstroke"),
            new("Malnutrition", "Malnutrition"),
            new("Asthma", "Asthma"),
            new("Cataract", "Cataract"),
        ],
        [
            new("ColdSnap", "Cold snap"),
            new("HeatWave", "Heat wave"),
            new("ToxicFallout", "Toxic fallout"),
            new("VolcanicWinter", "Volcanic winter"),
            new("Eclipse", "Eclipse"),
            new("Aurora", "Aurora"),
        ],
        [],
        [
            new("RaidEnemy", "Enemy raid"),
            new("Infestation", "Infestation"),
            new("ManhunterPack", "Manhunter pack"),
            new("ShipChunkDrop", "Ship chunk drop"),
            new("ColdSnap", "Cold snap"),
            new("HeatWave", "Heat wave"),
            new("ToxicFallout", "Toxic fallout"),
            new("Eclipse", "Eclipse"),
            new("Disease_Flu", "Flu"),
            new("WandererJoin", "Wanderer joins"),
            new("RefugeePodCrash", "Refugee pod crash"),
            new("ResourcePodCrash", "Resource pod crash"),
            new("TraderCaravanArrival", "Trader caravan"),
            new("VisitorGroup", "Visitors"),
        ],
        [
            new("", "Scenario's own"),
            new("new_arrivals", "New arrivals"),
            new("new_tribe", "New tribe"),
        ],
        [
            new("", "Scenario's own"),
            new("standing", "Standing"),
            new("drop_pods", "Drop pods"),
        ]);

    public static ScenarioPartDefaults PartDefaults { get; } = new("start_with", 1, 1, "player_starter", 1);

    public static NewColonyRanges Ranges { get; } = new(
        1, NewColonyBounds.MaxColonists, NewColonyBounds.MaxTemperature, NewColonyBounds.MaxPartsPerKind,
        NewColonyBounds.MaxThingCount, NewColonyBounds.MaxTraitDegree, NewColonyBounds.MaxSeverity);

    public static IReadOnlyList<NewColonyPreset> Presets { get; } = [new("rhadamant", "Rhadamant's Sea Ice challenge", Rhadamant())];

    /// <summary>
    /// Rhadamant's Sea Ice Community Challenge on Crashlanded (Go <c>RhadamantParts</c>; the same parts as the
    /// brain's <c>ScenarioParts.ApplyRhadamant</c>). The seed is a pinned default the player may edit; "Losing is fun"
    /// is Extreme.
    /// </summary>
    private static NewColonySpec Rhadamant()
    {
        var d = DefTablesRules.RhadamantThings;
        StartingThingPart Thing(string placement, string def, uint count) => new() { Placement = placement, ThingDef = def, Count = count };
        ForcedTraitPart Trait(string def, int degree) => new() { Trait = def, Degree = degree, Chance = 1, Context = "player_starter" };
        return new NewColonySpec
        {
            Scenario = "Crashlanded", ColonistCount = 1, Seed = "SeaIce", Biomes = ["SeaIce"], Difficulty = "Extreme",
            Storyteller = "Randy", WorldTemperature = "VeryCold", MapSize = 250, PlanetCoverage = 0.3f,
            StartingThings =
            [
                Thing("start_with", d.Silver, 800), Thing("start_with", d.Meal, 50), Thing("start_with", d.Wood, 300),
                Thing("start_with", d.Steel, 450), Thing("start_with", d.Component, 30), Thing("start_with", d.Medicine, 30),
                Thing("start_with", d.Rifle, 1), Thing("scattered", d.Steel, 720), Thing("scattered", d.Meal, 7),
                Thing("scattered", d.ShipChunk, 3),
            ],
            ForcedTraits = [Trait("NaturalMood", 2), Trait("Immunity", 1), Trait("SpeedOffset", 2)],
            PermaGameConditions = ["ColdSnap"],
        };
    }

    /// <summary>Labels each def by its name split at capitals and underscores.</summary>
    private static List<Choice> ThingOptions(IReadOnlyList<string> defs)
    {
        var options = new List<Choice>(defs.Count);
        foreach (var def in defs)
        {
            var label = new System.Text.StringBuilder();
            for (var i = 0; i < def.Length; i++)
            {
                var c = def[i];
                if (c == '_')
                {
                    label.Append(' ');
                }
                else if (i > 0 && char.IsUpper(c))
                {
                    label.Append(' ').Append(char.ToLowerInvariant(c));
                }
                else
                {
                    label.Append(c);
                }
            }

            options.Add(new Choice(def, label.ToString()));
        }

        return options;
    }

    /// <summary>
    /// The save name for <paramref name="spec"/> and <paramref name="seed"/>: RimGovernor-scenario-biome-seed ("any"
    /// for no biome, "multi" for several), within the wire's save-name pattern. It is deterministic for a spec; when
    /// <paramref name="existing"/> (the save names in the saves folder, compared case-insensitively) holds that name it
    /// appends -2, -3, ... so a generate never overwrites a save.
    /// </summary>
    public static string DeriveSaveName(NewColonySpec spec, string seed, IEnumerable<string> existing)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(existing);
        var biomes = spec.Biomes ?? [];
        var biome = biomes.Count switch { 0 => "any", 1 => biomes[0], _ => "multi" };
        var baseName = SavePrefix + Slug(spec.Scenario, 16) + "-" + Slug(biome, 18) + "-" + Slug(seed, 12);
        var taken = new HashSet<string>(existing.Select(e => e.ToLowerInvariant()), StringComparer.Ordinal);
        var name = baseName;
        for (var n = 2; taken.Contains(name.ToLowerInvariant()); n++)
        {
            name = baseName + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return name;
    }

    private static string Slug(string text, int max)
    {
        var slug = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            if (char.IsAsciiLetterOrDigit(c) && slug.Length < max)
            {
                slug.Append(c);
            }
        }

        return slug.Length == 0 ? "x" : slug.ToString();
    }

    /// <summary>
    /// Refuses a spec the wire would refuse, before any process is stopped. Whether a defName exists is native's call.
    /// Throws <see cref="LauncherException"/>.
    /// </summary>
    public static void Validate(NewColonySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        WireSpec(spec.SaveName.Length == 0 ? SavePrefix + "x" : spec.SaveName, spec, "x");
    }

    /// <summary>Builds the wire spec from the form and runs the bridge's own validation over it.</summary>
    internal static WireLifecycle.NewColonySpec WireSpec(string saveName, NewColonySpec form, string seed)
    {
        if (form.ColonistCount < 0 || form.MapSize < 0)
        {
            throw new LauncherException("colonist count and map size must be positive");
        }

        var wire = ToWire(form with { SaveName = saveName }, seed);
        try
        {
            NewColonyValidation.ValidateNewColonyRequest(new WireLifecycle.NewColonyRequest
            {
                RequestId = "x", TimeoutMs = NewColonyBounds.MaxTimeoutMs, Spec = wire,
            });
        }
        catch (BridgeContractException e)
        {
            throw new LauncherException(ContractPrefix().Replace(e.Message, ""), e);
        }

        return wire;
    }

    [GeneratedRegex("^bridge contract failure: ", RegexOptions.CultureInvariant)]
    private static partial Regex ContractPrefix();

    private static WireLifecycle.NewColonySpec ToWire(NewColonySpec s, string seed)
    {
        try
        {
            var spec = new WireLifecycle.NewColonySpec
            {
                Scenario = s.Scenario, ColonistCount = (uint)s.ColonistCount, Seed = seed, FlatTile = s.FlatTile,
                Difficulty = s.Difficulty, Storyteller = s.Storyteller, MapSize = (uint)s.MapSize, PlanetCoverage = s.PlanetCoverage,
                SaveName = s.SaveName, MinTemperature = s.MinTemperature, MaxTemperature = s.MaxTemperature,
                Biomes = [.. s.Biomes ?? []],
                StartingThings = [.. (s.StartingThings ?? []).Select(t => new WireLifecycle.StartingThing
                {
                    Placement = EnumByName<WireLifecycle.StartingThingPlacement>(t.Placement, "STARTING_THING_PLACEMENT_", "placement"),
                    ThingDef = t.ThingDef, Stuff = t.Stuff, Count = t.Count, Quality = t.Quality, AllowRoofed = t.AllowRoofed,
                })],
                ForcedTraits = [.. (s.ForcedTraits ?? []).Select(t => new WireLifecycle.ForcedTrait
                {
                    Trait = t.Trait, Degree = t.Degree, Chance = t.Chance, Context = PawnContext(t.Context),
                })],
                ForcedHediffs = [.. (s.ForcedHediffs ?? []).Select(h => new WireLifecycle.ForcedHediff
                {
                    Hediff = h.Hediff, Severity = h.Severity, Chance = h.Chance, Context = PawnContext(h.Context),
                })],
                Naked = [.. (s.Naked ?? []).Select(n => new WireLifecycle.NakedPart { Chance = n.Chance, Context = PawnContext(n.Context) })],
                SetNeedLevels = [.. (s.SetNeedLevels ?? []).Select(n => new WireLifecycle.SetNeedLevel
                {
                    Need = n.Need, Level = n.Level, Chance = n.Chance, Context = PawnContext(n.Context),
                })],
                PermaGameConditions = [.. s.PermaGameConditions ?? []],
                DisallowedBuildings = [.. s.DisallowedBuildings ?? []],
                DisabledIncidents = [.. s.DisabledIncidents ?? []],
            };
            if (!string.IsNullOrEmpty(s.WorldTemperature))
            {
                spec = spec with { WorldTemperature = s.WorldTemperature };
            }

            if (!string.IsNullOrEmpty(s.PlayerFaction))
            {
                spec = spec with { PlayerFaction = EnumByName<WireLifecycle.ScenarioPlayerFaction>(s.PlayerFaction, "SCENARIO_PLAYER_FACTION_", "playerFaction") };
            }

            if (!string.IsNullOrEmpty(s.ArrivalMethod))
            {
                spec = spec with { ArrivalMethod = EnumByName<WireLifecycle.ScenarioArrivalMethod>(s.ArrivalMethod, "SCENARIO_ARRIVAL_METHOD_", "arrivalMethod") };
            }

            return spec;
        }
        catch (FormatException e)
        {
            throw new LauncherException("scenario part " + e.Message, e);
        }
    }

    private static WireLifecycle.ScenarioPawnContext PawnContext(string name) =>
        EnumByName<WireLifecycle.ScenarioPawnContext>(name, "SCENARIO_PAWN_CONTEXT_", "context");

    /// <summary>A lower-case wire name maps to the enum value of that prefixed upper-case name, never the zero value (Go <c>enumByName</c>).</summary>
    private static T EnumByName<T>(string name, string prefix, string part)
        where T : struct, Enum
    {
        if (!Wire.TryParse<T>(prefix + name.ToUpperInvariant(), out var value) || EqualityComparer<T>.Default.Equals(value, default))
        {
            throw new FormatException($"{part}: unknown value \"{name}\"");
        }

        return value;
    }
}
