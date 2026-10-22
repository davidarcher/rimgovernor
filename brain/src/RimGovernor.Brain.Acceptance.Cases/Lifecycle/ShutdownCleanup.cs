using System.Text.Json.Nodes;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

internal static class LifecycleShutdownCleanup
{
    private const string Tool = "test/shutdown_cleanup";
    private static readonly FixtureOp<bool, Fx.ShutdownCleanupReply> Quit = new(
        Tool, _ => new JsonObject(),
        (label, reply) => reply is not null && Fx.FixtureJson.Deserialize<Fx.ShutdownCleanupReply>(reply.ToJsonString()) is { } read
            ? read : throw new AcceptanceException($"{label}: missing shutdown cleanup reply"));

    internal static TypedCase Case() => new()
    {
        Name = "lifecycle/shutdown-cleanup",
        Scope = "Engine quit callbacks stop the running brain and dispose a loaded map before the engine allocation sweep; duplicate quit is safe. No simulation ticks required. NoKeep: the brain and game are disposed.",
        Ticks = 0,
        Tier = CaseTier.Smoke,
        Ops = [Tool],
        NoKeep = true,
        Run = async (s, ct) =>
        {
            var reply = await s.FixtureAsync("quit", Quit, true, ct).ConfigureAwait(false);
            if (!reply.BrainStopped || !reply.MapDisposed || !reply.GameCleared || reply.BytesAfter >= reply.BytesBefore)
                throw new AcceptanceException($"Shutdown did not release the loaded game's resources: {reply}");
        },
    };
}
