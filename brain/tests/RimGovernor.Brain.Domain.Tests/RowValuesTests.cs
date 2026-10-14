using System.Collections.Immutable;
using RimGovernor.Brain.Telemetry;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Decision rows carrying typed maps were lost to an InvalidCast in <c>RowValues.Normalize</c> (#3143).</summary>
public sealed class RowValuesTests
{
    [Fact]
    public void TypedDictionariesBecomeSortedMaps()
    {
        var map = (IReadOnlyDictionary<string, object?>)RowValues.Normalize(new Dictionary<string, double> { ["b"] = 2.5, ["a"] = 1 })!;
        Assert.Equal(["a", "b"], map.Keys);
        Assert.Equal(2.5, map["b"]);
    }

    [Fact]
    public void ReadOnlyAndNonStringKeyedMapsAreHandled()
    {
        var ro = (IReadOnlyDictionary<string, object?>)RowValues.Normalize(ImmutableDictionary<string, int>.Empty.Add("k", 3))!;
        Assert.Equal(3L, ro["k"]);
        var ints = (IReadOnlyDictionary<string, object?>)RowValues.Normalize(new Dictionary<int, string> { [7] = "x" })!;
        Assert.Equal("x", ints["7"]);
    }

    [Fact]
    public void SequencesStayArrays() =>
        Assert.Equal(new List<object?> { 1L, 2L }, RowValues.Normalize(new List<int> { 1, 2 }));
}
