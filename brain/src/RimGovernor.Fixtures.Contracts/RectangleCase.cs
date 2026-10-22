#nullable enable
using System.Collections.Generic;

namespace RimGovernor.Fixtures.Contracts;

/// <summary>Acceptance-only execution of the prepared rectangle case; never a planner operation.</summary>
public sealed record RectangleCaseRequest
{
    public required string RunId { get; init; }
    public required string ColonyId { get; init; }
    public required string LoadToken { get; init; }
    public required int MapId { get; init; }
    public required StageRect Site { get; init; }
    public required string PlayerZone { get; init; }
    public RectangleCaseFault Fault { get; init; }
}

public enum RectangleCaseFault { None, Assertion, Exit, Hang }

/// <summary>Also written durably at evidence boundaries, so interrupted runs retain their last evidence.</summary>
public sealed record RectangleCaseResult
{
    public required string RunId { get; init; }
    public required string Phase { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public required Dictionary<string, string> Evidence { get; init; }
}
