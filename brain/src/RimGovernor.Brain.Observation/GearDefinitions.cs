namespace RimGovernor.Brain.Observation;

// Apparel facts use catalog rows for layers, body groups, wear filters, outfit tags, equipped offsets and
// components. Armor, insulation and market value use native stat values for the exact def/stuff pair.

/// <summary>What a gear census is decoded against: the load's catalog and its finished research projects (unknown without a research read).</summary>
public sealed record GearDefinitions
{
    public DefinitionCatalog? Catalog { get; init; }

    public Fact<IReadOnlySet<string>> Finished { get; init; }
}

/// <summary>A pawn's wear inputs: what the apparel rows cannot say.</summary>
internal sealed record GearWearer(D.Gender Gender, D.DevelopmentalStage Stage, IReadOnlySet<string> Groups)
{
    /// <summary>
    /// The game's wear filter for apparel properties: the gender (ApparelProperties.CorrectGenderForWearing), the
    /// developmental stage filter (PawnCanWear) and a present part in one of the garment's body part groups
    /// (ApparelUtility.HasPartsToWear).
    /// </summary>
    public bool CanWear(RimWorld.ApparelProperties a)
    {
        // The wire and game gender and stage enums carry the same values (none/male/female; newborn/baby/child/adult flags).
        if ((int)a.gender != (int)D.Gender.None && Gender != D.Gender.None && (int)a.gender != (int)Gender)
        {
            return false;
        }
        if (((int)a.developmentalStageFilter & (int)Stage) == 0)
        {
            return false;
        }
        foreach (var g in a.bodyPartGroups ?? [])
        {
            if (Groups.Contains(g.defName))
            {
                return true;
            }
        }
        return false;
    }
}

public static partial class ObservationPackage
{
    // The game's stats a loadout option takes from the stat table.
    internal const string StatArmorSharp = "ArmorRating_Sharp";
    internal const string StatArmorBlunt = "ArmorRating_Blunt";
    internal const string StatInsulationCold = "Insulation_Cold";
    internal const string StatInsulationHeat = "Insulation_Heat";
    internal const string StatMoveSpeed = "MoveSpeed";
    internal const string StatPsychic = "PsychicSensitivity";

    /// <summary>
    /// Reads the gear definitions from <paramref name="source"/>: its catalog and, when it is a research source,
    /// the finished projects. A source with no catalog yields none, which leaves every gear fact unknown.
    /// </summary>
    public static async Task<GearDefinitions> ReadGearDefinitions(object source, C.Identity id, CancellationToken cancellationToken)
    {
        var facts = await ReadDefinitionFacts(source, id, null, cancellationToken).ConfigureAwait(false);
        return new GearDefinitions { Catalog = facts?.Catalog, Finished = facts?.Finished ?? default };
    }

    /// <summary>The wear inputs of one gear row; null when the producer sent none.</summary>
    internal static GearWearer? WearerOf(O.GearLoadout p)
    {
        if (p.Gender is null || p.DevelopmentalStage is null)
        {
            return null;
        }
        return new GearWearer(p.Gender.GetValueOrDefault(), p.DevelopmentalStage.GetValueOrDefault(), new HashSet<string>(p.BodyPartGroups, StringComparer.Ordinal));
    }

    /// <summary>
    /// Every apparel def the wearer can wear, by name, with the role facts of the apparel policy: armor is a def
    /// only the game's Soldier outfit tag names (not Worker too), covers-body one covering the torso or legs.
    /// </summary>
    internal static EquatableArray<ApparelDefinition> ApparelDefinitions(DefinitionCatalog catalog, GearWearer w)
    {
        var output = new List<ApparelDefinition>();
        foreach (var (name, row) in catalog.ThingDefs)
        {
            var a = row.apparel;
            if (a is null || !w.CanWear(a))
            {
                continue;
            }
            var stage = (int)a.developmentalStageFilter;
            var groups = (a.bodyPartGroups ?? []).Select(group => group.defName).ToList();
            output.Add(new ApparelDefinition
            {
                Name = name,
                Armor = DefinitionCatalog.ApparelIsArmor(a),
                Child = (stage & (int)D.DevelopmentalStage.Child) != 0,
                Adult = (stage & (int)D.DevelopmentalStage.Adult) != 0,
                CoversBody = groups.Contains("Torso") || groups.Contains("Legs"),
            });
        }
        output.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return EquatableArray.From(output);
    }

    /// <summary>The def's apparel properties; the apparel is null when the catalog has no such row or it is not apparel.</summary>
    internal static (Verse.ThingDef? Row, RimWorld.ApparelProperties? Apparel) ApparelRow(DefinitionCatalog catalog, string def)
    {
        var row = catalog.FindThing(def);
        return (row, row?.apparel);
    }

    /// <summary>Values without repeats, in order.</summary>
    internal static EquatableArray<string> Distinct(IEnumerable<string> values)
    {
        var output = new List<string>();
        foreach (var v in values)
        {
            if (!output.Contains(v))
            {
                output.Add(v);
            }
        }
        return EquatableArray.From(output);
    }

    /// <summary>The sum of a def's equipped stat offsets for <paramref name="stat"/>.</summary>
    internal static float StatOffset(Verse.ThingDef? row, string stat)
    {
        float sum = 0;
        if (row is null)
        {
            return sum;
        }
        foreach (var m in row.equippedStatOffsets ?? [])
        {
            if (m.stat.defName == stat)
            {
                sum += m.value;
            }
        }
        return sum;
    }

    /// <summary>Reports a def with the shield comp.</summary>
    internal static bool HasShield(Verse.ThingDef? row) =>
        row is not null && DefinitionCatalog.HasComp<RimWorld.CompProperties_Shield>(row);

    /// <summary>
    /// One stat of the option's (def, stuff) at Normal quality before condition, never negative. A stat the game
    /// hides for the def is an error unless <paramref name="tolerateHidden"/>: armor and insulation are shown for
    /// every apparel (their StatDefs do not set showIfUndefined false), so a hidden one is a catalog gap, never a
    /// garment without it. Market value is the exception: the game hides it for gear nobody can trade (mech
    /// apparel), which is zero cost to the planner, not an unknown.
    /// </summary>
    internal static double OptionStat(DefinitionCatalog catalog, string def, string stuff, string stat, bool tolerateHidden)
    {
        var (value, shown) = catalog.ShownStatValue(def, stuff, stat);
        if (!shown)
        {
            if (tolerateHidden)
            {
                return 0;
            }
            throw new DomainException($"stat {stat} is not shown for apparel {def} with stuff \"{stuff}\"");
        }
        return Math.Max(0, (double)value);
    }
}
