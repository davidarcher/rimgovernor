using System.Reflection;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The sustained colony variants: the manifest tools/variantsavegen generates saves from and the
/// <c>sustained/matrix-*</c> cases load (Go <c>sustained/matrix.go</c>).
/// </summary>
internal static class SustainedMatrix
{
    private const string ManifestResource = "sustained.issue-1-matrix.json";

    /// <summary>The decoded, validated manifest.</summary>
    internal static IReadOnlyList<Variant> Variants() => VariantGen.Manifest(ReadManifest());

    /// <summary>A matrix save's case-name suffix: the save name without its RimGovernor-matrix- prefix, sanitized.</summary>
    internal static string Short(string save) =>
        VariantGen.Sanitize(save.StartsWith("RimGovernor-matrix-", StringComparison.Ordinal) ? save["RimGovernor-matrix-".Length..] : save);

    internal static IEnumerable<AcceptanceCase> Cases()
    {
        foreach (var v in Variants())
        {
            var name = Short(v.Save);
            yield return SustainedFoodCase.Case("sustained/matrix-" + name, v.Save, "sustained-matrix-" + name, SustainedFoodCase.MatrixWindowTicks);
        }
    }

    private static string ReadManifest()
    {
        using var stream = typeof(SustainedMatrix).Assembly.GetManifestResourceStream(ManifestResource)
            ?? throw new InvalidOperationException($"manifests/issue-1-matrix.json is not embedded ({ManifestResource})");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
