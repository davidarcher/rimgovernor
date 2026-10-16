using System.Net;
using System.Text;
using RimGovernor.Brain.Acceptance;
using RimGovernor.Launcher;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>New colony reuses a running game through the main menu and restarts only one it cannot reuse (#3299).</summary>
public sealed class LauncherNewColonyRunnerTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ReusableGameReturnsToTheMenuThenGeneratesWithoutAClose()
    {
        var host = new FakeHost(reusable: "http://game/");
        var game = new FakeGame();
        var runner = Runner(host, game);

        runner.Generate(NewColonySpec.Default);
        await host.Ended.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Equal(ColonyState.Completed, runner.View().Progress.State);
        Assert.Equal(["reusable"], host.Calls);
        Assert.Equal(
            ["POST /api/lifecycle/menu 202", "GET /api/lifecycle/menu 200", "POST /api/lifecycle/new 202", "GET /api/lifecycle/new 200"],
            game.Log);

        // The menu return and the generation are shipped to the game's brain as launch phases.
        var spans = await game.Spans.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
        Assert.Contains("\"launch.to_main_menu\"", spans, StringComparison.Ordinal);
        Assert.Contains("\"start.generate\"", spans, StringComparison.Ordinal);
        Assert.Contains("\"rimgovernor.launch.source\":\"launcher\"", spans, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GameThatCannotBeReusedIsClosedAndStarted()
    {
        var host = new FakeHost(reusable: null);
        var game = new FakeGame();
        var runner = Runner(host, game);

        runner.Generate(NewColonySpec.Default);
        await host.Ended.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Equal(ColonyState.Completed, runner.View().Progress.State);
        Assert.Equal(["reusable", "close", "start"], host.Calls);
        Assert.Equal(["POST /api/lifecycle/new 202", "GET /api/lifecycle/new 200"], game.Log);
    }

    [Fact]
    public async Task BusyGameIsAskedForTheMenuAgain()
    {
        var host = new FakeHost(reusable: "http://game/");
        var game = new FakeGame { BusyMenuPosts = 2 };
        var runner = Runner(host, game);

        runner.Generate(NewColonySpec.Default);
        await host.Ended.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Equal(ColonyState.Completed, runner.View().Progress.State);
        Assert.Equal(["reusable"], host.Calls);
        Assert.Equal(
            ["POST /api/lifecycle/menu 503", "POST /api/lifecycle/menu 503", "POST /api/lifecycle/menu 202", "GET /api/lifecycle/menu 200", "POST /api/lifecycle/new 202", "GET /api/lifecycle/new 200"],
            game.Log);
    }

    [Fact]
    public async Task CancelWhileReturningToTheMenuLeavesTheGameRunning()
    {
        var host = new FakeHost(reusable: "http://game/");
        var game = new FakeGame { MenuNeverCompletes = true };
        var runner = Runner(host, game);

        runner.Generate(NewColonySpec.Default);
        await game.MenuPosted.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
        await runner.CancelAsync();

        Assert.Equal(ColonyState.Cancelled, runner.View().Progress.State);
        Assert.Equal(["reusable"], host.Calls);
    }

    private static NewColonyRunner Runner(FakeHost host, FakeGame game) =>
        new(host, new HttpClient(game), NewColonySpec.Default) { Poll = TimeSpan.FromMilliseconds(1) };

    private sealed class FakeHost(string? reusable) : INewColonyHost
    {
        private readonly Lock _mu = new();

        public bool Observe => false;

        public string SavesDir { get; } = Path.Combine(Path.GetTempPath(), "rimgovernor-no-saves-" + Guid.NewGuid().ToString("N"));

        public List<string> Calls { get; } = [];

        /// <summary>Set when the runner logs the end of the run.</summary>
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> ReusableGameAsync(CancellationToken ct)
        {
            Record("reusable");
            return Task.FromResult(reusable);
        }

        public Task CloseGameAsync()
        {
            Record("close");
            return Task.CompletedTask;
        }

        public Task<string> StartGameAsync(CancellationToken ct)
        {
            Record("start");
            return Task.FromResult("http://game/");
        }

        public void SaveSpec(NewColonySpec spec)
        {
        }

        public void Log(string line)
        {
            if (line.Contains(" saved (seed", StringComparison.Ordinal) || line.StartsWith("new colony: failed", StringComparison.Ordinal))
            {
                _ = Ended.TrySetResult();
            }
        }

        private void Record(string call)
        {
            lock (_mu)
            {
                Calls.Add(call);
            }
        }
    }

    private sealed class FakeGame : HttpMessageHandler
    {
        private readonly Lock _mu = new();
        private int _menuPosts;

        public int BusyMenuPosts { get; init; }

        public bool MenuNeverCompletes { get; init; }

        public List<string> Log { get; } = [];

        public TaskCompletionSource MenuPosted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The launch spans body the runner posted once its run ended (#3287); kept out of <see cref="Log"/>.</summary>
        public TaskCompletionSource<string> Spans { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var post = request.Method == HttpMethod.Post;
            if (path == LaunchTrace.Route && post)
            {
                _ = Spans.TrySetResult(await request.Content!.ReadAsStringAsync(cancellationToken));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"spans":3}""", Encoding.UTF8, "application/json") };
            }

            (HttpStatusCode Status, string Body) reply = (path, post) switch
            {
                ("/api/lifecycle/menu", true) when Interlocked.Increment(ref _menuPosts) <= BusyMenuPosts =>
                    (HttpStatusCode.ServiceUnavailable, """{"code":"unavailable","detail":"a long event is queued"}"""),
                ("/api/lifecycle/menu", true) => (HttpStatusCode.Accepted, """{"status":"pending","requestId":"m","detail":"unloading"}"""),
                ("/api/lifecycle/menu", false) when MenuNeverCompletes => (HttpStatusCode.OK, """{"status":"pending","requestId":"m","detail":"unloading"}"""),
                ("/api/lifecycle/menu", false) => (HttpStatusCode.OK, """{"status":"completed","requestId":"m","unloaded":true}"""),
                ("/api/lifecycle/new", true) => (HttpStatusCode.Accepted, """{"status":"pending","phase":"world","detail":"generating","elapsedMs":5,"rerollCount":0}"""),
                ("/api/lifecycle/new", false) => (HttpStatusCode.OK, """{"status":"completed","saveName":"colony","seed":"abc"}"""),
                _ => (HttpStatusCode.NotFound, """{"code":"not_found","detail":"no route"}"""),
            };
            lock (_mu)
            {
                Log.Add($"{request.Method} {path} {(int)reply.Status}");
            }

            if (path == "/api/lifecycle/menu" && post && reply.Status == HttpStatusCode.Accepted)
            {
                _ = MenuPosted.TrySetResult();
            }

            return new HttpResponseMessage(reply.Status) { Content = new StringContent(reply.Body, Encoding.UTF8, "application/json") };
        }
    }
}
