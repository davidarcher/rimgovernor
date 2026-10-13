using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Acceptance;

namespace RimGovernor.Brain.RemoteAccept;

/// <summary>The trusted live-progress plan of one shard (Go <c>progressPlan</c>).</summary>
internal sealed record ProgressPlan(
    string Shard,
    string RunId,
    string Attempt,
    string HeadSha,
    string TestedCommit,
    IReadOnlyList<string> Cases,
    IReadOnlyDictionary<string, string> Outputs)
{
    public string ExternalId => RunId + ":" + Attempt + ":" + Shard;

    public static ProgressPlan Parse(JsonObject o)
    {
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (StrictJson.LooseObj(o, "outputs") is { } map)
        {
            foreach (var (key, value) in map)
            {
                outputs[key] = StrictJson.ElementStr(value);
            }
        }

        return new ProgressPlan(
            StrictJson.LooseStr(o, "shard"),
            StrictJson.LooseStr(o, "run_id"),
            StrictJson.LooseStr(o, "attempt"),
            StrictJson.LooseStr(o, "head_sha"),
            StrictJson.LooseStr(o, "tested_commit"),
            [.. StrictJson.LooseArr(o, "cases").Select(StrictJson.ElementStr)],
            outputs);
    }

    public byte[] ToJson() => StrictJson.Compact(w =>
    {
        w.WriteStartObject();
        w.WriteString("shard", Shard);
        w.WriteString("run_id", RunId);
        w.WriteString("attempt", Attempt);
        w.WriteString("head_sha", HeadSha);
        w.WriteString("tested_commit", TestedCommit);
        w.WritePropertyName("cases");
        w.WriteStartArray();
        foreach (var c in Cases)
        {
            w.WriteStringValue(c);
        }

        w.WriteEndArray();
        w.WritePropertyName("outputs");
        w.WriteStartObject();
        foreach (var key in Outputs.Keys.Order(StringComparer.Ordinal))
        {
            w.WriteString(key, Outputs[key]);
        }

        w.WriteEndObject();
        w.WriteEndObject();
    });
}

/// <summary>One case's observed state; terminal states are latched.</summary>
internal sealed class ProgressState
{
    public string Status { get; set; } = "";

    public DateTimeOffset Since { get; set; }

    public TimeSpan Wall { get; set; }
}

/// <summary>The title/summary/text triple of a check run's output.</summary>
internal sealed record CheckOutput(string Title, string Summary, string Text);

/// <summary>A check run as GitHub returns it.</summary>
internal sealed record CheckRun(long Id, string ExternalId, string Status, CheckOutput Output)
{
    public static CheckRun Parse(JsonObject o)
    {
        var output = StrictJson.LooseObj(o, "output");
        return new CheckRun(
            StrictJson.LooseInt64(o, "id"),
            StrictJson.LooseStr(o, "external_id"),
            StrictJson.LooseStr(o, "status"),
            output is null
                ? new CheckOutput("", "", "")
                : new CheckOutput(StrictJson.LooseStr(output, "title"), StrictJson.LooseStr(output, "summary"), StrictJson.LooseStr(output, "text")));
    }
}

/// <summary>
/// Live progress as a GitHub check run (Go cmd/remoteaccept progress.go). Display only: API, startup and observation
/// failures never change verdicts. Rendering uses only trusted names/identity and a fixed vocabulary; no native error,
/// argv, log content or path can enter a public check run.
/// </summary>
internal sealed class ProgressApi(HttpClient client, string baseUrl, string repo, string token)
{
    private const long MaxProgressFile = 16L << 20;

    /// <summary>The check-run name and output for the current states.</summary>
    public static (string Name, CheckOutput Output) Render(ProgressPlan p, IReadOnlyList<ProgressState> states, bool complete)
    {
        int passed = 0, failed = 0, done = 0;
        var running = "queued";
        var table = new StringBuilder("| Case | Status | Wall |\n|---|---|---|\n");
        for (var i = 0; i < p.Cases.Count; i++)
        {
            var name = p.Cases[i];
            var s = states[i];
            var status = "⏳ queued";
            var wall = "";
            switch (s.Status)
            {
                case "running":
                    status = "▶ running (since " + s.Since.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture) + "Z)";
                    running = name;
                    break;
                case "passed":
                    status = "✅ passed";
                    passed++;
                    done++;
                    break;
                case "failed":
                    status = "❌ failed";
                    failed++;
                    done++;
                    break;
            }

            if (s.Status is "passed" or "failed")
            {
                wall = FormatWall(s.Wall);
            }
            else if (complete)
            {
                status = "❌ missing result";
                failed++;
            }

            table.Append(CultureInfo.InvariantCulture, $"| {name} | {status} | {wall} |\n");
        }

        var title = string.Create(CultureInfo.InvariantCulture, $"acceptance-{p.Shard} · {done}/{p.Cases.Count} · {running}");
        if (complete)
        {
            title = failed > 0
                ? string.Create(CultureInfo.InvariantCulture, $"acceptance-{p.Shard} · {failed} failed")
                : string.Create(CultureInfo.InvariantCulture, $"acceptance-{p.Shard} · {passed}/{p.Cases.Count} passed");
        }

        return (title, new CheckOutput(title, "Tested commit: `" + p.TestedCommit + "`. Display only; aggregate owns the verdict.", table.ToString()));
    }

    /// <summary>
    /// Terminal states are latched. A partial JSON write is retried next poll, and reports are bounded before decoding.
    /// Only <c>passed</c> and <c>wall_ms</c> are projected.
    /// </summary>
    public static async Task ObserveAsync(ProgressPlan p, IReadOnlyList<ProgressState> states, DateTimeOffset now, Func<Task> transition)
    {
        for (var i = 0; i < p.Cases.Count; i++)
        {
            var name = p.Cases[i];
            var s = states[i];
            if (s.Status is "passed" or "failed")
            {
                continue;
            }

            var path = p.Outputs[name];
            if (s.Status.Length == 0 && File.Exists(Path.GetDirectoryName(path) + ".log"))
            {
                s.Status = "running";
                s.Since = now;
                await transition().ConfigureAwait(false);
            }

            bool? passed;
            long wallMs;
            try
            {
                var report = ReadProgressJson(path);
                passed = report.TryGetPropertyValue("passed", out var node) && node is not null ? ReadBool(node) : null;
                wallMs = StrictJson.LooseInt64(report, "wall_ms");
            }
            catch (Exception e) when (e is RemoteAcceptException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (passed is null)
            {
                continue;
            }

            s.Status = passed.Value ? "passed" : "failed";

            // Numeric input cannot overflow a TimeSpan or yield negative walls.
            s.Wall = TimeSpan.FromMilliseconds(Math.Min(Math.Max(wallMs, 0), (long)TimeSpan.FromHours(24).TotalMilliseconds));
            await transition().ConfigureAwait(false);
        }
    }

    /// <summary>Reads a progress JSON file: a regular file of at most 16 MiB.</summary>
    public static JsonObject ReadProgressJson(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 || file.Length > MaxProgressFile)
        {
            throw new RemoteAcceptException("invalid progress file");
        }

        using var buffer = new MemoryStream();
        var limited = new byte[1 << 16];
        int n;
        while ((n = file.Read(limited, 0, limited.Length)) > 0)
        {
            buffer.Write(limited, 0, n);
            if (buffer.Length > MaxProgressFile)
            {
                throw new RemoteAcceptException("oversize progress file");
            }
        }

        return StrictJson.ParseLoose(buffer.ToArray());
    }

    /// <summary>Creates the check run, then polls the planned case files until <paramref name="stopFile"/> appears and completes it.</summary>
    public async Task WatchAsync(ProgressPlan p, string idFile, string stopFile, CancellationToken cancellationToken)
    {
        var states = p.Cases.Select(static _ => new ProgressState()).ToList();
        var (name, output) = Render(p, states, complete: false);
        var created = await RequestAsync(HttpMethod.Post, "/check-runs", CheckBody(name, p.HeadSha, p.ExternalId, "in_progress", "", output), expectBody: true, cancellationToken).ConfigureAwait(false);
        var id = created is null ? 0 : StrictJson.LooseInt64(created, "id");
        if (id <= 0)
        {
            throw new RemoteAcceptException("missing check id");
        }

        await File.WriteAllTextAsync(idFile, id.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        var path = $"/check-runs/{id.ToString(CultureInfo.InvariantCulture)}";
        async Task UpdateAsync()
        {
            var (n, o) = Render(p, states, complete: false);
            await RequestAsync(new HttpMethod("PATCH"), path, CheckBody(n, "", "", "", "", o), expectBody: false, cancellationToken).ConfigureAwait(false);
        }

        using var ticks = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (true)
        {
            await ObserveAsync(p, states, DateTimeOffset.UtcNow, UpdateAsync).ConfigureAwait(false);
            if (File.Exists(stopFile))
            {
                // The suite may have written its last result during the first scan.
                await ObserveAsync(p, states, DateTimeOffset.UtcNow, UpdateAsync).ConfigureAwait(false);
                var (finalName, finalOutput) = Render(p, states, complete: true);
                var conclusion = states.All(static s => s.Status == "passed") ? "success" : "failure";
                await RequestAsync(new HttpMethod("PATCH"), path, CheckBody(finalName, "", "", "completed", conclusion, finalOutput), expectBody: false, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!await ticks.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new RemoteAcceptException("progress watch interrupted");
            }
        }
    }

    /// <summary>Adds the uploaded-artifact link to the shard's check run.</summary>
    public async Task FinishAsync(string idFile, string externalId, string artifact, CancellationToken cancellationToken)
    {
        if (artifact.Length == 0)
        {
            return;
        }

        if (!artifact.StartsWith("https://github.com/" + repo + "/actions/runs/", StringComparison.Ordinal)
            || artifact.IndexOfAny(['\r', '\n', '(', ')', '<', '>', ' ']) >= 0
            || !Uri.TryCreate(artifact, UriKind.Absolute, out _))
        {
            throw new RemoteAcceptException("invalid artifact URL");
        }

        var text = await File.ReadAllTextAsync(idFile, cancellationToken).ConfigureAwait(false);
        if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            throw new RemoteAcceptException("invalid check id");
        }

        var path = $"/check-runs/{id.ToString(CultureInfo.InvariantCulture)}";
        var body = await RequestAsync(HttpMethod.Get, path, null, expectBody: true, cancellationToken).ConfigureAwait(false);
        var check = CheckRun.Parse(body ?? new JsonObject());
        if (check.ExternalId != externalId)
        {
            throw new RemoteAcceptException("wrong check identity");
        }

        var output = check.Output with { Summary = check.Output.Summary + "\n\n[Shard diagnostics](" + artifact + ")" };
        await RequestAsync(new HttpMethod("PATCH"), path, OutputBody(output), expectBody: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cancels the abandoned in-progress shard checks of this run attempt.</summary>
    public async Task SweepAsync(string head, string prefix, CancellationToken cancellationToken)
    {
        for (var page = 1; ; page++)
        {
            var path = $"/commits/{head}/check-runs?filter=all&per_page=100&page={page.ToString(CultureInfo.InvariantCulture)}";
            var result = await RequestAsync(HttpMethod.Get, path, null, expectBody: true, cancellationToken).ConfigureAwait(false) ?? new JsonObject();
            var checks = StrictJson.LooseArr(result, "check_runs").Select(n => CheckRun.Parse(StrictJson.ElementObj(n))).ToList();
            foreach (var check in checks)
            {
                if (check.Status != "in_progress" || !check.ExternalId.StartsWith(prefix, StringComparison.Ordinal) || !EvidenceRules.IsShardId(check.ExternalId[prefix.Length..]))
                {
                    continue;
                }

                var output = check.Output with { Summary = check.Output.Summary + "\n\nShard ended without completing live progress (cancelled, timed out or observer unavailable)." };
                var body = StrictJson.Compact(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("status", "completed");
                    w.WriteString("conclusion", "cancelled");
                    w.WritePropertyName("output");
                    WriteOutput(w, output);
                    w.WriteEndObject();
                });
                await RequestAsync(new HttpMethod("PATCH"), $"/check-runs/{check.Id.ToString(CultureInfo.InvariantCulture)}", body, expectBody: false, cancellationToken).ConfigureAwait(false);
            }

            if (checks.Count < 100)
            {
                return;
            }
        }
    }

    private static bool ReadBool(JsonNode node) =>
        node is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>() : throw new RemoteAcceptException("expected a boolean");

    private static string FormatWall(TimeSpan wall)
    {
        var seconds = (long)Math.Floor((wall.TotalMilliseconds + 500) / 1000);
        var h = seconds / 3600;
        var m = seconds % 3600 / 60;
        var s = seconds % 60;
        var inv = CultureInfo.InvariantCulture;
        return h > 0 ? string.Create(inv, $"{h}h{m}m{s}s") : m > 0 ? string.Create(inv, $"{m}m{s}s") : string.Create(inv, $"{s}s");
    }

    private static void WriteOutput(Utf8JsonWriter w, CheckOutput o)
    {
        w.WriteStartObject();
        w.WriteString("title", o.Title);
        w.WriteString("summary", o.Summary);
        if (o.Text.Length > 0)
        {
            w.WriteString("text", o.Text);
        }

        w.WriteEndObject();
    }

    private static byte[] OutputBody(CheckOutput output) => StrictJson.Compact(w =>
    {
        w.WriteStartObject();
        w.WritePropertyName("output");
        WriteOutput(w, output);
        w.WriteEndObject();
    });

    private static byte[] CheckBody(string name, string head, string externalId, string status, string conclusion, CheckOutput output) => StrictJson.Compact(w =>
    {
        w.WriteStartObject();
        WriteIfSet(w, "name", name);
        WriteIfSet(w, "head_sha", head);
        WriteIfSet(w, "external_id", externalId);
        WriteIfSet(w, "status", status);
        WriteIfSet(w, "conclusion", conclusion);
        w.WritePropertyName("output");
        WriteOutput(w, output);
        w.WriteEndObject();
    });

    private static void WriteIfSet(Utf8JsonWriter w, string key, string value)
    {
        if (value.Length > 0)
        {
            w.WriteString(key, value);
        }
    }

    private async Task<JsonObject?> RequestAsync(HttpMethod method, string path, byte[]? body, bool expectBody, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, baseUrl + "/repos/" + repo + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new RemoteAcceptException("progress API unavailable", e);
        }

        using (response)
        {
            if ((int)response.StatusCode is < 200 or >= 300)
            {
                throw new RemoteAcceptException($"progress API status {(int)response.StatusCode}");
            }

            if (!expectBody)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[1 << 16];
            int n;
            while ((n = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, n);
                if (buffer.Length > 8L << 20)
                {
                    break;
                }
            }

            return StrictJson.ParseLoose(buffer.ToArray());
        }
    }
}
