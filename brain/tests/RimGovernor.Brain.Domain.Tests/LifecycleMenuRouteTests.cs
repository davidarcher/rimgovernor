using System.Text;
using System.Text.Json;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.HttpChannel;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.PlayerApi;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// <c>/api/lifecycle/menu</c> (#3298): the return to the main menu starts with a POST and is recovered by request id,
/// like load and new colony. The POST names the game it ends when the controller observes one, and names nothing at
/// the main menu.
/// </summary>
public sealed class LifecycleMenuRouteTests
{
    private const string Colony = "colony-1";
    private const string Load = "load-1";
    private const int Map = 3;

    private sealed class Snapshots(bool loaded) : ISnapshotProvider
    {
        public Task<ControllerSnapshot> SnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(loaded
            ? new ControllerSnapshot
            {
                Connected = true,
                Mode = "manual",
                World = new ControllerWorld.Observed(new RimGovernor.Brain.GameAccess.ObservationIdentity { Context = new() { Identity = new() { ColonyId = Colony, LoadToken = Load, MapId = Map }, Tick = 10, NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase() } }, false, default),
                Control = new ControllerControl.Matching(new GenerationSnapshot()),
            }
            : new ControllerSnapshot { Connected = false, Stale = true, Mode = "manual", World = new ControllerWorld.Unobserved(), Control = new ControllerControl.Unmatched() });
    }

    private sealed class Writer(Func<RimGovernor.Brain.GameFacts.Lifecycle.MenuReply> reply) : ILifecycleWriter
    {
        public List<RimGovernor.Brain.GameOrders.Lifecycle.MenuRequest> Started { get; } = [];

        public List<string> Read { get; } = [];

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.MenuReply> MenuAsync(RimGovernor.Brain.GameOrders.Lifecycle.MenuRequest request, CancellationToken cancellationToken)
        {
            Started.Add(request);
            return Task.FromResult(reply());
        }

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.MenuReply> ReadMenuAsync(string requestId, CancellationToken cancellationToken)
        {
            Read.Add(requestId);
            return Task.FromResult(reply());
        }

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.SaveReply> SaveAsync(RimGovernor.Brain.GameOrders.Lifecycle.SaveRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.SaveReply> ReadSaveAsync(string requestId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadReply> LoadAsync(RimGovernor.Brain.GameOrders.Lifecycle.LoadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadReply> ReadLoadAsync(string requestId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.NewColonyReply> NewColonyAsync(RimGovernor.Brain.GameOrders.Lifecycle.NewColonyRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.NewColonyReply> ReadNewColonyAsync(string requestId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static RimGovernor.Brain.GameFacts.Lifecycle.MenuReply Pending(string id) => new() { Outcome = new RimGovernor.Brain.GameFacts.Lifecycle.MenuPending { RequestId = id, Detail = "Unloading the game." } };

    private static RimGovernor.Brain.GameFacts.Lifecycle.MenuReply Completed(string id, bool unloaded) => new() { Outcome = new RimGovernor.Brain.GameFacts.Lifecycle.MenuCompleted { RequestId = id, Unloaded = unloaded } };

    private static async Task<(int Status, JsonElement Body)> SendAsync(ISnapshotProvider snapshots, ILifecycleWriter writer, string method, string target, string? body)
    {
        var routes = new RouteTable();
        using var registered = LifecycleRoutes.Register(routes, snapshots, writer);
        var bytes = body is null ? [] : Encoding.UTF8.GetBytes(body);
        var head = new RequestHead
        {
            Method = method,
            Target = target,
            Host = "127.0.0.1:1",
            ContentType = body is null ? null : "application/json",
            ContentLength = bytes.Length,
        };
        var response = await new Dispatcher(routes, 1 << 20).DispatchAsync(head, new Dictionary<string, string>(), (_, _) => Task.FromResult(bytes), CancellationToken.None);
        return (response.Status, JsonDocument.Parse(response.Body).RootElement.Clone());
    }

    [Fact]
    public async Task APostWhileAGameIsObservedNamesThatGameAndAnswersPending()
    {
        var writer = new Writer(() => Pending("menu-1"));
        var (status, body) = await SendAsync(new Snapshots(loaded: true), writer, "POST", LifecycleRoutes.MenuPath, """{"requestId":"menu-1"}""");

        Assert.Equal(202, status);
        Assert.Equal("pending", body.GetProperty("status").GetString());
        Assert.Equal("menu-1", body.GetProperty("requestId").GetString());
        var request = Assert.Single(writer.Started);
        Assert.Equal("menu-1", request.RequestId);
        Assert.Equal(new RimGovernor.Brain.GameFacts.Common.Identity { ColonyId = Colony, LoadToken = Load, MapId = Map }, request.ExpectedPlayer?.Identity);
        Assert.NotEqual(0UL, request.ExpectedPlayer?.PlayerDirection);
    }

    [Fact]
    public async Task APostAtTheMainMenuNamesNoGameAndAnswersCompleted()
    {
        var writer = new Writer(() => Completed("menu-2", unloaded: false));
        var (status, body) = await SendAsync(new Snapshots(loaded: false), writer, "POST", LifecycleRoutes.MenuPath, """{"requestId":"menu-2"}""");

        Assert.Equal(201, status);
        Assert.Equal("completed", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("unloaded").GetBoolean());
        Assert.Null(Assert.Single(writer.Started).ExpectedPlayer);
    }

    [Fact]
    public async Task TheRecoveryReadAnswersPendingAndCompletedWith200()
    {
        var replies = new Queue<RimGovernor.Brain.GameFacts.Lifecycle.MenuReply>([Pending("menu-3"), Completed("menu-3", unloaded: true)]);
        var writer = new Writer(replies.Dequeue);
        var snapshots = new Snapshots(loaded: false);

        var (pendingStatus, pending) = await SendAsync(snapshots, writer, "GET", LifecycleRoutes.MenuPath + "?requestId=menu-3", null);
        var (doneStatus, done) = await SendAsync(snapshots, writer, "GET", LifecycleRoutes.MenuPath + "?requestId=menu-3", null);

        Assert.Equal(200, pendingStatus);
        Assert.Equal("pending", pending.GetProperty("status").GetString());
        Assert.Equal(200, doneStatus);
        Assert.Equal("completed", done.GetProperty("status").GetString());
        Assert.True(done.GetProperty("unloaded").GetBoolean());
        Assert.Equal(["menu-3", "menu-3"], writer.Read);
    }

    [Fact]
    public async Task AMalformedRequestIsRefusedBeforeNative()
    {
        var writer = new Writer(() => Pending("x"));
        var snapshots = new Snapshots(loaded: true);

        var (missing, _) = await SendAsync(snapshots, writer, "POST", LifecycleRoutes.MenuPath, """{"saveName":"a"}""");
        var (query, _) = await SendAsync(snapshots, writer, "POST", LifecycleRoutes.MenuPath + "?x=1", """{"requestId":"menu-4"}""");
        var (read, _) = await SendAsync(snapshots, writer, "GET", LifecycleRoutes.MenuPath, null);

        Assert.Equal(400, missing);
        Assert.Equal(400, query);
        Assert.Equal(400, read);
        Assert.Empty(writer.Started);
        Assert.Empty(writer.Read);
    }

    [Fact]
    public async Task ANativeRefusalIsShownByItsCode()
    {
        var writer = new Writer(() => new RimGovernor.Brain.GameFacts.Lifecycle.MenuReply
        {
            Outcome = new RimGovernor.Brain.GameFacts.Common.Failure { RefusalClass = RimGovernor.Brain.GameFacts.Common.RefusalClass.Unclassified, Code = RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, Detail = "Ending a live map requires a matching expected player identity and direction." },
        });
        var (status, body) = await SendAsync(new Snapshots(loaded: true), writer, "POST", LifecycleRoutes.MenuPath, """{"requestId":"menu-5"}""");

        Assert.Equal(400, status);
        Assert.Equal("native_invalid_request", body.GetProperty("code").GetString());
    }

    [Fact]
    public void ATickReadAtTheMainMenuIsTheExpectedNotLoadedState()
    {
        // The facade's tick read throws what NativeFaults builds from the native unavailable state.
        _ = Assert.IsType<NotLoadedException>(NativeFaults.Unavailable(new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotLoaded, Detail = "No current colony map is loaded." }));

        var other = NativeFaults.Unavailable(new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.Stale });
        Assert.IsNotType<NotLoadedException>(other);
    }
}
