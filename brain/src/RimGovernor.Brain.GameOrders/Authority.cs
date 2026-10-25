using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Authority;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameOrders.Authority;

public sealed record StatusRequest
{
    public RimGovernor.Brain.GameFacts.Common.Identity? Identity { get; init; }
}

/// <summary>
/// SetMode is the only way to change authority explicitly. Auto grants the bot
/// authority outright; Manual revokes it. There is no acquire/renew handshake.
/// </summary>
public sealed record SetMode
{
    public RimGovernor.Brain.GameFacts.Common.Identity? Identity { get; init; }

    public required ulong ExpectedGeneration { get; init; }

    public required Mode Mode { get; init; }
}

public sealed record Revoke
{
    public RimGovernor.Brain.GameFacts.Common.Identity? Identity { get; init; }

    public required ulong ExpectedGeneration { get; init; }

    public required RevocationReason Reason { get; init; }
}

public sealed record ControlRequest
{
    public ControlRequestOperation? Operation { get; init; }
}

/// <summary>The <c>operation</c> choice of <see cref="ControlRequest"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ControlRequestOperation.SetModeCase), "setMode")]
[JsonDerivedType(typeof(ControlRequestOperation.RevokeCase), "revoke")]
public abstract record ControlRequestOperation
{
    private protected ControlRequestOperation() { }

    public sealed record SetModeCase(SetMode Value) : ControlRequestOperation;

    public static implicit operator ControlRequestOperation(SetMode value) => new SetModeCase(value);

    public sealed record RevokeCase(Revoke Value) : ControlRequestOperation;

    public static implicit operator ControlRequestOperation(Revoke value) => new RevokeCase(value);
}

/// <summary>
/// Writes atomically require current colony/load/map identity and a fresh
/// attempt; Auto has a single bot owner.
/// </summary>
public sealed record WritePrecondition
{
    public RimGovernor.Brain.GameFacts.Common.Identity? Identity { get; init; }

    public RimGovernor.Brain.GameFacts.Common.AttemptKey? Attempt { get; init; }
}
