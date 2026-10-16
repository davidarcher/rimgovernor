using System;

namespace RimGovernor.Host;

/// <summary>
/// Which <c>DeepProfiler</c> labels the launch waterfall skips (#3308). RimWorld also opens a profiler stage per def
/// and per patch, at the same depth as its coarse load stages, thousands per boot:
/// <list type="bullet">
/// <item><c>DirectXmlCrossRefLoader</c>'s <c>Register*WantsCrossRef</c>, from <c>ParseAndProcessXML()</c>;</item>
/// <item><c>PatchOperation</c>'s <c>"&lt;type&gt; Worker"</c>, from <c>ApplyPatches()</c>;</item>
/// <item><c>LongEventHandler</c>'s <c>"&lt;type&gt; -&gt; &lt;method&gt;"</c> for each queued action, of which the per-def
/// ones are lambdas a def's load hook queued (<c>&lt;PostLoad&gt;b__</c>, <c>&lt;ResolveReferences&gt;b__</c>,
/// <c>&lt;LoadDataFromXmlCustom&gt;b__</c> and their <c>Special</c> forms).</item>
/// </list>
/// Those are matched by pattern rather than the coarse stages by an allow-list, so a stage a game update adds still
/// shows up. Pure, so it is unit-tested without the game.
/// </summary>
internal static class LaunchStageFilter
{
    private static readonly string[] PerDefHooks = ["<PostLoad", "<ResolveReferences", "<LoadDataFromXmlCustom"];

    /// <summary>True for a per-def or per-patch label: neither it nor anything nested in it is recorded.</summary>
    public static bool IsPerDefNoise(string label)
    {
        if (string.IsNullOrEmpty(label))
            return false;
        if (label.StartsWith("Register", StringComparison.Ordinal) && label.Contains("WantsCrossRef"))
            return true;
        if (label.EndsWith(" Worker", StringComparison.Ordinal))
            return true;
        var action = label.IndexOf(" -> ", StringComparison.Ordinal);
        if (action < 0)
            return false;
        foreach (var hook in PerDefHooks)
        {
            if (label.IndexOf(hook, action, StringComparison.Ordinal) >= 0)
                return true;
        }
        return false;
    }
}
