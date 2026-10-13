namespace RimGovernor.Brain.Host;

/// <summary>
/// The routine planner families a composition wires (Go <c>serveConfig.rounds*Plans</c> and
/// <c>routinefamily</c>). Every family is on by default, exactly as an autonomous Go serve composed them;
/// <see cref="FromEnvironment"/> narrows them the way <c>RIMGOVERNOR_ROUTINE_FAMILIES</c> did.
/// </summary>
public sealed record BrainFamilies
{
    /// <summary>The environment variable that narrows the families (Go <c>roundsFamiliesEnv</c>).</summary>
    public const string EnvironmentVariable = "RIMGOVERNOR_ROUTINE_FAMILIES";

    /// <summary>The <c>sleeping</c> family.</summary>
    public bool Sleeping { get; init; } = true;

    /// <summary>The <c>bill</c> family.</summary>
    public bool Bill { get; init; } = true;

    /// <summary>The <c>field</c> family.</summary>
    public bool Field { get; init; } = true;

    /// <summary>The <c>acquisition</c> family.</summary>
    public bool Acquisition { get; init; } = true;

    /// <summary>The <c>work</c> family.</summary>
    public bool Work { get; init; } = true;

    /// <summary>The <c>supply</c> family.</summary>
    public bool Supply { get; init; } = true;

    /// <summary>The <c>cooking</c> family.</summary>
    public bool Cooking { get; init; } = true;

    /// <summary>The <c>shelter</c> family.</summary>
    public bool Shelter { get; init; } = true;

    /// <summary>The <c>comfort</c> family.</summary>
    public bool Comfort { get; init; } = true;

    /// <summary>The <c>workshop</c> family.</summary>
    public bool Workshop { get; init; } = true;

    /// <summary>The <c>research</c> family.</summary>
    public bool Research { get; init; } = true;

    /// <summary>The <c>hospital</c> family.</summary>
    public bool Hospital { get; init; } = true;

    /// <summary>The <c>expansion</c> family.</summary>
    public bool Expansion { get; init; } = true;

    /// <summary>The <c>temperature</c> family.</summary>
    public bool Temperature { get; init; } = true;

    /// <summary>The <c>power</c> family.</summary>
    public bool Power { get; init; } = true;

    /// <summary>The <c>defense</c> family.</summary>
    public bool Defense { get; init; } = true;

    /// <summary>The <c>tend</c> family.</summary>
    public bool Tend { get; init; } = true;

    /// <summary>The <c>rescue</c> family.</summary>
    public bool Rescue { get; init; } = true;

    /// <summary>The <c>equip</c> family.</summary>
    public bool Equip { get; init; } = true;

    /// <summary>The <c>repair</c> family.</summary>
    public bool Repair { get; init; } = true;

    /// <summary>The <c>fire</c> family.</summary>
    public bool FireSafety { get; init; } = true;

    /// <summary>The <c>burial</c> family.</summary>
    public bool Burial { get; init; } = true;

    /// <summary>The <c>training</c> family.</summary>
    public bool Training { get; init; } = true;

    /// <summary>The <c>envelope</c> family.</summary>
    public bool Envelope { get; init; } = true;

    /// <summary>The <c>incineration</c> family.</summary>
    public bool Incineration { get; init; } = true;

    /// <summary>The <c>blight</c> family.</summary>
    public bool Blight { get; init; } = true;

    /// <summary>The <c>pollution</c> family.</summary>
    public bool Pollution { get; init; } = true;

    /// <summary>The <c>mechcharger</c> family.</summary>
    public bool MechCharger { get; init; } = true;

    /// <summary>The <c>genebank</c> family.</summary>
    public bool GeneBank { get; init; } = true;

    /// <summary>The <c>armory</c> family.</summary>
    public bool Armory { get; init; } = true;

    /// <summary>The <c>clearance</c> family.</summary>
    public bool Clearance { get; init; } = true;

    /// <summary>The <c>shrine</c> family.</summary>
    public bool Shrine { get; init; } = true;

    /// <summary>The <c>stockpiles</c> family.</summary>
    public bool Stockpiles { get; init; } = true;

    /// <summary>The <c>mood</c> family.</summary>
    public bool Mood { get; init; } = true;

    /// <summary>The <c>gear</c> family.</summary>
    public bool Gear { get; init; } = true;

    /// <summary>The <c>medical</c> family.</summary>
    public bool Medical { get; init; } = true;

    /// <summary>The <c>food-storage-upkeep</c> family.</summary>
    public bool FoodStorageUpkeep { get; init; } = true;

    /// <summary>The <c>refrigeration</c> family.</summary>
    public bool Refrigeration { get; init; } = true;

    /// <summary>The <c>lighting</c> family.</summary>
    public bool Lighting { get; init; } = true;

    /// <summary>The <c>art</c> family.</summary>
    public bool Art { get; init; } = true;

    /// <summary>The <c>mechs</c> family.</summary>
    public bool Mechs { get; init; } = true;

    /// <summary>The <c>flooring</c> family.</summary>
    public bool Flooring { get; init; } = true;

    /// <summary>The <c>routes</c> family.</summary>
    public bool Routes { get; init; } = true;

    /// <summary>The <c>animal-containment</c> family.</summary>
    public bool AnimalContainment { get; init; } = true;

    /// <summary>The <c>recovery</c> family.</summary>
    public bool Recovery { get; init; } = true;

    /// <summary>The <c>husbandry</c> family.</summary>
    public bool Husbandry { get; init; } = true;

    /// <summary>The <c>rules</c> family.</summary>
    public bool Rules { get; init; } = true;

    /// <summary>The <c>prisoner-interaction</c> family.</summary>
    public bool PrisonerInteraction { get; init; } = true;

    /// <summary>The <c>population-custody</c> family.</summary>
    public bool PopulationCustody { get; init; } = true;

    /// <summary>The <c>population-joiner</c> family.</summary>
    public bool PopulationJoiner { get; init; } = true;

    /// <summary>The <c>home-coverage</c> family.</summary>
    public bool HomeCoverage { get; init; } = true;

    /// <summary>The <c>sheltering</c> family.</summary>
    public bool Sheltering { get; init; } = true;

    /// <summary>The <c>firebreak</c> family.</summary>
    public bool Firebreak { get; init; } = true;

    /// <summary>The <c>psylink</c> family.</summary>
    public bool Psylink { get; init; } = true;

    /// <summary>The <c>creepjoiner</c> family.</summary>
    public bool CreepJoiner { get; init; } = true;

    /// <summary>The <c>permits</c> family.</summary>
    public bool Permits { get; init; } = true;

    /// <summary>The <c>ideo-roles</c> family.</summary>
    public bool IdeoRoles { get; init; } = true;

    /// <summary>The <c>ideoligion-reform</c> family.</summary>
    public bool Ideoligion { get; init; } = true;

    /// <summary>The <c>rituals</c> family.</summary>
    public bool Rituals { get; init; } = true;

    /// <summary>The <c>gathering</c> family.</summary>
    public bool Gathering { get; init; } = true;

    /// <summary>The <c>stone-shell</c> family.</summary>
    public bool StoneShell { get; init; } = true;

    /// <summary>The <c>defensive-layout</c> family.</summary>
    public bool DefensiveLayout { get; init; } = true;

    /// <summary>The <c>dialog</c> family.</summary>
    public bool Dialog { get; init; } = true;

    /// <summary>The <c>trade</c> family.</summary>
    public bool Trade { get; init; } = true;

    /// <summary>The <c>resource</c> family.</summary>
    public bool Resource { get; init; } = true;

    private static readonly Entry[] Entries =
    [
        new("sleeping", f => f.Sleeping, (f, on) => f with { Sleeping = on }),
        new("bill", f => f.Bill, (f, on) => f with { Bill = on }),
        new("field", f => f.Field, (f, on) => f with { Field = on }),
        new("acquisition", f => f.Acquisition, (f, on) => f with { Acquisition = on }),
        new("work", f => f.Work, (f, on) => f with { Work = on }),
        new("supply", f => f.Supply, (f, on) => f with { Supply = on }),
        new("cooking", f => f.Cooking, (f, on) => f with { Cooking = on }),
        new("shelter", f => f.Shelter, (f, on) => f with { Shelter = on }),
        new("comfort", f => f.Comfort, (f, on) => f with { Comfort = on }),
        new("workshop", f => f.Workshop, (f, on) => f with { Workshop = on }),
        new("research", f => f.Research, (f, on) => f with { Research = on }),
        new("hospital", f => f.Hospital, (f, on) => f with { Hospital = on }),
        new("expansion", f => f.Expansion, (f, on) => f with { Expansion = on }),
        new("temperature", f => f.Temperature, (f, on) => f with { Temperature = on }),
        new("power", f => f.Power, (f, on) => f with { Power = on }),
        new("defense", f => f.Defense, (f, on) => f with { Defense = on }),
        new("tend", f => f.Tend, (f, on) => f with { Tend = on }),
        new("rescue", f => f.Rescue, (f, on) => f with { Rescue = on }),
        new("equip", f => f.Equip, (f, on) => f with { Equip = on }),
        new("repair", f => f.Repair, (f, on) => f with { Repair = on }),
        new("fire", f => f.FireSafety, (f, on) => f with { FireSafety = on }),
        new("burial", f => f.Burial, (f, on) => f with { Burial = on }),
        new("training", f => f.Training, (f, on) => f with { Training = on }),
        new("envelope", f => f.Envelope, (f, on) => f with { Envelope = on }),
        new("incineration", f => f.Incineration, (f, on) => f with { Incineration = on }),
        new("blight", f => f.Blight, (f, on) => f with { Blight = on }),
        new("pollution", f => f.Pollution, (f, on) => f with { Pollution = on }),
        new("mechcharger", f => f.MechCharger, (f, on) => f with { MechCharger = on }),
        new("genebank", f => f.GeneBank, (f, on) => f with { GeneBank = on }),
        new("armory", f => f.Armory, (f, on) => f with { Armory = on }),
        new("clearance", f => f.Clearance, (f, on) => f with { Clearance = on }),
        new("shrine", f => f.Shrine, (f, on) => f with { Shrine = on }),
        new("stockpiles", f => f.Stockpiles, (f, on) => f with { Stockpiles = on }),
        new("mood", f => f.Mood, (f, on) => f with { Mood = on }),
        new("gear", f => f.Gear, (f, on) => f with { Gear = on }),
        new("medical", f => f.Medical, (f, on) => f with { Medical = on }),
        new("food-storage-upkeep", f => f.FoodStorageUpkeep, (f, on) => f with { FoodStorageUpkeep = on }),
        new("refrigeration", f => f.Refrigeration, (f, on) => f with { Refrigeration = on }),
        new("lighting", f => f.Lighting, (f, on) => f with { Lighting = on }),
        new("art", f => f.Art, (f, on) => f with { Art = on }),
        new("mechs", f => f.Mechs, (f, on) => f with { Mechs = on }),
        new("flooring", f => f.Flooring, (f, on) => f with { Flooring = on }),
        new("routes", f => f.Routes, (f, on) => f with { Routes = on }),
        new("animal-containment", f => f.AnimalContainment, (f, on) => f with { AnimalContainment = on }),
        new("recovery", f => f.Recovery, (f, on) => f with { Recovery = on }),
        new("husbandry", f => f.Husbandry, (f, on) => f with { Husbandry = on }),
        new("rules", f => f.Rules, (f, on) => f with { Rules = on }),
        new("prisoner-interaction", f => f.PrisonerInteraction, (f, on) => f with { PrisonerInteraction = on }),
        new("population-custody", f => f.PopulationCustody, (f, on) => f with { PopulationCustody = on }),
        new("population-joiner", f => f.PopulationJoiner, (f, on) => f with { PopulationJoiner = on }),
        new("home-coverage", f => f.HomeCoverage, (f, on) => f with { HomeCoverage = on }),
        new("sheltering", f => f.Sheltering, (f, on) => f with { Sheltering = on }),
        new("firebreak", f => f.Firebreak, (f, on) => f with { Firebreak = on }),
        new("psylink", f => f.Psylink, (f, on) => f with { Psylink = on }),
        new("creepjoiner", f => f.CreepJoiner, (f, on) => f with { CreepJoiner = on }),
        new("permits", f => f.Permits, (f, on) => f with { Permits = on }),
        new("ideo-roles", f => f.IdeoRoles, (f, on) => f with { IdeoRoles = on }),
        new("ideoligion-reform", f => f.Ideoligion, (f, on) => f with { Ideoligion = on }),
        new("rituals", f => f.Rituals, (f, on) => f with { Rituals = on }),
        new("gathering", f => f.Gathering, (f, on) => f with { Gathering = on }),
        new("stone-shell", f => f.StoneShell, (f, on) => f with { StoneShell = on }),
        new("defensive-layout", f => f.DefensiveLayout, (f, on) => f with { DefensiveLayout = on }),
        new("dialog", f => f.Dialog, (f, on) => f with { Dialog = on }),
        new("trade", f => f.Trade, (f, on) => f with { Trade = on }),
        new("resource", f => f.Resource, (f, on) => f with { Resource = on }),
    ];

    /// <summary>Every family on (the autonomous default).</summary>
    public static BrainFamilies All { get; } = new();

    /// <summary>
    /// The families <paramref name="selection"/> names, a comma-separated list of the family names; every family when
    /// it is null, empty or blank (Go <c>selectRoundsFamilies</c>). A name no family has throws
    /// <see cref="FormatException"/>.
    /// </summary>
    public static BrainFamilies FromEnvironment(string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection))
        {
            return All;
        }
        var byName = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var result = All;
        foreach (var entry in Entries)
        {
            byName[entry.Name] = entry;
            result = entry.With(result, false);
        }
        foreach (var raw in selection.Split(','))
        {
            var name = raw.Trim();
            if (!byName.TryGetValue(name, out var entry))
            {
                throw new FormatException($"{EnvironmentVariable}: unknown routine family \"{name}\"");
            }
            result = entry.With(result, true);
        }
        return result;
    }

    /// <summary>The name of every enabled family, in composition order (Go <c>activeRoundsFamilies</c>).</summary>
    public IReadOnlyList<string> Active() => [.. Entries.Where(e => e.Get(this)).Select(e => e.Name)];

    /// <summary>
    /// Whether the composition plans every Foothold exit criterion (shelter, cooking, food storage and basic
    /// defense), so the measured colony stage can climb (Go <c>footholdComposed</c>).
    /// </summary>
    public bool FootholdComposed =>
        (Sleeping || Shelter) && (Bill || Cooking) && Stockpiles && (Defense || Equip);

    /// <summary>Whether EnsureResearch is composed (Go <c>researchPlans</c>).</summary>
    public bool ResearchPlans => Research;

    /// <summary>Whether MaintainResource runs (Go <c>resourceTargetsConfigured</c>).</summary>
    public bool ResourceTargetsConfigured => Resource;

    /// <summary>Whether the workshop planner is composed: it needs a resource target or the gear family (Go <c>workshopPlans</c>).</summary>
    public bool WorkshopPlans => Workshop && (ResourceTargetsConfigured || Gear);

    private sealed record Entry(string Name, Func<BrainFamilies, bool> Get, Func<BrainFamilies, bool, BrainFamilies> With);
}
