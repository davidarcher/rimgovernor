using System.Text.Json;
using Google.Protobuf.WellKnownTypes;

namespace RimGovernor.BrainSpike.Contracts;

/// <summary>What the brain may call back into. Lives in the default context beside the host.</summary>
public interface IBrainHost
{
    /// <summary>A long-lived, host-owned serializer configuration (the shared-cache hazard).</summary>
    JsonSerializerOptions SharedJson { get; }

    /// <summary>A host-owned event (the subscription-leak hazard).</summary>
    event Action<string>? Observation;
}

/// <summary>The reloadable unit. Messages are protobuf types shared from the default context.</summary>
public interface IBrain : IDisposable
{
    StringValue Tick(StringValue frame);

    string Describe();
}

public interface IBrainFactory
{
    IBrain Create(string scenario, IBrainHost host);
}
