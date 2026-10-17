using System.Reflection;
using System.Threading.Channels;
using RimGovernor.Brain.Host;
using RimGovernor.Brain.NativeFacade;
using Xunit;
using PbObservations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Stopping a started host drains the worker, player and session, then closes the state database (#3305). The
/// composition is the production one over a native facade that opens its frame stream and then never answers, so
/// the run driver, session, player, worker and scheduler loop are all real.
/// </summary>
public sealed class BrainHostStopTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

#pragma warning disable CA1852 // DispatchProxy derives the proxy from its base, so the base cannot be sealed
    private class SilentGame : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "get_" + nameof(INativeFacade.Frames))
            {
                return Channel.CreateUnbounded<NativeFrame>().Reader;
            }
            if (targetMethod.Name == nameof(INativeFacade.OpenFramesAsync))
            {
                return Task.FromResult(new PbObservations.SnapshotStreamReply { Outcome = new PbObservations.SnapshotStreamOpened { Name = "in-process" } });
            }

            // Every other call waits for its caller to give up, as a game that never answers does.
            var answer = targetMethod.ReturnType.GetGenericArguments()[0];
            var source = typeof(TaskCompletionSource<>).MakeGenericType(answer);
            var pending = Activator.CreateInstance(source, TaskCreationOptions.RunContinuationsAsynchronously)!;
            var token = args!.OfType<CancellationToken>().FirstOrDefault();
            _ = token.Register(() => source.GetMethod(nameof(TaskCompletionSource.TrySetCanceled), [typeof(CancellationToken)])!.Invoke(pending, [token]));
            return source.GetProperty(nameof(TaskCompletionSource.Task))!.GetValue(pending);
        }
    }
#pragma warning restore CA1852

    [Fact]
    public async Task StopAfterStartDrainsAndClosesTheStateDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "rg-host-stop-" + Guid.NewGuid().ToString("N"));
        var otlp = Path.Combine(root, "otlp");
        var state = Path.Combine(root, "state.db");
        _ = Directory.CreateDirectory(otlp);
        try
        {
            var host = await BrainHost.StartAsync(
                new BrainHostOptions
                {
                    StatePath = state,
                    OtlpDirectory = otlp,
                    RunId = "host-stop",
                    // One drain attempt, as the mod runs it: a retry would hide a failure the first attempt had.
                    Timings = BrainTimings.Standard(TimeSpan.FromSeconds(5)) with { ShutdownAttempts = 1 },
                    Watchdog = BrainWatchdog.Standard(),
                    Composition = new BrainComposition(new BrainCompositionOptions()),
                    Facade = DispatchProxy.Create<INativeFacade, SilentGame>(),
                },
                Ct);

            await host.StopAsync();

            Assert.Equal(BrainHostState.Stopped, host.State);
            // The database is closed: nothing holds its file, so an exclusive open succeeds.
            using var exclusive = new FileStream(state, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.DoesNotContain("database_retained", ReadLogs(otlp), StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // a still-open file would already have failed the assertions above
            }
        }
    }

    private static string ReadLogs(string otlp) =>
        string.Concat(Directory.EnumerateFiles(otlp, "*.jsonl", SearchOption.AllDirectories).Select(f =>
        {
            using var reader = new StreamReader(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            return reader.ReadToEnd();
        }));
}
