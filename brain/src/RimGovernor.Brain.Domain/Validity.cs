using System.Globalization;

namespace RimGovernor.Brain.Domain;

/// <summary>
/// The loaded world a read describes: the map, its load token and the native order generation. A read
/// from another scope describes another world; the tick a read was taken at never makes it stale: the
/// native side revalidates every action when it applies.
/// </summary>
public readonly record struct ReadScope(ColonyId Colony, MapId Map, LoadId Load, NativeGeneration Native)
{
    /// <summary>The read scope of a generation snapshot (Go <c>ScopeOf</c>).</summary>
    public static ReadScope Of(GenerationSnapshot s) => new(s.Colony, s.Map, s.Load, s.Native);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Colony}/{Map}/{Load}@{Native}");
}

/// <summary>
/// The world a decision context's facts belong to: the scope, the plan revision the step commits under,
/// the tick the step observed, and whether the view was invalidated whole.
/// </summary>
public readonly record struct ReadValidity(ReadScope Scope, PlanId Plan, PlanRevision Revision, Tick Tick, string Invalid = "")
{
    /// <summary>The validity bound to <paramref name="s"/> at <paramref name="tick"/> (Go <c>ValidityOf</c>).</summary>
    public static ReadValidity Of(GenerationSnapshot s, Tick tick) => new(ReadScope.Of(s), s.Plan, s.Revision, tick);

    /// <summary>Whether a step fixed a validity; the default value validates nothing.</summary>
    public bool IsKnown => Scope != default;

    /// <summary>Names why a read of scope <paramref name="have"/> no longer serves this validity, empty when it does.</summary>
    public string Stale(ReadScope have)
    {
        if (!IsKnown)
        {
            return "";
        }
        if (Invalid.Length != 0)
        {
            return "view invalidated: " + Invalid;
        }
        var want = Scope;
        if (have.Colony != want.Colony || have.Map != want.Map || have.Load != want.Load)
        {
            return $"scope {have.Colony}/{have.Map}/{have.Load}, step is {want.Colony}/{want.Map}/{want.Load}";
        }
        if (have.Native != want.Native)
        {
            return $"native generation {have.Native}, step is {want.Native}";
        }
        return "";
    }
}

/// <summary>
/// Carries a <see cref="ReadValidity"/> on the ambient async context (Go's <c>WithReadValidity(ctx, v)</c> and
/// <c>ReadValidityFrom(ctx)</c>). Prefer passing the validity explicitly; this exists for call chains that
/// threaded it through <c>context.Context</c>.
/// </summary>
public static class ReadValidityScope
{
    private static readonly AsyncLocal<ReadValidity> Current = new();

    /// <summary>Runs until the returned scope is disposed, with <paramref name="v"/> as the current validity.</summary>
    public static IDisposable With(ReadValidity v)
    {
        var previous = Current.Value;
        Current.Value = v;
        return new Restore(previous);
    }

    /// <summary>The validity the current context carries; false when none.</summary>
    public static bool TryGet(out ReadValidity v)
    {
        v = Current.Value;
        return v.IsKnown;
    }

    private sealed class Restore(ReadValidity previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
