using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Tests that open an <c>OtlpFileWriter</c>. Its activity listener is process-wide, so a span any other test starts
/// meanwhile lands in that test's traces file (#3345); these run alone, after the parallel tests.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SpanWriters
{
    public const string Name = "OTLP span writers";
}
