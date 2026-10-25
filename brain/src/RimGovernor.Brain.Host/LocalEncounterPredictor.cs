using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Host;

/// <summary>
/// One owned isolated process, one active request, no queue or retry. Runtime plumbing only. The worker announces
/// in its ready frame how many requests one process answers; this owner counts the requests it writes and replaces
/// the worker before the one that would exceed that budget, so that request pays a reported cold start instead of
/// being refused (#3524).
/// </summary>
public sealed class LocalEncounterPredictor(LocalEncounterOptions options) : IEncounterPredictor, IAsyncDisposable
{
    private readonly LocalEncounterOptions _settings = CheckOptions(options);
    private readonly SemaphoreSlim _active = new(1, 1);
    private Process? _process;
    private Task? _drain;
    private string _engine = "";
    private int _arenaBudget;
    private int _written;
    private bool _disposed;
    private string _cleanupFailure = "";
    private readonly CancellationTokenSource _lifetime = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<EncounterPrediction> PredictAsync(EncounterRequest request, CancellationToken cancellationToken)
    {
        var key = EncounterPredictionConsumer.Fingerprint(request);
        if (cancellationToken.IsCancellationRequested) return EncounterPredictionConsumer.Refused(EncounterStatus.Cancelled, key, "request_cancelled");
        if (!_active.Wait(0, CancellationToken.None))
            return EncounterPredictionConsumer.Refused(EncounterStatus.Busy, key, "worker_busy");
        double? cold = null;
        Stopwatch? startup = null;
        Stopwatch? watch = null;
        try
        {
            if (_disposed || _cleanupFailure.Length != 0) return EncounterPredictionConsumer.Refused(EncounterStatus.Unavailable, key, _disposed ? "worker_disposed" : _cleanupFailure);
            var problem = Validate(request);
            if (problem.Length != 0) return EncounterPredictionConsumer.Refused(EncounterStatus.Unsupported, key, problem);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            if (_process is not null && _written >= _arenaBudget)
            {
                // The worker's budget is spent: retire it now so this request is answered by a fresh process. The
                // retirement is part of the cold start this request reports.
                startup = Stopwatch.StartNew();
                await StopAsync().ConfigureAwait(false);
                if (_cleanupFailure.Length != 0) return EncounterPredictionConsumer.Refused(EncounterStatus.Unavailable, key, _cleanupFailure);
            }
            if (_process is null)
            {
                budget.CancelAfter(_settings.ColdStartBudget);
                startup ??= Stopwatch.StartNew();
                await StartAsync(budget.Token).ConfigureAwait(false);
                startup.Stop();
                cold = startup.Elapsed.TotalMilliseconds;
            }
            budget.CancelAfter(_settings.RequestBudget);
            watch = Stopwatch.StartNew();
            var payload = Encode(request, key);
            if (payload.Length > _settings.MaximumFrameCharacters)
                return EncounterPredictionConsumer.Refused(EncounterStatus.Unsupported, key, "request_frame_budget");
            _written++;
            await _process!.StandardInput.WriteLineAsync(payload.AsMemory(), budget.Token).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(budget.Token).ConfigureAwait(false);
            var line = await ReadFrameAsync(_process.StandardOutput, _settings.MaximumFrameCharacters, budget.Token).ConfigureAwait(false);
            var reply = Decode(line, request, key);
            return reply with { EngineFingerprint = _engine, ColdStartMilliseconds = Measured(cold), RequestMilliseconds = Measured(watch.Elapsed.TotalMilliseconds) };
        }
        catch (OperationCanceledException)
        {
            startup?.Stop(); watch?.Stop();
            await StopAsync().ConfigureAwait(false);
            if (_cleanupFailure.Length != 0) return EncounterPredictionConsumer.Refused(EncounterStatus.Unavailable, key, _cleanupFailure);
            return EncounterPredictionConsumer.Refused(cancellationToken.IsCancellationRequested ? EncounterStatus.Cancelled : EncounterStatus.TimedOut, key, watch is null ? "worker_startup_cancelled" : "worker_request_cancelled")
                with { ColdStartMilliseconds = Measured(cold ?? startup?.Elapsed.TotalMilliseconds), RequestMilliseconds = Measured(watch?.Elapsed.TotalMilliseconds) };
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException or KeyNotFoundException or FormatException)
        {
            startup?.Stop(); watch?.Stop();
            await StopAsync().ConfigureAwait(false);
            if (_cleanupFailure.Length != 0) return EncounterPredictionConsumer.Refused(EncounterStatus.Unavailable, key, _cleanupFailure);
            return EncounterPredictionConsumer.Refused(e is JsonException or KeyNotFoundException or FormatException ? EncounterStatus.Malformed : EncounterStatus.Unavailable, key, e.Message)
                with { ColdStartMilliseconds = Measured(cold ?? startup?.Elapsed.TotalMilliseconds), RequestMilliseconds = Measured(watch?.Elapsed.TotalMilliseconds) };
        }
        finally { _active.Release(); }
    }

    private static LocalEncounterOptions CheckOptions(LocalEncounterOptions value)
    {
        if (string.IsNullOrWhiteSpace(value.Mods) || string.IsNullOrWhiteSpace(value.PredictorMods) || value.ColdStartBudget <= TimeSpan.Zero || value.ColdStartBudget > TimeSpan.FromMinutes(1) ||
            value.RequestBudget <= TimeSpan.Zero || value.RequestBudget > TimeSpan.FromMinutes(1) ||
            value.CleanupBudget <= TimeSpan.Zero || value.CleanupBudget > TimeSpan.FromSeconds(5) ||
            value.MaximumSamples is < 1 or > 64 || value.MaximumTicks is < 1 or > GameTime.TicksPerDay ||
            value.MaximumMapSize is < 1 or > 128 || value.MaximumActors is < 1 or > 32 ||
            value.MaximumFrameCharacters is < 1 or > 1048576)
            throw new ArgumentOutOfRangeException(nameof(value), "prediction effort budgets must be positive and bounded");
        return value;
    }

    private string Validate(EncounterRequest r)
    {
        if (r.Identity.World.Length == 0 || r.Identity.GameBuild != _settings.GameBuild || r.Identity.Mods != _settings.Mods)
            return "observation_build_or_mods_mismatch";
        if (r.Identity.Tactic != "native-hunt/timed-departure") return "unsupported_tactic";
        if (r.UnsupportedObservations.Count != 0) return "unrepresented_observation:" + string.Join(',', r.UnsupportedObservations);
        if (r.HorizonTicks <= 0 || r.HorizonTicks > _settings.MaximumTicks || r.Seeds.Count == 0 || r.Seeds.Count > _settings.MaximumSamples || r.Seeds.Distinct().Count() != r.Seeds.Count || string.IsNullOrWhiteSpace(r.SamplingReason)) return "sample_or_tick_budget";
        if (r.Width <= 0 || r.Height <= 0 || r.Width > _settings.MaximumMapSize || r.Height > _settings.MaximumMapSize || r.Terrain.Length == 0 || r.Weather.Length == 0) return "map_or_environment";
        if (r.Environment is { } environment && (!double.IsFinite(environment.OutdoorTemperature) || !double.IsFinite(environment.SkyGlow) || environment.SkyGlow is < 0 or > 1)) return "environment_malformed";
        if (r.Actors.Count == 0 || r.Actors.Count > _settings.MaximumActors || r.Actors.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != r.Actors.Count) return "participants";
        foreach (var a in r.Actors)
        {
            if (a.Id.Length == 0 || a.Kind.Length == 0 || a.X < 0 || a.Z < 0 || a.X >= r.Width || a.Z >= r.Height) return "participant_identity_or_cell";
            if (!double.IsFinite(a.Age) || a.Health.Any(h => !double.IsFinite(h.Severity) || h.Severity <= 0)) return "participant_health_malformed";
            if (a.Departure is { } d && (d.Tick < 0 || d.Tick > r.HorizonTicks || !Enum.IsDefined(d.Kind))) return "participant_availability";
            if (!a.Wild && (a.Age <= 0 || a.Xenotype.Length == 0 || a.HuntTarget.Length == 0 || !r.Actors.Any(p => p.Wild && p.Id == a.HuntTarget))) return "hunter_observations";
            if (a.Weapon is { } w && (w.HitPoints <= 0 || w.Quality.Length == 0)) return "weapon_condition_unknown";
            if (a.Apparel.Any(w => w.HitPoints <= 0 || w.Quality.Length == 0)) return "apparel_condition_unknown";
        }
        return "";
    }

    private async Task StartAsync(CancellationToken ct)
    {
        var start = new ProcessStartInfo(_settings.DotnetExecutable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = _settings.WorkingDirectory,
        };
        start.ArgumentList.Add(_settings.WorkerAssembly);
        start.ArgumentList.Add("--game"); start.ArgumentList.Add(_settings.PrivateGameDirectory);
        start.ArgumentList.Add("--worker"); start.ArgumentList.Add("stdio");
        _process = Process.Start(start) ?? throw new IOException("worker_start_failed");
        _drain = DrainAsync(_process.StandardError);
        using var ready = JsonDocument.Parse(await ReadFrameAsync(_process.StandardOutput, _settings.MaximumFrameCharacters, ct).ConfigureAwait(false));
        var root = ready.RootElement;
        if (root.GetProperty("kind").GetString() != "ready" || root.GetProperty("gameBuild").GetString() != _settings.GameBuild || root.GetProperty("mods").GetString() != _settings.PredictorMods)
            throw new IOException("worker_build_or_mods_mismatch");
        _arenaBudget = root.GetProperty("arenaBudget").GetInt32();
        if (_arenaBudget < 1) throw new JsonException("worker_arena_budget");
        _written = 0;
#pragma warning disable IL3000 // Empty inside a single-file host (rg.exe): the process file then is the adapter assembly.
        var adapterPath = typeof(LocalEncounterPredictor).Assembly.Location;
#pragma warning restore IL3000
        using var adapterAssembly = File.OpenRead(adapterPath.Length > 0 ? adapterPath : Environment.ProcessPath ?? throw new IOException("adapter_path_unknown"));
        _engine = (root.GetProperty("engine").GetString() ?? throw new JsonException("engine identity missing")) + ":mods=" + _settings.PredictorMods + ":" + Convert.ToHexString(SHA256.HashData(adapterAssembly));
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer).ConfigureAwait(false) != 0) { /* Drain without accumulating game logs in planner memory. */ }
    }

    private async Task StopAsync()
    {
        var process = _process;
        if (process is null) return;
        using var budget = new CancellationTokenSource(_settings.CleanupBudget);
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
            if (_drain is not null) await _drain.WaitAsync(budget.Token).ConfigureAwait(false);
            process.Dispose(); _process = null; _drain = null; _cleanupFailure = "";
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Retain the exact owned handle and refuse new work. Never lose a possibly running process.
            _cleanupFailure = "owned_worker_cleanup_failed:" + process.Id + ":" + e.GetType().Name;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _lifetime.Cancel();
        if (!await _active.WaitAsync(_settings.CleanupBudget).ConfigureAwait(false))
            throw new TimeoutException("owned_worker_dispose_wait_timeout");
        try
        {
            await StopAsync().ConfigureAwait(false);
            if (_cleanupFailure.Length != 0) throw new IOException(_cleanupFailure);
        }
        finally { _active.Release(); }
    }
    private static async Task<string> ReadFrameAsync(StreamReader reader, int maximum, CancellationToken ct)
    {
        var text = new StringBuilder();
        var c = new char[1];
        while (await reader.ReadAsync(c.AsMemory(), ct).ConfigureAwait(false) != 0)
        {
            if (c[0] == '\n') return text.ToString();
            if (text.Length == maximum) throw new JsonException("worker_frame_budget");
            text.Append(c[0]);
        }
        throw new IOException("worker_closed");
    }

    private static string Encode(EncounterRequest r, string key) => JsonSerializer.Serialize(new
    {
        schema = 3, name = key, width = r.Width, height = r.Height, terrain = r.Terrain, weather = r.Weather, environment = r.Environment,
        ground = r.Ground.Select(g => new { g.MinX, g.MinZ, g.MaxX, g.MaxZ, g.Terrain, g.Roof }),
        things = r.Obstacles.Select(t => new { def = t.Definition, t.Stuff, t.X, t.Z }),
        pawns = r.Actors.Select(a => new
        {
            a.Id, side = a.Wild ? "wild" : "colonist", a.Kind, a.X, a.Z, a.Age, a.Xenotype, a.Herd, hunt = a.HuntTarget,
            weapon = a.Weapon?.Definition ?? "", weaponStuff = a.Weapon?.Stuff ?? "", weaponQuality = a.Weapon?.Quality ?? "", weaponHitPoints = a.Weapon?.HitPoints ?? 0,
            worn = a.Apparel.Select(w => new { def = w.Definition, w.Stuff, w.Quality, w.HitPoints }), a.Skills,
            traits = a.Traits.Select(t => new { def = t.Definition, t.Degree }),
            hediffs = a.Health.Select(h => new { def = h.Definition, h.Part, h.Severity }), missing = a.MissingParts,
        }),
        departures = r.Actors.Where(a => a.Departure is not null).Select(a => new { a.Id, a.Departure!.Tick, kind = a.Departure.Kind == EncounterDepartureKind.Leave ? "leave" : "flee" }),
        seeds = r.Seeds, maxTicks = r.HorizonTicks, verify = false, // replay is the separate retained native qualification check
    }, Json);

    private static EncounterPrediction Decode(string line, EncounterRequest request, string key)
    {
        using var document = JsonDocument.Parse(line);
        var envelope = document.RootElement;
        if (envelope.GetProperty("name").GetString() != key) throw new JsonException("reply_identity");
        if (envelope.GetProperty("kind").GetString() == "rejected")
            return EncounterPredictionConsumer.Refused(EncounterStatus.Unsupported, key, envelope.GetProperty("reason").GetString() ?? "worker_rejected");
        if (envelope.GetProperty("kind").GetString() != "result") throw new IOException(envelope.GetProperty("reason").GetString() ?? "worker_failed");
        var result = envelope.GetProperty("result");
        if (result.GetProperty("schema").GetInt32() != 3 || result.GetProperty("name").GetString() != key) throw new JsonException("reply_schema_or_identity");
        List<EncounterSample> samples = [];
        foreach (var sample in result.GetProperty("outcomes").EnumerateArray())
        {
            if (sample.GetProperty("nondeterministic").GetBoolean()) return EncounterPredictionConsumer.Refused(EncounterStatus.Unavailable, key, "nondeterministic_replay");
            List<EncounterActorOutcome> actors = [];
            foreach (var a in sample.GetProperty("pawns").EnumerateArray())
            {
                var products = a.GetProperty("products");
                actors.Add(new EncounterActorOutcome(a.GetProperty("id").GetString()!, a.GetProperty("downed").GetBoolean(), a.GetProperty("dead").GetBoolean(), a.GetProperty("healthPercent").GetDouble(),
                    a.GetProperty("bleedRate").GetDouble(), a.GetProperty("needsTend").GetBoolean(),
                    a.GetProperty("injuries").EnumerateArray().Select(i => new EncounterInjury(i.GetProperty("def").GetString()!, i.GetProperty("part").GetString()!, i.GetProperty("severity").GetDouble(), i.GetProperty("bleedRate").GetDouble(), i.GetProperty("tendableNow").GetBoolean(), i.GetProperty("tended").GetBoolean(), i.GetProperty("permanent").GetBoolean())).ToArray(),
                    Strings(a.GetProperty("missing")), Strings(a.GetProperty("kills")), products.ValueKind == JsonValueKind.Null ? null : new EncounterProducts(products.GetProperty("meatDef").GetString()!, products.GetProperty("meat").GetDouble(), products.GetProperty("leatherDef").GetString()!, products.GetProperty("leather").GetDouble()),
                    Tick(a, "firstAttackTick"), Tick(a, "lastShotTick"), Tick(a, "departureTick"), Tick(a, "withdrawalTick")));
            }
            var ticks = sample.GetProperty("ticks").GetInt32();
            if (ticks < 0 || ticks > request.HorizonTicks || !actors.Select(a => a.Id).Order(StringComparer.Ordinal).SequenceEqual(request.Actors.Select(a => a.Id).Order(StringComparer.Ordinal))) throw new JsonException("reply_participants_or_duration");
            foreach (var a in actors)
                if (request.Actors.Single(p => p.Id == a.Id).Departure is { } d && a.LastShotTick is { } shot && shot.Tick >= d.Tick) throw new JsonException("combat_after_departure");
            samples.Add(new EncounterSample(sample.GetProperty("seed").GetInt32(), ticks, ticks == request.HorizonTicks, actors));
        }
        if (!samples.Select(s => s.Seed).SequenceEqual(request.Seeds)) throw new JsonException("reply_samples");
        return new EncounterPrediction
        {
            Status = EncounterStatus.Evidence, RequestFingerprint = key, Reason = "unqualified_empirical_samples", Samples = samples,
            ModelUncertainty = ["held_out_accuracy_unqualified", "false_safe_frequency_unknown", "revenge_and_withdrawal_coverage_unqualified", "needs_time_of_day_and_environment_changes_unrepresented", "injury_recovery_and_downstream_work_not_estimated"],
        };
    }

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString() ?? throw new JsonException("null string")).ToArray();
    private static EncounterTick? Tick(JsonElement value, string property) => value.GetProperty(property).GetInt32() is var tick && tick >= 0 ? new EncounterTick(tick) : null;

    private static MeasuredMilliseconds? Measured(double? milliseconds) => milliseconds is { } value ? new MeasuredMilliseconds(value) : null;
}

public sealed record LocalEncounterOptions(string DotnetExecutable, string WorkerAssembly, string PrivateGameDirectory,
    string WorkingDirectory, string GameBuild, string Mods, string PredictorMods, TimeSpan ColdStartBudget, TimeSpan RequestBudget, TimeSpan CleanupBudget,
    int MaximumSamples, int MaximumTicks, int MaximumMapSize, int MaximumActors, int MaximumFrameCharacters);
