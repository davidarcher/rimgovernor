namespace RimGovernor.Brain.Domain;

/// <summary>
/// Def-level stat reads: the game's <c>StatWorker.GetValue</c> and <c>ShouldShowFor</c> for a
/// <c>ThingDef</c> or <c>TerrainDef</c> with optional stuff and quality, plus the cost and stuff
/// queries the catalog derivations need. This replaces Go's <c>internal/bridge/stateval</c>, which is
/// not ported: the production implementation is the game-thread stat service (#3080); tests and the
/// simulation use <see cref="FakeStatSource"/>.
/// </summary>
/// <remarks>
/// A def, stuff or stat the source does not know, and a stat class it cannot evaluate, throw
/// <see cref="StatException"/>; there is never a guessed default. A fact a thing request needs and
/// its <see cref="StatContext"/> does not state is an error naming it (Go's <c>need</c>).
/// </remarks>
public interface IStatSource
{
    /// <summary>The stat's final value and whether the game shows it for the subject (Go <c>Evaluate</c>).</summary>
    StatResult Evaluate(string stat, StatSubject subject);

    /// <summary><c>StatWorker.GetValue</c> (Go <c>Value</c>).</summary>
    float Value(string stat, StatSubject subject);

    /// <summary><c>StatWorker.ShouldShowFor</c>.</summary>
    bool ShouldShowFor(string stat, StatSubject subject);

    /// <summary><c>GetValue(req, applyPostProcess: false)</c>: no post-process curve, factors or min/max clamp.</summary>
    float ValueWithoutPostProcess(string stat, StatSubject subject);

    /// <summary><c>GenStuff.AllowedStuffsFor(def)</c>: the stuff def names the def can be made from, empty for a def not made from stuff.</summary>
    IReadOnlyList<string> AllowedStuffsFor(string def);

    /// <summary><c>ThingDef.CostListAdjusted(stuff)</c>; <paramref name="stuff"/> is empty for a def not made from stuff.</summary>
    IReadOnlyList<StatCost> CostListAdjusted(string def, string stuff);

    /// <summary><c>CostListAdjusted(null)</c> of a TerrainDef; empty is a free floor.</summary>
    IReadOnlyList<StatCost> TerrainCostListAdjusted(string terrain);
}

/// <summary>One stat evaluation: the final value and whether the game shows the stat for the subject.</summary>
public readonly record struct StatResult(float Value, bool Shown);

/// <summary>An adjusted cost entry: a ThingDef name and a unit count (Go <c>stateval.Cost</c>).</summary>
public readonly record struct StatCost(string Def, long Units);

/// <summary>The stat source could not answer: unknown def or stat, an unevaluable class, or an unstated fact.</summary>
public sealed class StatException(string message) : DomainException(message);

/// <summary>
/// A stat request subject (Go <c>stateval.Subject</c>): a ThingDef, or a TerrainDef when
/// <see cref="Terrain"/> is set, with optional stuff and quality category (0 awful to 6 legendary;
/// null is normal). A thing request carries its live state in <see cref="Context"/>; with a context the
/// quality is the context's, so <see cref="Quality"/> must be null.
/// </summary>
public sealed record StatSubject(string Def, bool Terrain = false, string Stuff = "", int? Quality = null, StatContext? Context = null)
{
    /// <summary>Go <c>ThingSubject</c>: the request for a ThingDef made of stuff (empty for none).</summary>
    public static StatSubject ForThing(string def, string stuff = "") => new(def, false, stuff);

    /// <summary>Go <c>TerrainSubject</c>.</summary>
    public static StatSubject ForTerrain(string terrain) => new(terrain, true);
}
