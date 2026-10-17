using System.Text.Json.Nodes;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance;

// The plan-stage records of this namespace (StageSpec, StageCell, ...) share names with the fixture contracts, and a
// namespace's own types win over a using, so the contracts are reached through the Fx alias.

/// <summary>
/// The typed fixture stage (#3393): one <c>test/stage</c> call applies a <see cref="Fx.StageSpec"/> over the core
/// primitives and replies a <see cref="Fx.StageReply"/> keyed by the spec's names.
/// </summary>
public static class FixtureStage
{
    /// <summary>The fixture op that applies a stage spec.</summary>
    public const string Tool = "test/stage";

    /// <summary>The stage op as a typed fixture op, for <see cref="ITypedSession.FixtureAsync"/>.</summary>
    public static readonly FixtureOp<Fx.StageSpec, Fx.StageReply> Op = new(Tool, Args, Read);

    /// <summary>A start that brings the game up through <paramref name="on"/> (null: the lab) and stages <paramref name="spec"/>; the reply is the session's Prepared.</summary>
    public static FixtureStart Start(Fx.StageSpec spec, Start? on = null) =>
        new() { Op = Tool, On = on ?? new LabStart(), Args = Args(spec) };

    /// <summary>Stages <paramref name="spec"/> on the loaded game and returns the typed reply.</summary>
    public static Task<Fx.StageReply> ApplyAsync(Harness h, string label, Fx.StageSpec spec, CancellationToken cancellationToken) =>
        Op.CallAsync(h, label, spec, cancellationToken);

    /// <summary>The typed reply of a session started by <see cref="Start"/>.</summary>
    public static Fx.StageReply Prepared(ISession s) => Read(Tool, s.Prepared);

    /// <summary>The op arguments that stage <paramref name="spec"/>, for a start whose spec needs the loaded game (<see cref="FixtureStart.ArgsFrom"/>).</summary>
    public static JsonObject Args(Fx.StageSpec spec) => new() { ["spec"] = Fx.FixtureJson.Serialize(spec) };

    private static Fx.StageReply Read(string label, JsonObject? reply)
    {
        var staged = reply is null ? null : Fx.FixtureJson.Deserialize<Fx.StageReply>(reply.ToJsonString());
        return staged is { Success: true } ? staged : throw new AcceptanceException($"{label}: {Tool} refused: {reply?.ToJsonString()}");
    }
}

/// <summary>
/// A bespoke fixture op (<c>test/*</c>) with a typed request and reply: its tool name and the codec between its records
/// and the op's JSON arguments and reply. A typed case calls it through <see cref="ITypedSession.FixtureAsync"/>.
/// </summary>
public sealed class FixtureOp<TRequest, TReply>
{
    private readonly Func<TRequest, JsonObject> _args;
    private readonly Func<string, JsonObject?, TReply> _reply;

    /// <param name="tool">The op's tool name (<c>test/stage</c>).</param>
    /// <param name="args">The op's JSON arguments for a request.</param>
    /// <param name="reply">The typed reply of a JSON reply under a call label; throws <see cref="AcceptanceException"/> on a refusal.</param>
    public FixtureOp(string tool, Func<TRequest, JsonObject> args, Func<string, JsonObject?, TReply> reply)
    {
        Tool = tool;
        _args = args;
        _reply = reply;
    }

    /// <summary>The op's tool name, as a case declares it in its Ops.</summary>
    public string Tool { get; }

    /// <summary>Calls the op on the loaded game and returns its typed reply.</summary>
    public async Task<TReply> CallAsync(Harness h, string label, TRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(h);
        var reply = await h.CallAsync(label, Tool, _args(request), cancellationToken).ConfigureAwait(false);
        return _reply(label, reply);
    }
}
