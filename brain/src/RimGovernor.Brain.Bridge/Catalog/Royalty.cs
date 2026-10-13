using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_royalty.go, catalog_royalty_pawn.go and catalog_throne.go: the royalty static defs (the title ladder from
// every RoyalTitleDef and the permit catalog from every RoyalTitlePermitDef), the AbilityDef, ThingDef, BiomeDef and
// IncidentDef rows behind the royalty facts of a pawn or colony, and the throne room requirements.

public sealed partial class DefinitionCatalog
{
    private const string PsyfocusCostStat = "Ability_PsyfocusCost";
    private const string EntropyGainStat = "Ability_EntropyGain";

    /// <summary>
    /// <paramref name="facts"/> with its Ladder and Permits read from the defs: every RoyalTitleDef by ascending seniority
    /// (then name), each with its favor, bedroom and throne requirements, and every RoyalTitlePermitDef by name. A catalog
    /// holding no RoyalTitleDef is an error. <paramref name="facts"/> is not changed.
    /// </summary>
    public P.RoyaltyFacts WithTitleDefs(P.RoyaltyFacts facts)
    {
        var titles = Rows<RoyalTitleDef>().Values.OrderBy(t => t.seniority).ThenBy(t => t.defName, StringComparer.Ordinal).ToList();
        if (titles.Count == 0)
        {
            throw new CatalogException("royalty defs: catalog: no RoyalTitleDef rows");
        }

        var permits = new Dictionary<string, P.RoyalPermit>(StringComparer.Ordinal);
        foreach (var permit in Rows<RoyalTitlePermitDef>().Values.OrderBy(p => p.defName, StringComparer.Ordinal))
        {
            // The worker class is the type name without its namespace (the base RoyalTitlePermitWorker for a permit with no worker of its own).
            var entry = new P.RoyalPermit
            {
                Name = permit.defName,
                PermitPoints = Known(permit.permitPointCost),
                Acts = Known(permit.royalAid is not null),
                CooldownDays = Known((double)permit.cooldownDays),
                Worker = permit.workerClass?.Name ?? string.Empty,
            };
            if (permit.minTitle is { } min)
            {
                entry = entry with { MinTitle = Known(min.defName) };
            }

            if (permit.royalAid is { } aid)
            {
                entry = entry with { FavorCost = Known(aid.favorCost) };
            }

            permits[permit.defName] = entry;
        }

        return facts with { Ladder = Domain.EquatableArray.From(titles.Select(RoyalRung)), Permits = permits };
    }

    // Title's ladder row: seniority, favor needed (favorCost), the bedroom requirements and the throne requirement.
    private P.RoyalRung RoyalRung(RoyalTitleDef title)
    {
        var bedroom = BedroomRequirements(title, _ => false);
        var rung = new P.RoyalRung
        {
            Title = title.defName,
            Seniority = Known(title.seniority),
            FavorNeeded = Known(title.favorCost),
            BedroomThings = Domain.EquatableArray.From(bedroom.Things),
        };
        if (bedroom.HaveArea)
        {
            rung = rung with { BedroomMinArea = Known(bedroom.Area) };
        }

        if (bedroom.HaveImpressiveness)
        {
            rung = rung with { BedroomMinImpressiveness = Known(bedroom.Impressiveness) };
        }

        if (bedroom.Floored)
        {
            rung = rung with { BedroomFloored = Known(true) };
        }

        var throne = ThroneRequirements(title.defName);
        return rung with { Throne = Known(throne with { ForbiddenDefs = ForbiddenDefs(throne) }) };
    }

    /// <summary>A title's bedroom requirements: the largest area and impressiveness any requires (Have* when one does), whether a floor is required, and the furniture.</summary>
    private sealed record BedroomNeed(int Area, bool HaveArea, int Impressiveness, bool HaveImpressiveness, bool Floored, IReadOnlyList<P.BedroomThing> Things);

    // Title's bedroomRequirements, skipping each whose disablingPrecepts <paramref name="disabled"/> reports waived. A
    // requirement with no furniture is an error.
    private static BedroomNeed BedroomRequirements(RoyalTitleDef title, Func<IEnumerable<string>, bool> disabled)
    {
        int area = 0, impressiveness = 0;
        bool haveArea = false, haveImpressiveness = false, floored = false;
        var things = new List<P.BedroomThing>();

        void Thing(IEnumerable<ThingDef> anyOf, int count)
        {
            var defs = anyOf.Select(d => new P.Resource(d.defName)).ToList();
            if (defs.Count == 0 || count < 1)
            {
                throw new CatalogException($"royalty defs: title {title.defName}: invalid bedroom furniture requirement");
            }

            things.Add(new P.BedroomThing { AnyOf = Domain.EquatableArray.From(defs), Count = count });
        }

        IEnumerable<string> Disabling(RoomRequirement req) => req.disablingPrecepts.Seq().Select(p => p.defName);

        // The derived requirement classes are tested before the base they derive from.
        foreach (var req in title.bedroomRequirements.Seq())
        {
            if (req is null || disabled(Disabling(req)))
            {
                continue;
            }

            switch (req)
            {
                case RoomRequirement_Area a:
                    (haveArea, area) = (true, Math.Max(area, a.area));
                    break;
                case RoomRequirement_Impressiveness i:
                    (haveImpressiveness, impressiveness) = (true, Math.Max(impressiveness, i.impressiveness));
                    break;
                case RoomRequirement_TerrainWithTags:
                    floored = true;
                    break;
                case RoomRequirement_ThingAnyOfCount any:
                    Thing(any.things.Seq(), any.count);
                    break;
                case RoomRequirement_ThingAnyOf any:
                    Thing(any.things.Seq(), 1);
                    break;
                case RoomRequirement_ThingCount c:
                    Thing([c.thingDef], c.count);
                    break;
                case RoomRequirement_Thing t:
                    Thing([t.thingDef], 1);
                    break;
            }
        }

        return new BedroomNeed(area, haveArea, impressiveness, haveImpressiveness, floored, things);
    }

    /// <summary>
    /// The bedroom picture of a holder's most senior title: the RoyalTitleDef row's seniority and bedroom requirements,
    /// less those the holder waives (an ascetic has none; a requirement whose disablingPrecepts the holder's ideoligion
    /// holds is dropped). A title with no row is an error.
    /// </summary>
    public P.RoyalTitle RoyalTitleOf(string title, bool ascetic, IReadOnlyCollection<string> precepts)
    {
        var row = RequireRow<RoyalTitleDef>(title);
        var result = new P.RoyalTitle { Definition = title, Seniority = row.seniority };
        if (ascetic)
        {
            return result;
        }

        var need = BedroomRequirements(row, disabling => disabling.Any(precepts.Contains));
        return result with
        {
            BedroomMinArea = need.Area,
            BedroomMinImpressiveness = need.Impressiveness,
            BedroomFloored = need.Floored,
            BedroomThings = Domain.EquatableArray.From(need.Things),
        };
    }

    /// <summary>
    /// The ability row's static half of a P.Psycast: the psylink level that unlocks it, the psyfocus it spends (statBases
    /// Ability_PsyfocusCost), the neural heat it adds (Ability_EntropyGain), the longest cooldown (cooldownTicksRange.max)
    /// and what it targets (the verb's targetParams). A def with no row, or a row naming a value out of range, is an error.
    /// </summary>
    public P.Psycast Psycast(string name)
    {
        var row = RequireRow<AbilityDef>(name);
        var cost = AbilityStat(row, PsyfocusCostStat);
        var entropy = AbilityStat(row, EntropyGainStat);
        var cooldown = row.cooldownTicksRange.max;
        if (cost is < 0 or > 1 || entropy < 0 || cooldown < 0 || row.level < 0)
        {
            throw new CatalogException($"ability {name} has an invalid psycast row");
        }

        return new P.Psycast
        {
            Def = name,
            Level = Known(row.level),
            PsyfocusCost = Known(cost),
            Entropy = Known(entropy),
            Target = PsycastTarget(row.verbProperties?.targetParams),
            CooldownTicks = Known(cooldown),
        };
    }

    // The named stat of an AbilityDef's statBases, 0 when it lists none.
    private static double AbilityStat(AbilityDef row, string stat) =>
        row.statBases.Seq().FirstOrDefault(mod => mod.stat.defName == stat) is { } mod ? Decimal32(mod.value) : 0;

    // The kind of target the verb's targetParams allows: the caster alone, a pawn, a thing (building or item), a cell, or none.
    private static P.PsycastTarget PsycastTarget(TargetingParameters? t) => t switch
    {
        null => P.PsycastTarget.None,
        { canTargetSelf: true, canTargetPawns: false, canTargetLocations: false, canTargetBuildings: false, canTargetItems: false } => P.PsycastTarget.Self,
        { canTargetPawns: true } => P.PsycastTarget.Pawn,
        { canTargetBuildings: true } or { canTargetItems: true } => P.PsycastTarget.Thing,
        { canTargetLocations: true } => P.PsycastTarget.Cell,
        _ => P.PsycastTarget.None,
    };

    /// <summary>
    /// <paramref name="colony"/> with each neuroformer's trainer ability (the ability its
    /// <c>CompProperties_UseEffect_GainAbility</c> teaches) and tradeability (a trader can sell a Sellable or All thing)
    /// read from its ThingDef row. The colony is not changed; a neuroformer with no row is an error.
    /// </summary>
    public P.RoyaltyColony WithNeuroformerDefs(P.RoyaltyColony colony)
    {
        var neuroformers = new Dictionary<string, P.Neuroformer>(StringComparer.Ordinal);
        foreach (var (name, n) in colony.Neuroformers)
        {
            var row = ThingRow(name);
            neuroformers[name] = n with
            {
                TeachesPsycast = row.GetCompProperties<CompProperties_UseEffect_GainAbility>()?.ability?.defName ?? string.Empty,
                Tradeable = Known(row.tradeability is Tradeability.Sellable or Tradeability.All),
            };
        }

        return colony with { Neuroformers = neuroformers };
    }

    /// <summary>The hediffs of the named biome's disease incidents: each <c>BiomeDef.diseases</c> record with commonality, resolved through its IncidentDef's diseaseIncident, sorted and unique. A biome with no row is an error.</summary>
    public IReadOnlyList<string> BiomeDiseases(string biome) =>
        [.. GameMembers.BiomeDiseases(RequireRow<BiomeDef>(biome)).Seq()
            .Where(record => record.commonality > 0)
            .Select(record => RequireRow<IncidentDef>(record.diseaseInc.defName).diseaseIncident?.defName ?? string.Empty)
            .Where(hediff => hediff.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// The title's throne-room requirement from its RoyalTitleDef row. A title with no throneRoomRequirements asks for no
    /// throne (no Things). A missing row, a malformed requirement (no things, a count below one, a tag-less floor), a kind
    /// given twice that may appear once, a requirement of a kind the view does not read, or a throne without an area is an
    /// error.
    /// </summary>
    public P.ThroneRequirements ThroneRequirements(string title)
    {
        var row = DefRow<RoyalTitleDef>(title) ?? throw ThroneError(title, "the catalog has no RoyalTitleDef row");
        var result = new P.ThroneRequirements();
        bool haveArea = false, haveImpressiveness = false, haveThrone = false, haveFloor = false;
        var anyOfCounts = new List<P.ThingAnyOfCount>();
        var counts = new List<P.ThingCount>();
        var anyOf = new List<EquatableArray<string>>();
        var glowing = new List<EquatableArray<string>>();
        var forbiddenTags = new List<string>();

        List<string> Names(string kind, IEnumerable<ThingDef>? things)
        {
            var list = things.Seq().Select(d => d.defName).ToList();
            return list.Count > 0 ? list : throw ThroneError(title, $"{kind} names no def");
        }

        int Count(string kind, int n) => n >= 1 ? n : throw ThroneError(title, $"{kind} count is {n}");

        var index = 0;
        foreach (var req in row.throneRoomRequirements.Seq())
        {
            // The derived requirement classes are tested before the base they derive from.
            switch (req)
            {
                case RoomRequirement_HasAssignedThroneAnyOf assigned:
                    if (haveThrone)
                    {
                        throw ThroneError(title, "two assigned-throne requirements");
                    }

                    haveThrone = true;
                    result = result with { Assigned = true, Things = EquatableArray.From(Names("HasAssignedThroneAnyOf", assigned.things)) };
                    break;
                case RoomRequirement_Area area:
                    if (haveArea || area.area < 1)
                    {
                        throw ThroneError(title, "invalid or repeated Area requirement");
                    }

                    haveArea = true;
                    result = result with { MinArea = area.area };
                    break;
                case RoomRequirement_Impressiveness impressiveness:
                    if (haveImpressiveness || impressiveness.impressiveness < 0)
                    {
                        throw ThroneError(title, "invalid or repeated Impressiveness requirement");
                    }

                    haveImpressiveness = true;
                    result = result with { MinImpressiveness = impressiveness.impressiveness };
                    break;
                case RoomRequirement_TerrainWithTags floor:
                    if (haveFloor)
                    {
                        throw ThroneError(title, "two floor requirements");
                    }

                    if (floor.tags.Seq().Any() is false)
                    {
                        throw ThroneError(title, "TerrainWithTags names no def");
                    }

                    haveFloor = true;
                    result = result with { FloorTags = EquatableArray.From(floor.tags), FloorLabel = floor.labelKey };
                    break;
                case RoomRequirement_AllThingsAnyOfAreGlowing glowAny:
                    glowing.Add(EquatableArray.From(Names("AllThingsAnyOfAreGlowing", glowAny.things)));
                    break;
                case RoomRequirement_ThingAnyOfCount any:
                    anyOfCounts.Add(new P.ThingAnyOfCount { Things = EquatableArray.From(Names("ThingAnyOfCount", any.things)), Count = Count("ThingAnyOfCount", any.count) });
                    break;
                case RoomRequirement_ThingAnyOf any:
                    anyOf.Add(EquatableArray.From(Names("ThingAnyOf", any.things)));
                    break;
                case RoomRequirement_ThingCount c:
                    counts.Add(new P.ThingCount { Def = c.thingDef.defName, Count = Count("ThingCount", c.count) });
                    break;
                case RoomRequirement_Thing t:
                    counts.Add(new P.ThingCount { Def = t.thingDef.defName, Count = 1 });
                    break;
                case RoomRequirement_AllThingsAreGlowing glow:
                    glowing.Add(EquatableArray.From([glow.thingDef.defName]));
                    break;
                case RoomRequirement_ForbiddenBuildings forbidden:
                    foreach (var tag in forbidden.buildingTags.Seq().Concat(GameMembers.ForbiddenBuildingTagsSet(forbidden).Seq()))
                    {
                        if (!forbiddenTags.Contains(tag))
                        {
                            forbiddenTags.Add(tag);
                        }
                    }

                    if (forbiddenTags.Count == 0)
                    {
                        throw ThroneError(title, "ForbiddenBuildings names no def");
                    }

                    break;
                case RoomRequirement_ForbidAltars:
                    result = result with { ForbidAltars = true };
                    break;
                default:
                    throw ThroneError(title, $"requirement {index} is unset or of a kind the view does not read ({req?.GetType().Name})");
            }

            index++;
        }

        if (haveThrone && !haveArea)
        {
            throw ThroneError(title, "a throne without an Area requirement");
        }

        return result with { AnyOfCounts = EquatableArray.From(anyOfCounts), Counts = EquatableArray.From(counts), AnyOf = EquatableArray.From(anyOf), Glowing = EquatableArray.From(glowing), ForbiddenBuildingTags = EquatableArray.From(forbiddenTags) };
    }

    private static CatalogException ThroneError(string title, string message) => new($"throne requirements: title {title}: {message}");

    // The building defs req forbids: those whose building.buildingTags meet ForbiddenBuildingTags (the game's
    // RoomRequirement_ForbiddenBuildings test) and, when altars are forbidden, those with isAltar
    // (RoomRequirement_ForbidAltars), sorted.
    private EquatableArray<string> ForbiddenDefs(P.ThroneRequirements req) =>
        req.ForbiddenBuildingTags.Count == 0 && !req.ForbidAltars
            ? default
            : EquatableArray.From(_thingDefs.Values
                .Where(def => (req.ForbidAltars && IsAltar(def)) || def.building?.buildingTags.Seq().Any(tag => req.ForbiddenBuildingTags.Contains(tag)) == true)
                .Select(def => def.defName)
                .Order(StringComparer.Ordinal));
}
