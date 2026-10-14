using System.Text.Json;
using RimGovernor.Brain.Acceptance;

namespace RimGovernor.Launcher;

/// <summary>One decision or event row of the game's log ring, flattened for the Observe tab.</summary>
/// <param name="Seq">Numbers the rows the launcher has read, from 1; the page asks for what follows the last it holds.</param>
/// <param name="Detail">The row's fields (a decision's attrs, an event's fields) as JSON text, shown when the row is opened.</param>
public sealed record WatchRow(long Seq, string At, string Level, string Kind, string Component, long? Tick, string Verdict, string Reason, string Target, string Text, string Detail);

/// <summary>What the Observe tab polls: the brain's live reads and the log rows after <c>since</c>.</summary>
/// <param name="Problem">A plain line when the game or its channel is not there; empty otherwise.</param>
/// <param name="State"><c>GET /api/state</c> as the game answered it; null when it did not.</param>
/// <param name="Routines"><c>GET /api/routines</c>: stage, emergencies, runways and every concern's progress.</param>
/// <param name="Colony"><c>GET /api/player/colony</c>: the census.</param>
/// <param name="Next">The sequence of the newest row; pass it back as <c>since</c>.</param>
public sealed record ObserveView(
    string Problem,
    JsonElement? State,
    JsonElement? Routines,
    JsonElement? Colony,
    long Warns,
    long Errors,
    IReadOnlyList<WatchRow> Rows,
    long Next);

/// <summary>
/// Reads what the game's brain is doing: its three read routes over the local channel and its OTLP log ring
/// (<c>logs.jsonl</c>, the runtime log). Nothing here writes to the game or needs a backend.
/// </summary>
public sealed class ObserveService(HttpClient http, Func<CancellationToken, Task<string?>> address, Func<string?> logsPath)
{
    private const int KeepRows = 3000;
    private const int MaxPerPoll = 500;
    private const int DetailChars = 4000;

    private readonly Lock _mu = new();
    private readonly List<WatchRow> _rows = [];
    private LogTail? _tail;
    private string? _tailPath;
    private long _seq;
    private long _warns;
    private long _errors;

    public async Task<ObserveView> ViewAsync(long since, CancellationToken ct)
    {
        Pump();
        var baseAddress = await address(ct).ConfigureAwait(false);
        JsonElement? state = null, routines = null, colony = null;
        var problem = "";
        if (baseAddress is null)
        {
            problem = "The game is not running with the brain. Press Play on the Launch tab; the log below is whatever the last run left.";
        }
        else
        {
            var reads = await Task.WhenAll(
                GetAsync(baseAddress, "/api/state", ct),
                GetAsync(baseAddress, "/api/routines", ct),
                GetAsync(baseAddress, "/api/player/colony", ct)).ConfigureAwait(false);
            (state, routines, colony) = (reads[0], reads[1], reads[2]);
        }

        lock (_mu)
        {
            var fresh = since <= 0 ? _rows.TakeLast(MaxPerPoll) : _rows.Where(r => r.Seq > since).Take(MaxPerPoll);
            var rows = fresh.ToList();
            return new ObserveView(problem, state, routines, colony, _warns, _errors, rows, rows.Count > 0 ? rows[^1].Seq : Math.Max(since, 0));
        }
    }

    /// <summary>One GET against the channel; null for any failure (a route family absent or the game busy), so the page shows that panel as unknown.</summary>
    private async Task<JsonElement?> GetAsync(string baseAddress, string route, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var response = await http.GetAsync(new Uri(baseAddress + route), timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), cancellationToken: timeout.Token).ConfigureAwait(false);
            return doc.RootElement.Clone();
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    // Reads the rows appended since the last poll. The first read of a path starts at its end: what an earlier run wrote is
    // history (the Log filter shows it only once the game is running again), and a game that has not written yet starts at 0.
    private void Pump()
    {
        var path = logsPath();
        if (path is null)
        {
            return;
        }

        lock (_mu)
        {
            if (_tail is null || _tailPath != path)
            {
                _tail = LogTail.Create(path);
                _tailPath = path;
                return;
            }

            foreach (var row in _tail.Next())
            {
                var level = row.ContextString("level");
                _warns += level == "WARN" ? 1 : 0;
                _errors += level == "ERROR" ? 1 : 0;
                _rows.Add(Flatten(row, ++_seq));
            }

            if (_rows.Count > KeepRows)
            {
                _rows.RemoveRange(0, _rows.Count - KeepRows);
            }
        }
    }

    private static WatchRow Flatten(LogRow row, long seq)
    {
        var fields = row.Fields();
        string Text(string key) => fields.TryGetValue(key, out var v) && v is string s ? s : "";
        var detail = JsonSerializer.Serialize(fields, LauncherJson.Indented);
        return new WatchRow(
            seq,
            row.ContextString("at"),
            row.ContextString("level"),
            row.Kind,
            row.ContextString("component"),
            row.HasTick ? row.Tick : null,
            Text("verdict"),
            Text("reason"),
            Text("target"),
            Text("msg"),
            detail.Length > DetailChars ? detail[..DetailChars] + "\n..." : detail);
    }
}
