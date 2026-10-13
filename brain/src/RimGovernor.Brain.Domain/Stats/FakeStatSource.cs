namespace RimGovernor.Brain.Domain;

/// <summary>
/// An <see cref="IStatSource"/> over recorded values, for tests and simulation. Values are keyed by
/// (stat, def, stuff); a request with nothing recorded throws <see cref="StatException"/> naming it.
/// Not thread-safe while being filled; fill it before sharing.
/// </summary>
public sealed class FakeStatSource : IStatSource
{
    private readonly Dictionary<(string Stat, string Def, string Stuff, bool Terrain), StatResult> _values = [];
    private readonly Dictionary<string, List<string>> _allowedStuffs = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Def, string Stuff, bool Terrain), List<StatCost>> _costs = [];

    /// <summary>Records the value of <paramref name="stat"/> for a def request (empty stuff is none).</summary>
    public FakeStatSource Set(string stat, string def, float value, string stuff = "", bool shown = true, bool terrain = false)
    {
        _values[(stat, def, stuff, terrain)] = new StatResult(value, shown);
        return this;
    }

    public FakeStatSource SetAllowedStuffs(string def, params string[] stuffs)
    {
        _allowedStuffs[def] = [.. stuffs];
        return this;
    }

    public FakeStatSource SetCosts(string def, string stuff, params StatCost[] costs)
    {
        _costs[(def, stuff, false)] = [.. costs];
        return this;
    }

    public FakeStatSource SetTerrainCosts(string terrain, params StatCost[] costs)
    {
        _costs[(terrain, string.Empty, true)] = [.. costs];
        return this;
    }

    public StatResult Evaluate(string stat, StatSubject subject)
    {
        if (_values.TryGetValue((stat, subject.Def, subject.Stuff, subject.Terrain), out var result))
        {
            return result;
        }

        throw new StatException($"no recorded value of stat {stat} for {subject.Def} with stuff \"{subject.Stuff}\"");
    }

    public float Value(string stat, StatSubject subject) => Evaluate(stat, subject).Value;

    public bool ShouldShowFor(string stat, StatSubject subject) => Evaluate(stat, subject).Shown;

    public float ValueWithoutPostProcess(string stat, StatSubject subject) => Value(stat, subject);

    public IReadOnlyList<string> AllowedStuffsFor(string def) =>
        _allowedStuffs.TryGetValue(def, out var stuffs) ? stuffs : [];

    public IReadOnlyList<StatCost> CostListAdjusted(string def, string stuff) =>
        _costs.TryGetValue((def, stuff, false), out var costs) ? costs : [];

    public IReadOnlyList<StatCost> TerrainCostListAdjusted(string terrain) =>
        _costs.TryGetValue((terrain, string.Empty, true), out var costs) ? costs : [];
}
