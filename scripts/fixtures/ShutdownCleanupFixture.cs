#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Brain.Host;
using RimGovernor.Fixtures.Contracts;
using RimGovernor.Host.Sdk;
using Verse;
using Waylith;
using Waylith.Runtime;

namespace RimGovernor.Native;

// Exercises the engine's actual event wiring without exiting before the harness
// can read the postcondition. NoKeep retires this process after the reply.
public sealed class ShutdownCleanupFixture
{
    private const string Owner = "rimgovernor.fixture.shutdown-cleanup";
    private static Action? pending;

    [Tool("test/shutdown_cleanup", Description = "Disposable fixture: fires engine quit callbacks and reads game cleanup before the engine allocation sweep.")]
    public async Task<object> Run(INativeContext ctx, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        await ctx.MainThread.InvokeAsync(() =>
        {
            pending = () =>
            {
                try { completion.TrySetResult(Check()); }
                catch (Exception e) { completion.TrySetException(e); }
            };
            // The engine raises quit AFTER the update, not halfway through Root.Update
            // while Root_Play still has work to do with Current.Game.
            new Harmony(Owner).Patch(AccessTools.Method(typeof(Root_Play), nameof(Root_Play.Update)),
                postfix: new HarmonyMethod(typeof(ShutdownCleanupFixture), nameof(AfterUpdate)));
        }, cancellationToken).ConfigureAwait(false);
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AfterUpdate()
    {
        var run = pending;
        pending = null;
        if (run == null) return;
        new Harmony(Owner).Unpatch(AccessTools.Method(typeof(Root_Play), nameof(Root_Play.Update)), HarmonyPatchType.Postfix, Owner);
        run();
    }

    private static object Check()
    {
            var map = Find.CurrentMap ?? throw new InvalidOperationException("Loaded map required");
            var brain = BrainBootstrap.Host ?? throw new InvalidOperationException("Running brain required");
            if (brain.State != BrainHostState.Running) throw new InvalidOperationException("Brain must be running before quit");
            var before = NativeAllocationRuntime.GetDiagnosticsSnapshot().TotalBytes;
            // Waylith's event raiser is internal. Invoke its registered delegate
            // (compiler-checked event name), not BrainBootstrap's handler, so
            // this verifies production subscription as well as cleanup.
            var app = EngineApplication.Instance.Runtime.App;
            var quit = AccessTools.Field(typeof(WaylithAppState), nameof(WaylithAppState.ShuttingDown))?.GetValue(app) as Action
                ?? throw new InvalidOperationException("Engine quit event is unavailable");
            quit();
            // A duplicate quit notification must not dispose the game or cancellation source twice.
            quit();
            var reply = new ShutdownCleanupReply
            {
                BrainStopped = brain.State == BrainHostState.Stopped,
                MapDisposed = map.Disposed,
                GameCleared = Current.Game == null,
                BytesBefore = before,
                BytesAfter = NativeAllocationRuntime.GetDiagnosticsSnapshot().TotalBytes,
            };
            // The real engine stops frames after its quit callbacks. This fixture
            // keeps HTTP alive long enough to return evidence, so leave the play
            // scene only AFTER recording the cleanup postcondition.
            GenScene.GoToMainMenu();
            return FixtureKit.Reply(reply);
    }
}
