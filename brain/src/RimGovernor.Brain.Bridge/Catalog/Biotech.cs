using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go biotech.go and biotech_catalog.go (#3056 left them out; #3111): one load's Biotech facts as a facade over the Def
// rows. The gene, race and mech kind defs are the catalog's own rows; the three mech work modes the game picks by
// role are the MechWorkModeDefOf members, whose defName is the member's name. The GeneTuning singleton Go carried is
// the game's own static GeneTuning class and needs no copy.

public sealed partial class DefinitionCatalog
{
    private readonly Lazy<BiotechCatalog?> _biotech;

    /// <summary>The load's Biotech facts; null without Biotech (the game has no gene defs).</summary>
    public BiotechCatalog? Biotech => _biotech.Value;

    private BiotechCatalog? BuildBiotech()
    {
        var genes = Rows<GeneDef>();
        if (genes.Count == 0)
        {
            return null;
        }

        var races = new Dictionary<string, RaceProperties>(StringComparer.Ordinal);
        foreach (var (name, def) in _thingDefs)
        {
            if (def.race is { intelligence: >= Intelligence.Humanlike, lifeStageAges: { Count: > 0 } } race)
            {
                races[name] = race;
            }
        }

        var kinds = new Dictionary<string, MechKindRow>(StringComparer.Ordinal);
        foreach (var kind in Rows<PawnKindDef>().Values)
        {
            if (MechKindOf(kind) is { } row)
            {
                kinds[kind.defName] = row;
            }
        }

        var work = RequireWorkMode(nameof(MechWorkModeDefOf.Work));
        var escort = RequireWorkMode(nameof(MechWorkModeDefOf.Escort));
        var recharge = RequireWorkMode(nameof(MechWorkModeDefOf.Recharge));
        return new BiotechCatalog(genes, races, kinds, work, escort, recharge);
    }

    private string RequireWorkMode(string role) =>
        DefRow<MechWorkModeDef>(role)?.defName
        ?? throw new CatalogException($"biotech mech work mode role {role} is not a def of the catalog");

    // A PawnKindDef as a controllable mech: its race is a mechanoid with an overseer-subject comp; the bandwidth cost
    // is the race's stat.
    private MechKindRow? MechKindOf(PawnKindDef kind)
    {
        if (kind.race is not { } race || !RaceFlags(race.defName).Mechanoid || !HasComp<CompProperties_OverseerSubject>(race))
        {
            return null;
        }

        var (cost, _) = ShownStatValue(race.defName, string.Empty, "BandwidthCost");
        var workTypes = (race.race?.mechEnabledWorkTypes ?? []).Select(w => w.defName).Order(StringComparer.Ordinal).ToList();
        return new MechKindRow(race.defName, workTypes.Count > 0, cost, kind.combatPower, workTypes);
    }
}

/// <summary>A controllable mech kind as the planner reads it (Go <c>mechKindRow</c>).</summary>
internal sealed record MechKindRow(string Race, bool WorkMech, double BandwidthCost, double CombatPower, IReadOnlyList<string> WorkTypes);

/// <summary>One load's Biotech facts (Go <c>BiotechCatalog</c>); <see cref="DefinitionCatalog.Biotech"/> is null without Biotech.</summary>
public sealed class BiotechCatalog
{
    private readonly IReadOnlyDictionary<string, RaceProperties> _races;
    private readonly IReadOnlyDictionary<string, MechKindRow> _mechKinds;
    private readonly string _work;
    private readonly string _escort;
    private readonly string _recharge;

    internal BiotechCatalog(
        IReadOnlyDictionary<string, GeneDef> genes,
        IReadOnlyDictionary<string, RaceProperties> races,
        IReadOnlyDictionary<string, MechKindRow> mechKinds,
        string work,
        string escort,
        string recharge)
    {
        Genes = genes;
        _races = races;
        _mechKinds = mechKinds;
        _work = work;
        _escort = escort;
        _recharge = recharge;
    }

    /// <summary>The GeneDef rows by name.</summary>
    public IReadOnlyDictionary<string, GeneDef> Genes { get; }

    /// <summary>The catalog's mech kinds and the work modes the game names by role, as the mech planner reads them.</summary>
    public P.MechCatalog MechCatalog()
    {
        var kinds = new Dictionary<string, P.MechKind>(StringComparer.Ordinal);
        foreach (var (name, row) in _mechKinds)
        {
            kinds[name] = new P.MechKind
            {
                Name = name,
                WorkMech = row.WorkMech,
                BandwidthCost = row.BandwidthCost,
                CombatPower = row.CombatPower,
                WorkTypes = [.. row.WorkTypes.Select(w => Wire.TryParse<P.WorkType>(w, out var type) ? type : P.WorkType.None).Where(t => t != P.WorkType.None)],
            };
        }

        return new P.MechCatalog { Kinds = kinds, Work = _work, Escort = _escort, Recharge = _recharge };
    }

    /// <summary>
    /// Resolves the active genes of a pawn into their combined typed effects from the catalog's gene rows. A gene the
    /// catalog does not define is a contract failure, never skipped.
    /// </summary>
    public P.GeneEffects GeneEffects(IEnumerable<P.PawnGene> genes)
    {
        var stats = new Dictionary<string, P.StatModifier>(StringComparer.Ordinal);
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        var enabled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gene in genes)
        {
            if (gene.Active.TryGetValue(out var active) && !active)
            {
                continue;
            }

            if (!Genes.TryGetValue(gene.Name, out var row))
            {
                throw new CatalogException($"pawn gene {gene.Name} is not in the biotech catalog");
            }

            foreach (var factor in row.statFactors ?? [])
            {
                var m = stats.GetValueOrDefault(factor.stat.defName, new P.StatModifier(0, 1));
                stats[factor.stat.defName] = m with { Factor = m.Factor * factor.value };
            }

            foreach (var offset in row.statOffsets ?? [])
            {
                var m = stats.GetValueOrDefault(offset.stat.defName, new P.StatModifier(0, 1));
                stats[offset.stat.defName] = m with { Offset = m.Offset + offset.value };
            }

            foreach (var need in row.disablesNeeds ?? [])
            {
                disabled.Add(need.defName);
            }

            foreach (var need in row.enablesNeeds ?? [])
            {
                enabled.Add(need.defName);
            }
        }

        return new P.GeneEffects { Stats = stats, DisabledNeeds = disabled, EnabledNeeds = enabled };
    }

    /// <summary>
    /// The race's minimum age in years per work type from the catalog. A race the catalog lacks is a contract failure:
    /// a child must never be left unrestricted for want of data.
    /// </summary>
    public IReadOnlyDictionary<P.WorkType, int> WorkMinAges(string race)
    {
        if (!_races.TryGetValue(race, out var row))
        {
            throw new CatalogException($"child race {race} is not in the biotech catalog");
        }

        var result = new Dictionary<P.WorkType, int>();
        foreach (var setting in row.lifeStageWorkSettings ?? [])
        {
            if (Wire.TryParse<P.WorkType>(setting.workType.defName, out var type))
            {
                result[type] = setting.minAge;
            }
        }

        return result;
    }
}
