namespace RimGovernor.Brain.Bridge;

internal static class ListExtensions
{
    /// <summary>The list, or an empty one when the game left the field null (the game's own <c>NullOrEmpty</c> convention): <c>def.comps.Seq()</c>.</summary>
    internal static IEnumerable<T> Seq<T>(this List<T>? list) => list ?? Enumerable.Empty<T>();

    /// <summary>The sequence, or an empty one when it is null.</summary>
    internal static IEnumerable<T> Seq<T>(this IEnumerable<T>? items) => items ?? Enumerable.Empty<T>();
}
