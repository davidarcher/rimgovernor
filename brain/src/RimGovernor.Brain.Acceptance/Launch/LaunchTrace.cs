using System.Net;
using System.Text.Json;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The out-of-game side of the launch waterfall (#3287): the harness and the launcher time prepare, process start,
/// connect and the start in their own process, buffer the finished phases and post them to the game's brain
/// (<see cref="Route"/>, the brain's <c>TelemetryRoutes.SpansPath</c>), which owns the run's <c>traces.jsonl</c> and replays them as a
/// <c>launch</c> tree under its run id. A kept game's spans therefore join the live game's run. Spans never fail a
/// run: a game that cannot take them loses them, and <see cref="PostAsync"/> says why.
/// <para>
/// The harness makes one trace per session open (<see cref="Begin"/>) and the launch code it calls records into
/// <see cref="Current"/> through <see cref="Span"/>, which does nothing outside a traced open.
/// </para>
/// </summary>
public sealed class LaunchTrace
{
    /// <summary>The span attribute naming who recorded a phase: <c>harness</c> or <c>launcher</c>.</summary>
    public const string SourceAttribute = "rimgovernor.launch.source";

    /// <summary>The brain route the phases are posted to (<c>TelemetryRoutes.SpansPath</c>; this assembly does not reference PlayerApi).</summary>
    public const string Route = "/api/telemetry/spans";

    /// <summary>The longest a post waits for the brain to register its routes (it starts after the channel answers).</summary>
    public static readonly TimeSpan PostWait = TimeSpan.FromSeconds(30);

    private static readonly AsyncLocal<LaunchTrace?> Ambient = new();
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private readonly List<Phase> _done = [];

    public LaunchTrace(string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        Source = source;
    }

    /// <summary>Who records the phases (the <see cref="SourceAttribute"/> value).</summary>
    public string Source { get; }

    /// <summary>The traced open's channel directory, where the game announces its endpoint and writes its OTLP rings; empty until a launch or attach names it.</summary>
    public string Directory { get; set; } = "";

    /// <summary>The trace of the session open this async flow is in, if any.</summary>
    public static LaunchTrace? Current => Ambient.Value;

    /// <summary>Starts a trace for the calling async flow (and the calls it makes) and returns it.</summary>
    public static LaunchTrace Begin(string source)
    {
        var trace = new LaunchTrace(source);
        Ambient.Value = trace;
        return trace;
    }

    /// <summary>Times the enclosed code as <paramref name="name"/> in <see cref="Current"/>; nothing outside a traced flow.</summary>
    public static IDisposable Span(string name) => Current?.Time(name) ?? NoScope.Instance;

    /// <summary>Times the enclosed code as <paramref name="name"/>; dispose ends it.</summary>
    public IDisposable Time(string name) => new Scope(this, name, DateTime.UtcNow);

    /// <summary>Records one finished phase.</summary>
    public void Record(string name, DateTime startUtc, DateTime endUtc)
    {
        lock (_gate)
        {
            _done.Add(new Phase(name, startUtc, endUtc < startUtc ? startUtc : endUtc));
        }
    }

    /// <summary>
    /// Posts the recorded phases to the game at <paramref name="baseUrl"/> and forgets them. While the brain has not
    /// registered the route yet (404) or the channel does not answer, the post is retried for up to
    /// <paramref name="wait"/>. Returns null when the game took them (or there were none), else why they were dropped.
    /// </summary>
    public async Task<string?> PostAsync(HttpClient http, string baseUrl, TimeSpan wait, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(baseUrl);
        List<Phase> phases;
        lock (_gate)
        {
            phases = [.. _done];
            _done.Clear();
        }

        if (phases.Count == 0)
        {
            return null;
        }

        var body = Body(phases);
        var uri = new Uri(baseUrl.TrimEnd('/') + Route);
        var deadline = DateTime.UtcNow + wait;
        string problem;
        while (true)
        {
            try
            {
                using var content = new ByteArrayContent(body);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                using var response = await http.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return null;
                }

                problem = $"{Route} answered {(int)response.StatusCode}";
                if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    return $"launch spans dropped: {problem}";
                }
            }
            catch (HttpRequestException e)
            {
                problem = e.Message;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                problem = "no reply";
            }

            if (DateTime.UtcNow >= deadline)
            {
                return $"launch spans dropped: {problem}";
            }

            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private byte[] Body(List<Phase> phases)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartArray();
            foreach (var phase in phases)
            {
                w.WriteStartObject();
                w.WriteString("name", phase.Name);
                w.WriteString("parent", "");
                w.WriteString("startUtc", phase.StartUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                w.WriteString("endUtc", phase.EndUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                w.WriteStartObject("attributes");
                w.WriteString(SourceAttribute, Source);
                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        return stream.ToArray();
    }

    private sealed record Phase(string Name, DateTime StartUtc, DateTime EndUtc);

    private sealed class Scope(LaunchTrace trace, string name, DateTime startUtc) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            trace.Record(name, startUtc, DateTime.UtcNow);
        }
    }

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();

        public void Dispose()
        {
        }
    }
}
