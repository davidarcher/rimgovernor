using System.Text;
using System.Text.Json;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.HttpChannel;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.PlayerApi;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #2723: <c>POST /api/lifecycle/new</c> answered every failed start 503 "Controller data is unavailable", so a themed
/// colony whose design failed looked like a brain still starting, and the harness asked again until its patience ran
/// out. A failed start now answers with its cause.
/// </summary>
public sealed class LifecycleNewColonyRouteTests
{
    private const string Body = """
        {"requestId":"theme-create","timeoutMs":600000,"spec":{"biomes":["TemperateForest"],"colonistCount":3,"difficulty":"Peaceful",
        "flatTile":true,"mapSize":150,"planetCoverage":0.05,"saveName":"theme-colony-accept","scenario":"LostTribe","seed":"theme-colony-accept",
        "storyteller":"Phoebe","theme":"naked-cannibals"}}
        """;

    private sealed class Snapshots : ISnapshotProvider
    {
        public Task<ControllerSnapshot> SnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ControllerSnapshot { Connected = false, Stale = true, Mode = "manual", World = new ControllerWorld.Unobserved(), Control = new ControllerControl.Unmatched() });
    }

    private sealed class Writer(Exception failure) : ILifecycleWriter
    {
        public Task<RimGovernor.Brain.GameFacts.Lifecycle.NewColonyReply> NewColonyAsync(RimGovernor.Brain.GameOrders.Lifecycle.NewColonyRequest request, CancellationToken cancellationToken) => Task.FromException<RimGovernor.Brain.GameFacts.Lifecycle.NewColonyReply>(failure);

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.NewColonyReply> ReadNewColonyAsync(string requestId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.MenuReply> MenuAsync(RimGovernor.Brain.GameOrders.Lifecycle.MenuRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.MenuReply> ReadMenuAsync(string requestId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.SaveReply> SaveAsync(RimGovernor.Brain.GameOrders.Lifecycle.SaveRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.SaveReply> ReadSaveAsync(string requestId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadReply> LoadAsync(RimGovernor.Brain.GameOrders.Lifecycle.LoadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadReply> ReadLoadAsync(string requestId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static async Task<(int Status, string Code, string Detail)> PostAsync(Exception failure)
    {
        var routes = new RouteTable();
        using var registered = LifecycleRoutes.Register(routes, new Snapshots(), new Writer(failure));
        var bytes = Encoding.UTF8.GetBytes(Body);
        var head = new RequestHead
        {
            Method = "POST",
            Target = LifecycleRoutes.NewPath,
            Host = "127.0.0.1:1",
            ContentType = "application/json",
            ContentLength = bytes.Length,
        };
        var response = await new Dispatcher(routes, 1 << 20).DispatchAsync(head, new Dictionary<string, string>(), (_, _) => Task.FromResult(bytes), CancellationToken.None);
        var body = JsonDocument.Parse(response.Body).RootElement;
        return (response.Status, body.GetProperty("code").GetString()!, body.GetProperty("detail").GetString()!);
    }

    [Fact]
    public async Task AFailedThemeDesignAnswersItsCauseAndIsNotAskedAgain()
    {
        var (status, code, detail) = await PostAsync(new InvalidOperationException("No supported legal ideoligion design holds the naked-cannibals theme's memes, precepts and animals."));

        Assert.Equal(500, status);
        Assert.Equal("new_colony_failed", code);
        Assert.Contains("naked-cannibals", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANativeRefusalAnswersByItsCode()
    {
        var failure = new RimGovernor.Brain.GameFacts.Common.Failure { RefusalClass = RimGovernor.Brain.GameFacts.Common.RefusalClass.Unclassified, Code = RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, Detail = "Unknown scenario." };
        var (status, code, detail) = await PostAsync(new NativeFailureException(failure, Result.Empty));

        Assert.Equal(400, status);
        Assert.Equal("native_invalid_request", code);
        Assert.Equal("Unknown scenario.", detail);
    }

    [Fact]
    public async Task AnUnavailableBridgeAnswers503WithItsCause()
    {
        var (status, code, detail) = await PostAsync(new BridgeUnavailableException("new colony governor_ideoligion needs the creation catalog"));

        Assert.Equal(503, status);
        Assert.Equal("unavailable", code);
        Assert.Contains("creation catalog", detail, StringComparison.Ordinal);
    }
}
