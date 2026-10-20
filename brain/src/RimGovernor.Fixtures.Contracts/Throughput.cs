#nullable enable
namespace RimGovernor.Fixtures.Contracts;

/// <summary>The arguments of <c>test/throughput_prepare</c>: the Cloth stacks to spawn.</summary>
public sealed record ThroughputPrepareRequest
{
    public int ItemCount { get; init; }
}
