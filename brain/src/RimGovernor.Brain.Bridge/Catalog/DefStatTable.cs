using RimGovernor.Brain.Domain;
using Verse;

namespace RimGovernor.Brain.Bridge;

/// <summary>
/// The def-level stat, stuff and cost values of one load, keyed by the game's Def objects: every ThingDef with no
/// stuff and with each stuff the game allows it, and every TerrainDef, over the stats <see cref="StatNames"/>
/// declares. The mod computes it in one game-thread pass when the load's definition catalog is first read
/// (<c>NativeStatTable</c>); it is immutable afterwards, so the brain reads it on any thread without a game hop.
/// </summary>
/// <remarks>
/// A stat value is present when the game shows the stat for the subject, and for every subject when the stat is
/// <see cref="CatalogStat.ReadHidden"/>; a hidden stat of any other subject reads as not shown with no value. A
/// subject or stat outside the table, and a value the game could not compute, is a <see cref="StatException"/>
/// naming it; there is never a guessed default.
/// </remarks>
public sealed class DefStatTable
{
    /// <summary>One computed stat value, or why the game could not compute it.</summary>
    public readonly record struct Value(float Number, bool Shown, string? Failure);

    /// <summary>One subject's adjusted cost list, or why it has none (a def made from stuff asked with no stuff).</summary>
    public readonly record struct Cost(IReadOnlyList<StatCost>? Costs, string? Failure);

    private readonly IReadOnlyDictionary<(BuildableDef Def, ThingDef? Stuff, CatalogStat Stat), Value> _values;
    private readonly IReadOnlyDictionary<(BuildableDef Def, ThingDef? Stuff), Cost> _subjects;
    private readonly IReadOnlyDictionary<BuildableDef, IReadOnlyList<string>> _stuffs;
    private readonly IReadOnlySet<CatalogStat> _defined;

    /// <summary>
    /// A table over <paramref name="subjects"/> (each (def, stuff) the table covers, with its cost), the stat
    /// values computed for them, each def's allowed stuff names and the declared stats the game defines.
    /// </summary>
    public DefStatTable(
        IReadOnlyDictionary<(BuildableDef Def, ThingDef? Stuff), Cost> subjects,
        IReadOnlyDictionary<(BuildableDef Def, ThingDef? Stuff, CatalogStat Stat), Value> values,
        IReadOnlyDictionary<BuildableDef, IReadOnlyList<string>> stuffs,
        IReadOnlySet<CatalogStat> defined)
    {
        _subjects = subjects;
        _values = values;
        _stuffs = stuffs;
        _defined = defined;
    }

    /// <summary>A table with no subjects: a catalog that reads no stats (the creation-time ideoligion choice).</summary>
    public static DefStatTable Empty { get; } = new(
        new Dictionary<(BuildableDef, ThingDef?), Cost>(),
        new Dictionary<(BuildableDef, ThingDef?, CatalogStat), Value>(),
        new Dictionary<BuildableDef, IReadOnlyList<string>>(),
        new HashSet<CatalogStat>());

    /// <summary>The stat of def made of stuff (null for none): the game's value and whether it shows the stat.</summary>
    public StatResult Evaluate(CatalogStat stat, BuildableDef def, ThingDef? stuff)
    {
        if (!_defined.Contains(stat))
        {
            throw new StatException($"stat {stat.Name} is not defined in this game");
        }

        if (!_subjects.ContainsKey((def, stuff)))
        {
            throw new StatException($"no stat row for {def.defName} with stuff \"{stuff?.defName}\"");
        }

        if (!_values.TryGetValue((def, stuff, stat), out var value))
        {
            return new StatResult(float.NaN, false);
        }

        return value.Failure is { } failure ? throw new StatException(failure) : new StatResult(value.Number, value.Shown);
    }

    /// <summary>The game's <c>GenStuff.AllowedStuffsFor(def)</c> by name; empty for a def not made from stuff.</summary>
    public IReadOnlyList<string> AllowedStuffs(BuildableDef def) =>
        _stuffs.TryGetValue(def, out var stuffs) ? stuffs : throw new StatException($"no stuff row for {def.defName}");

    /// <summary>The game's <c>CostListAdjusted(stuff)</c> of def; an empty list is a def with no cost.</summary>
    public IReadOnlyList<StatCost> Costs(BuildableDef def, ThingDef? stuff)
    {
        if (!_subjects.TryGetValue((def, stuff), out var cost))
        {
            throw new StatException($"no cost row for {def.defName} with stuff \"{stuff?.defName}\"");
        }

        return cost.Failure is { } failure ? throw new StatException(failure) : cost.Costs!;
    }
}
