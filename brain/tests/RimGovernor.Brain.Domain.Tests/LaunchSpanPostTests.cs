using System.Text;
using System.Text.Json;
using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.HttpChannel;
using RimGovernor.Brain.PlayerApi;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The out-of-game launch spans (#3287): the harness/launcher client posts to <c>POST /api/telemetry/spans</c>, the
/// brain replays them into its traces ring, and result.json's <c>boot_phases</c> reads that ring back together with the
/// game's own launch tree.
/// </summary>
[Collection(SpanWriters.Name)]
public sealed class LaunchSpanPostTests
{
    [Fact]
    public void ClientRouteMatchesTheBrainRoute() => Assert.Equal(TelemetryRoutes.SpansPath, LaunchTrace.Route);

    [Fact]
    public void DecodesNestedSpansWithAttributes()
    {
        var phases = TelemetryRoutes.Decode(Encoding.UTF8.GetBytes("""
            [{"name":"launch.connect","parent":"","startUtc":"2026-10-16T10:00:00.0000000Z","endUtc":"2026-10-16T10:00:05Z","attributes":{"rimgovernor.launch.source":"harness"}},
             {"name":"probe","parent":"launch.connect","startUtc":"2026-10-16T10:00:01Z","endUtc":"2026-10-16T10:00:02Z","attributes":{}}]
            """));
        Assert.Equal(2, phases.Count);
        Assert.Equal((1, 0, "launch.connect"), (phases[0].Id, phases[0].Parent, phases[0].Name));
        Assert.Equal(TimeSpan.FromSeconds(5), phases[0].EndUtc - phases[0].StartUtc);
        Assert.Equal(DateTimeKind.Utc, phases[0].StartUtc.Kind);
        Assert.Equal("harness", phases[0].Attributes!["rimgovernor.launch.source"]);
        Assert.Equal(1, phases[1].Parent);
    }

    [Theory]
    [InlineData("""{"name":"a","parent":"","startUtc":"2026-10-16T10:00:00Z","endUtc":"2026-10-16T10:00:01Z","attributes":{}}""")]
    [InlineData("""[{"name":"a","parent":"b","startUtc":"2026-10-16T10:00:00Z","endUtc":"2026-10-16T10:00:01Z","attributes":{}}]""")]
    [InlineData("""[{"name":"a","parent":"","startUtc":"2026-10-16T10:00:02Z","endUtc":"2026-10-16T10:00:01Z","attributes":{}}]""")]
    [InlineData("""[{"name":"a","parent":"","startUtc":"2026-10-16T10:00:00","endUtc":"2026-10-16T10:00:01Z","attributes":{}}]""")]
    [InlineData("""[{"name":"a","parent":"","startUtc":"2026-10-16T10:00:00Z","endUtc":"2026-10-16T10:00:01Z","attributes":{"k":1}}]""")]
    [InlineData("""[{"name":"a","parent":"","startUtc":"2026-10-16T10:00:00Z","endUtc":"2026-10-16T10:00:01Z"}]""")]
    [InlineData("""[{"name":"a","parent":null,"startUtc":"2026-10-16T10:00:00Z","endUtc":"2026-10-16T10:00:01Z","attributes":{}}]""")]
    [InlineData("""[{"name":"","parent":"","startUtc":"2026-10-16T10:00:00Z","endUtc":"2026-10-16T10:00:01Z","attributes":{}}]""")]
    public void RefusesMalformedBodies(string body)
    {
        var response = TelemetryRoutes.Post(new ChannelRequest("POST", TelemetryRoutes.SpansPath, "", Encoding.UTF8.GetBytes(body), new Dictionary<string, string>()));
        Assert.Equal(400, response.Status);
    }

    [Fact]
    public void RefusesMoreThanTheSpanBound()
    {
        var spans = Enumerable.Range(0, TelemetryRoutes.MaxSpans + 1).Select(i => new { name = "s" + i, parent = "", startUtc = "2026-10-16T10:00:00Z", endUtc = "2026-10-16T10:00:00Z", attributes = new { } });
        var response = TelemetryRoutes.Post(new ChannelRequest("POST", TelemetryRoutes.SpansPath, "", JsonSerializer.SerializeToUtf8Bytes(spans), new Dictionary<string, string>()));
        Assert.Equal(400, response.Status);
    }

    [Fact]
    public async Task BootPhasesCoverTheHarnessPostAndTheGameTreeOfThisRunOnly()
    {
        var directory = Path.Combine(Path.GetTempPath(), "launch-post-" + Guid.NewGuid().ToString("N"));
        var since = DateTimeOffset.UtcNow;
        var game = since.UtcDateTime.AddMilliseconds(10);
        try
        {
            await using (var writer = OtlpFileWriter.Start(new() { Dir = directory }))
            {
                // A kept game's own boot, before this run started: not this run's boot.
                var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                Assert.Equal(2, LaunchSpans.Replay([new(1, 0, "old.boot-phase", old, old.AddSeconds(3))], old.AddSeconds(4)));
                // The game's waterfall of this run.
                LaunchSpans.Replay(
                    [
                        new(1, 0, "bridge-start.total", game, game.AddSeconds(2)),
                        new(2, 1, "capabilities.initialize", game, game.AddMilliseconds(1500)),
                    ],
                    game.AddSeconds(3));

                // The harness's phases, posted through the client into the route as the channel would dispatch it.
                var trace = new LaunchTrace("harness");
                trace.Record("launch.prepare", game, game.AddMilliseconds(250));
                trace.Record("launch.connect", game.AddSeconds(1), game.AddSeconds(5));
                trace.Record("start.load", game.AddSeconds(5), game.AddSeconds(7));
                trace.Record("start.load", game.AddSeconds(8), game.AddSeconds(9));
                using var http = new HttpClient(new RouteHandler());
                Assert.Null(await trace.PostAsync(http, "http://127.0.0.1:1", TimeSpan.Zero, CancellationToken.None));
                // Posted phases are forgotten: a second post sends nothing.
                Assert.Null(await trace.PostAsync(new HttpClient(new FailingHandler()), "http://127.0.0.1:1", TimeSpan.Zero, CancellationToken.None));
            }

            var tracesPath = Path.Combine(directory, OtlpFiles.Traces);
            var phases = BootPhases.Read(tracesPath, since);
            Assert.Equal(250, phases["launch.prepare"]!.GetValue<long>());
            Assert.Equal(4000, phases["launch.connect"]!.GetValue<long>());
            Assert.Equal(3000, phases["start.load"]!.GetValue<long>());
            Assert.Equal(2000, phases["bridge-start.total"]!.GetValue<long>());
            Assert.Equal(1500, phases["capabilities.initialize"]!.GetValue<long>());
            Assert.False(phases.ContainsKey("old.boot-phase"));
            Assert.False(phases.ContainsKey(LaunchSpans.Root));

            var spans = new List<OtlpSpan>();
            OtlpReader.ReadSpans(tracesPath, LaunchSpans.Component, spans.Add);
            Assert.All(spans.Where(s => s.Name is "launch.prepare" or "launch.connect"), s => Assert.Equal("harness", s.Attrs.Str(LaunchTrace.SourceAttribute)));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AnUnreachableGameDropsTheSpansWithAReason()
    {
        var trace = new LaunchTrace("launcher");
        trace.Record("launch.connect", DateTime.UtcNow, DateTime.UtcNow);
        using var http = new HttpClient(new FailingHandler());
        var dropped = await trace.PostAsync(http, "http://127.0.0.1:1", TimeSpan.Zero, CancellationToken.None);
        Assert.StartsWith("launch spans dropped", dropped, StringComparison.Ordinal);
    }

    /// <summary>Dispatches a post straight into the route, as the channel does after its guard.</summary>
    private sealed class RouteHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(TelemetryRoutes.SpansPath, request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var response = TelemetryRoutes.Post(new ChannelRequest("POST", request.RequestUri.AbsolutePath, "", body, new Dictionary<string, string>()));
            Assert.Equal(200, response.Status);
            return new HttpResponseMessage((System.Net.HttpStatusCode)response.Status) { Content = new ByteArrayContent(response.Body) };
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
