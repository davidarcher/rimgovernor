using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.HttpChannel;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3349: a detached game inherits only its own log, never the caller's redirected stdout, so a redirected
/// <c>acceptance run</c> returns when the case ends. #3350: a run whose game exited or whose channel stopped answering
/// ends at once with the cause, instead of at its wall budget; calls after that fail without waiting, and a kept game
/// that answers nothing fails its connect in seconds.
/// </summary>
public sealed class AcceptanceFailFastTests : IDisposable
{
    private const string GameId = "fake";
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    private readonly string root = Path.Combine(Path.GetTempPath(), "rg-failfast-" + Guid.NewGuid().ToString("N"));

    public AcceptanceFailFastTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        _ = GameProcess.StopAsync(root, GameId).GetAwaiter().GetResult();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private GameProcess Start(string script) => GameProcess.StartDetached(new GameLaunchSpec
    {
        GameId = GameId,
        Executable = "cmd.exe",
        Args = ["/c", script],
        StateDir = root,
    });

    [Fact]
    public async Task DetachedGameDoesNotHoldTheCallersRedirectedPipe()
    {
        // The pipe's write end stands for the caller's redirected stdout: inheritable, as a redirect's handle is.
        using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var game = Start("ping -n 60 127.0.0.1 >nul");
        pipe.DisposeLocalCopyOfClientHandle();

        // End of file arrives only once no process holds the write end; a game that inherited it keeps it open.
        var read = Task.Run(() => pipe.ReadByte());
        var finished = await Task.WhenAny(read, Task.Delay(HangGuard, TestContext.Current.CancellationToken));
        Assert.Same(read, finished);
        Assert.Equal(-1, await read);
        Assert.True(game.Alive, "the kept game stays up after the caller's pipe closed");
    }

    [Fact]
    public async Task ExitedGameIsGoneWithItsExitCodeAndLogTail()
    {
        _ = Start("echo boom& exit 7");
        var deadline = DateTime.UtcNow + HangGuard;
        string? reason;
        while ((reason = GameSessions.GoneReason(root, GameId)) is null || GameProcess.LastExit(root, GameId) is null)
        {
            Assert.True(DateTime.UtcNow < deadline, "the fake game never exited");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        reason = GameSessions.GoneReason(root, GameId);
        Assert.NotNull(reason);
        Assert.Contains("exit code 7", reason, StringComparison.Ordinal);
        Assert.Contains("boom", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunningGameIsNotGone()
    {
        _ = Start("ping -n 60 127.0.0.1 >nul");
        Assert.Null(GameSessions.GoneReason(root, GameId));
        await GameProcess.StopAsync(root, GameId, TestContext.Current.CancellationToken);
        Assert.Null(GameSessions.GoneReason(root, GameId));
    }

    [Fact]
    public async Task TransportFailsTheRunOnTheCallAfterTheGameExited()
    {
        var calls = 0;
        var transport = Transport(_ => { calls++; return Reply(HttpStatusCode.OK); }, gone: () => "the game (pid 1) exited (exit code 3)");

        var lost = await Assert.ThrowsAsync<RunLostException>(() => Call(transport));
        Assert.Contains("exit code 3", lost.Message, StringComparison.Ordinal);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task TwoConsecutiveTransportFailuresEndTheRun()
    {
        var transport = Transport(_ => throw new HttpRequestException("connection refused"));

        _ = await Assert.ThrowsAsync<NativeTransportException>(() => Call(transport));
        var lost = await Assert.ThrowsAsync<RunLostException>(() => Call(transport));
        Assert.Contains("native unreachable: 2 consecutive transport failures", lost.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnsweredCallBetweenFailuresKeepsTheRunAlive()
    {
        var fail = true;
        var transport = Transport(_ => fail ? throw new HttpRequestException("connection refused") : Reply(HttpStatusCode.OK));

        _ = await Assert.ThrowsAsync<NativeTransportException>(() => Call(transport));
        fail = false;
        _ = await Call(transport);
        fail = true;
        _ = await Assert.ThrowsAsync<NativeTransportException>(() => Call(transport));
    }

    [Fact]
    public async Task FailedCheckpointReadEndsTheRunAndTheCallsAfterItFailAtOnce()
    {
        var calls = 0;
        var transport = Transport(_ => { calls++; return Reply(HttpStatusCode.OK); });
        var harness = new Harness(transport, root);
        var ring = new CheckpointRing(Path.Combine(root, "ring"), "case", new Config { Output = root })
        {
            Every = TimeSpan.FromMilliseconds(1),
            Keep = 5,
            Bridge = () => harness,
            Saver = (_, _, _, _) => throw new NativeTransportException("native transport failure: lifecycle_read_identity: no reply within 60s"),
        };

        var lost = await Assert.ThrowsAsync<RunLostException>(() => ring.MaybeAsync(CancellationToken.None));
        Assert.Contains("no reply within 60s", lost.Message, StringComparison.Ordinal);
        Assert.Single(ring.Errors());

        var after = await Assert.ThrowsAsync<RunLostException>(() => Call(transport));
        Assert.Equal(lost.Message, after.Message);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CallAfterALostRunFailsAtOnceWithTheFirstCause()
    {
        var calls = 0;
        var transport = Transport(_ => { calls++; throw new HttpRequestException("connection refused"); });

        _ = await Assert.ThrowsAsync<NativeTransportException>(() => Call(transport));
        var lost = await Assert.ThrowsAsync<RunLostException>(() => Call(transport));
        Assert.Equal(2, calls);

        // Nothing is sent: the call does not wait out its deadline against a game already given up on.
        var after = await Assert.ThrowsAsync<RunLostException>(() => Call(transport));
        Assert.Equal(lost.Message, after.Message);
        Assert.Equal(2, calls);
        Assert.Null(await transport.ToolNamesAsync(CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task KeptGameThatAnswersNothingFailsWithinTheAttachBudget()
    {
        var game = Start("ping -n 120 127.0.0.1 >nul");

        // Kept: launched an hour ago, so its boot is long over.
        File.WriteAllText(GameProcess.EndpointPath(root, GameId), (game.Record with { LaunchedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(1) }).ToJson());

        // Its channel accepts connections (the listen backlog) and never answers.
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        EndpointFile.Write(GameSessions.EndpointDirectory(root, GameId), $"http://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}", game.Pid, "run");

        var open = GameSessions.OpenBridgeSessionAsync(root, GameId, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(open, Task.Delay(HangGuard, TestContext.Current.CancellationToken));

        // The hang guard is a fifth of the boot budget a fresh launch gets: finishing inside it is the attach budget.
        Assert.Same(open, finished);
        var failed = await Assert.ThrowsAsync<AcceptanceException>(() => open);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"is alive but did not answer within {GameSessions.AttachBudget.TotalSeconds:0}s"), failed.Message, StringComparison.Ordinal);
        Assert.Contains("no reply", failed.Message, StringComparison.Ordinal);
    }

    private HttpNativeTransport Transport(Func<HttpRequestMessage, HttpResponseMessage> respond, Func<string?>? gone = null)
    {
        EndpointFile.Write(root, "http://127.0.0.1:1", Environment.ProcessId, "run");
        return new HttpNativeTransport(root, TimeSpan.FromSeconds(5), new HttpClient(new Handler(respond))) { Gone = gone };
    }

    private static Task<NativeResult> Call(HttpNativeTransport transport) =>
        transport.CallAsync("rimgovernor/lifecycle_read_identity", new JsonObject { ["request"] = "{}" }, new NativeCallOptions(), CancellationToken.None);

    private static HttpResponseMessage Reply(HttpStatusCode status) => new(status) { Content = new StringContent("{}") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
