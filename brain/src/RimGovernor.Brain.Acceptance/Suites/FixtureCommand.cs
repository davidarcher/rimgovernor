using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The fixture subcommand's parsed flags: the op, its arguments and the world it runs on (Go <c>fixtureOptions</c>).</summary>
public sealed record FixtureOptions
{
    public string Op { get; init; } = "";

    public JsonObject Args { get; init; } = [];

    /// <summary>The save the op runs on (the committed tribal8 baseline by default).</summary>
    public string Save { get; init; } = "";

    /// <summary>Runs the op on whatever an earlier call left loaded instead of loading <see cref="Save"/>.</summary>
    public bool Loaded { get; init; }

    public string Root { get; init; } = "";

    public string Output { get; init; } = "";

    public string GameId { get; init; } = "";

    public bool Headless { get; init; }

    public TimeSpan Timeout { get; init; }

    public bool Json { get; init; }

    /// <summary>Loads a save whose recorded mods are not all active (a CI bundle may record mods since retired from the build).</summary>
    public bool IgnoreMods { get; init; }
}

/// <summary>
/// <c>acceptance fixture</c> (Go <c>cmd/acceptance/fixture.go</c>): opens the root's game (a kept process is attached, else
/// one launches), loads the save (or keeps the loaded world under <c>-loaded</c>), calls the op through the same harness
/// the cases use and prints its reply with a census of the world after it. The game stays loaded and paused with the
/// process kept, so the next call (<c>-loaded</c>) continues on it; the next <c>acceptance run</c> unloads it as it would
/// any leftover. The exit code is 1 when the call failed or native refused the op.
/// </summary>
public static class FixtureCommand
{
    /// <summary>The usage line for the command's help text.</summary>
    public const string Usage = "  acceptance fixture <op> [key=value ...] -root <dir> [-save <name> | -loaded] [-output <dir> -game <id> -headless=false -timeout <d> -json]";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Runs the subcommand; returns the process exit code (2 refused, 1 a failed call or a refused op).</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        FixtureOptions o;
        try
        {
            o = Parse(args);
        }
        catch (AcceptanceException e)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 2;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(o.Timeout);
        var result = new FixtureRun(o.Op, o.Args, Path.Combine(o.Output, o.Op.Replace('/', '_') + "-" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture)));
        try
        {
            await ExecuteAsync(o, result, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            result.Error = e.Message;
        }

        await PrintAsync(stdout, o.Json, result).ConfigureAwait(false);
        return result.Error is not null || result.Success != true ? 1 : 0;
    }

    /// <summary>
    /// Resolves the fixture subcommand: the op name, its key=value arguments (a value that parses as JSON is that value, so
    /// x=12 is a number, roofed=true a bool and cells=[[1,2]] a list; anything else is a string), then the flags. Arguments
    /// precede the flags the way case names precede run's.
    /// </summary>
    public static FixtureOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var fs = new FlagSet("fixture");
        var root = fs.String("root", "");
        var save = fs.String("save", Baselines.BaselineName);
        var loaded = fs.Bool("loaded", false);
        var output = fs.String("output", "");
        var game = fs.String("game", "rimgovernor-trial");
        var headless = fs.Bool("headless", true);
        var timeout = fs.Duration("timeout", TimeSpan.FromMinutes(10));
        var asJson = fs.Bool("json", false);
        var positional = fs.ParseAfterPositionals(args);
        if (fs.Args.Count > 0)
        {
            throw new UsageException($"the op and its arguments must precede the flags: [{string.Join(' ', fs.Args)}]");
        }

        if (positional.Count == 0)
        {
            throw new UsageException("fixture needs an op name (e.g. test/stage)");
        }

        var opArgs = new JsonObject();
        foreach (var kv in positional.Skip(1))
        {
            var eq = kv.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                throw new UsageException($"argument \"{kv}\" is not key=value");
            }

            // A repeated key keeps its last value, as a map assignment would.
            var key = kv[..eq];
            _ = opArgs.Remove(key);
            opArgs.Add(key, FixtureValue(kv[(eq + 1)..]));
        }

        if (root.Value.Length == 0)
        {
            throw new UsageException("-root is required");
        }

        if (!Path.IsPathFullyQualified(root.Value))
        {
            throw new UsageException($"-root must be absolute: {root.Value}");
        }

        if (loaded.Value && fs.WasSet("save"))
        {
            throw new UsageException("-loaded and -save are exclusive");
        }

        return new FixtureOptions
        {
            Op = positional[0],
            Args = opArgs,
            Save = save.Value,
            Loaded = loaded.Value,
            Root = root.Value,
            Output = Path.GetFullPath(output.Value.Length == 0 ? Path.Combine(root.Value, "acceptance", "fixture") : output.Value),
            GameId = game.Value,
            Headless = headless.Value,
            Timeout = timeout.Value,
            Json = asJson.Value,
        };
    }

    /// <summary>Decodes one key=value argument's value: JSON when it parses as JSON, the literal string otherwise.</summary>
    public static JsonNode? FixtureValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            return JsonNode.Parse(value);
        }
        catch (JsonException)
        {
            return JsonValue.Create(value);
        }
    }

    internal static async Task ExecuteAsync(FixtureOptions o, FixtureRun result, CancellationToken cancellationToken)
    {
        var output = result.Output;
        Directory.CreateDirectory(output);
        Start start = o.Loaded ? new LoadedStart() : new SaveStart { Name = o.Save, IgnoreModCompatibility = o.IgnoreMods };

        // FixtureOps names the op so the stale-package check's rebuild hint does; Prepare stages the baseline save into the
        // profile.
        var config = new Config
        {
            Root = o.Root,
            Output = output,
            Headless = o.Headless,
            GameId = o.GameId,
            FixtureOps = [o.Op],
            KeepLoaded = true,
        };
        var report = Report.Create("fixture " + o.Op, o.Headless);
        var session = await Session.OpenAsync(config, new KeepHost(SessionHost.Default), report, start, QuietMode.IfAvailable, null, cancellationToken).ConfigureAwait(false);
        try
        {
            result.RecordStart(CaseResult.Of(report).Start);
            if (!session.Names.Contains(o.Op, StringComparer.Ordinal))
            {
                throw new AcceptanceException($"missing {o.Op} in discovery; rebuild the native mod with its fixture{session.Host.FixtureBuildHint(o.Op)}");
            }

            var reply = await session.Harness.CallAsync("fixture", o.Op, o.Args, cancellationToken).ConfigureAwait(false);
            result.Response = reply.DeepClone().AsObject();
            result.Success = FixtureSucceeded(o.Op, reply);
            report["prepared"] = reply;

            // The op may have moved the clock; the census reads the paused world.
            await session.PauseAsync(cancellationToken).ConfigureAwait(false);
            ResetState state;
            try
            {
                (state, _) = await GameReuse.ObserveResetAsync(session.Harness, "census", cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"census: {e.Message}", e);
            }

            result.Census = FixtureCensus.Of(state);
            report["census"] = result.Census.ToJson();
            report["passed"] = result.Success;
        }
        finally
        {
            session.Close();
            report.Write(output);
        }
    }

    // Whether native accepted the op: every fixture reply carries its success flag (Fixtures.Contracts FixtureDone).
    private static bool FixtureSucceeded(string op, JsonObject reply)
    {
        try
        {
            return Fx.FixtureJson.Deserialize<Fx.FixtureDone>(reply.ToJsonString()).Success;
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"{op}: reply without a boolean success: {e.Message}", e);
        }
    }

    // Writes the result: one JSON object, or the response block followed by a short census (tick, pause, authority, drafts
    // and the non-zero stocks in name order).
    internal static async Task PrintAsync(TextWriter w, bool asJson, FixtureRun r)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(r);
        if (asJson)
        {
            await w.WriteLineAsync(r.ToJson().ToJsonString(Indented)).ConfigureAwait(false);
            return;
        }

        var error = r.Error ?? "";
        var status = r.Success == true ? "OK" : "REFUSED";
        if (error.Length > 0)
        {
            status = "ERROR";
        }

        await w.WriteLineAsync($"{status}\t{r.Op}\t{r.Output}").ConfigureAwait(false);
        if (error.Length > 0)
        {
            await w.WriteLineAsync($"\t{error}").ConfigureAwait(false);
        }

        if (r.Response is { } response)
        {
            await w.WriteLineAsync($"response:\n{response.ToJsonString(Indented)}").ConfigureAwait(false);
        }

        if (r.Census is not { } c)
        {
            return;
        }

        await w.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"census: tick={c.Tick:0} paused={Bool(c.Paused)} authority={Bool(c.AuthorityActive)} drafted={c.Drafted:0} loadToken={c.LoadToken}")).ConfigureAwait(false);
        foreach (var (name, counts) in c.Stock)
        {
            if (counts.Units == 0)
            {
                continue;
            }

            await w.WriteLineAsync(counts.Forbidden >= 0
                ? string.Create(CultureInfo.InvariantCulture, $"  {name}\t{counts.Units}\t(forbidden {counts.Forbidden})")
                : string.Create(CultureInfo.InvariantCulture, $"  {name}\t{counts.Units}")).ConfigureAwait(false);
        }
    }

    private static string Bool(bool value) => value ? "true" : "false";

    // The world stays loaded for the next call whatever the environment says about keeping the process.
    private sealed class KeepHost(ISessionHost inner) : ISessionHost
    {
        public IStartCache StartCache => inner.StartCache;

        public Task PrepareAsync(Config config, IReadOnlyList<string> saves, CancellationToken cancellationToken) =>
            inner.PrepareAsync(config, saves, cancellationToken);

        public JsonObject PackageFiles(Config config) => inner.PackageFiles(config);

        public async Task<IGameHold> OpenGameAsync(Config config, Report report, CancellationToken cancellationToken)
        {
            var hold = await inner.OpenGameAsync(config, report, cancellationToken).ConfigureAwait(false);
            if (hold is Game game)
            {
                game.Keep = true;
            }

            return hold;
        }

        public JsonObject RecordWorld(Config config, Start start, WorldSource source) => inner.RecordWorld(config, start, source);

        public string FixtureBuildHint(string op) => inner.FixtureBuildHint(op);
    }
}
