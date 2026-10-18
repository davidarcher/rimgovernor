using System.Net;
using System.Text;
using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.HttpChannel;
using RimGovernor.Contracts.Wire;
using Xunit;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #2723: <c>lifecycle/ideoligion-design</c> sent its <c>governorIdeoligion</c> spec straight to the native op, which
/// refuses the flag (the brain resolves it before native creation). The harness now starts such a colony through the
/// brain's player route: the wire request goes to <c>POST /api/lifecycle/new</c>, a route the brain has not registered yet
/// is asked again, and any other refusal fails the start with the route's answer.
/// </summary>
public sealed class HarnessNewColonyRouteTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rg-newcolony-route-" + Guid.NewGuid().ToString("N"));

    public HarnessNewColonyRouteTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static Lifecycle.NewColonyRequest Request() => new()
    {
        RequestId = "ideoligion-create",
        TimeoutMs = 600_000,
        Spec = new Lifecycle.NewColonySpec { Scenario = "Crashlanded", ColonistCount = 3, GovernorIdeoligion = true },
    };

    [Fact]
    public async Task StartPostsTheWireRequestToTheBrainRouteAndAsksAgainWhileTheRouteIsNotRegistered()
    {
        var sent = new List<(HttpMethod Method, string Path, string Body)>();
        var answers = new Queue<HttpStatusCode>([HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable, HttpStatusCode.Accepted]);
        await using var harness = Harness(request =>
        {
            sent.Add((request.Method, request.RequestUri!.AbsolutePath, request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            return new HttpResponseMessage(answers.Dequeue()) { Content = new StringContent("{}") };
        });

        await harness.StartNewColonyThroughBrainAsync("ideoligion-start", Request(), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Equal(3, sent.Count);
        Assert.All(sent, s => Assert.Equal((HttpMethod.Post, "/api/lifecycle/new"), (s.Method, s.Path)));
        var body = WireJson.Deserialize<Lifecycle.NewColonyRequest>(sent[0].Body);
        Assert.Equal(Request(), body);
        Assert.True(body.Spec!.GovernorIdeoligion);
    }

    [Fact]
    public async Task RefusedStartFailsWithTheRouteAnswer()
    {
        await using var harness = Harness(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"code":"native_invalid_request","message":"Unknown scenario"}""", Encoding.UTF8, "application/json"),
        });

        var refused = await Assert.ThrowsAsync<AcceptanceException>(
            () => harness.StartNewColonyThroughBrainAsync("ideoligion-start", Request(), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
        Assert.Contains("answered 400", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Unknown scenario", refused.Message, StringComparison.Ordinal);
    }

    private Harness Harness(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        EndpointFile.Write(root, "http://127.0.0.1:1", Environment.ProcessId, "run");
        return new Harness(new HttpNativeTransport(root, TimeSpan.FromSeconds(5), new HttpClient(new Handler(respond))), root);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
