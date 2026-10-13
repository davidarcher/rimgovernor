using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RimGovernor.Brain.Acceptance;

/// <summary>A finished <c>lifecycle_new_colony</c> start (Go <c>NewColonyRun</c>).</summary>
public sealed record NewColonyRun
{
    /// <summary>The phases seen in wire order, each once.</summary>
    public IReadOnlyList<string> Phases { get; init; } = [];

    /// <summary>The completed reply: saveName, seed, paused, byteLength, context.</summary>
    public JsonObject Completed { get; init; } = [];

    /// <summary>The last rerollCount a pending reply carried (completed replies carry none), 0 when the first roll was accepted.</summary>
    public int Rerolls { get; init; }

    /// <summary>
    /// Each phase's start in ms since the op began (the pending reply's elapsedMs at the first poll that saw it, so
    /// quantised by the 250 ms poll).
    /// </summary>
    public IReadOnlyDictionary<string, long> PhaseMs { get; init; } = new Dictionary<string, long>(StringComparer.Ordinal);

    /// <summary>The harness's own wall time for the whole start.</summary>
    public long TotalMs { get; init; }
}

/// <summary>The new-colony op's driver and the save-file digests of a generated start (Go <c>newcolony.go</c>).</summary>
public static class NewColony
{
    /// <summary>Prefixes the wire names of NewColonyPhase.</summary>
    public const string NewColonyPhasePrefix = "NEW_COLONY_PHASE_";

    /// <summary>The wire order a start's phases must follow.</summary>
    public static readonly IReadOnlyList<string> NewColonyPhaseOrder = ["GENERATING_WORLD", "CHOOSING_TILE", "ROLLING_COLONISTS", "GENERATING_MAP", "FINISHING", "SAVING"];

    /// <summary>The timeoutMs a harness start asks for when the caller names none: the team policy's reroll budget can take minutes.</summary>
    public static readonly TimeSpan NewColonyTimeout = TimeSpan.FromMinutes(10);

    private const int PollBudget = 6000;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan SaveScanTimeout = TimeSpan.FromSeconds(60);

    private static readonly Regex MapThingPattern = new(
        "<thing Class=\"[^\"]*\">\\s*<def>([^<]*)</def>.*?<pos>([^<]*)</pos>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        SaveScanTimeout);

    private static readonly Regex MapTerrainBlock = new(
        "<terrainGrid>.*?</terrainGrid>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        SaveScanTimeout);

    private static readonly Regex StartingTilePattern = new(
        "<startingTile>(\\d+)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(10));

    /// <summary>
    /// A digest of the generated map in a save, for comparing two starts of one spec: the terrain grid and every
    /// thing's def and position, sorted. Ids, ticks and anything else a start stamps are left out.
    /// </summary>
    public static string SaveMapHash(string savePath)
    {
        var text = File.ReadAllText(savePath);
        var start = text.IndexOf("<maps>", StringComparison.Ordinal);
        var end = text.LastIndexOf("</maps>", StringComparison.Ordinal);
        if (start < 0 || end < start)
        {
            throw new AcceptanceException($"{savePath} has no maps section");
        }

        text = text[start..end];
        var terrain = MapTerrainBlock.Match(text);
        if (!terrain.Success)
        {
            throw new AcceptanceException($"{savePath} has no terrain grid");
        }

        var things = new List<string>();
        foreach (Match match in MapThingPattern.Matches(text))
        {
            things.Add(match.Groups[1].Value + "@" + match.Groups[2].Value);
        }

        if (things.Count == 0)
        {
            throw new AcceptanceException($"{savePath} has no map things");
        }

        things.Sort(StringComparer.Ordinal);
        using var sum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sum.AppendData(Encoding.UTF8.GetBytes(terrain.Value));
        foreach (var thing in things)
        {
            sum.AppendData("\n"u8);
            sum.AppendData(Encoding.UTF8.GetBytes(thing));
        }

        var digest = Convert.ToHexStringLower(sum.GetHashAndReset().AsSpan(0, 8));
        return string.Create(CultureInfo.InvariantCulture, $"{digest}/{things.Count}-things");
    }

    /// <summary>Wraps a wire spec in a <c>lifecycle_new_colony</c> request; a zero timeout is <see cref="NewColonyTimeout"/>.</summary>
    public static JsonObject NewColonyRequest(string id, JsonObject spec, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (timeout <= TimeSpan.Zero)
        {
            timeout = NewColonyTimeout;
        }

        return new JsonObject { ["requestId"] = id, ["spec"] = spec, ["timeoutMs"] = (long)timeout.TotalMilliseconds };
    }

    /// <summary>
    /// Starts a colony (<c>lifecycle_new_colony</c>) and polls until it completes. The game must be at the main menu;
    /// it ends paused with a live map, the naming dialog confirmed and the save in the profile's Saves. A terminal
    /// failure is an error (a failed start can leave game state behind: restart the game before retrying); phases
    /// must follow <see cref="NewColonyPhaseOrder"/> without going back.
    /// </summary>
    public static async Task<NewColonyRun> RunNewColonyAsync(Harness harness, JsonObject request, string label, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(request);
        var phases = new List<string>();
        var phaseMs = new Dictionary<string, long>(StringComparer.Ordinal);
        var rerolls = 0;
        var began = Stopwatch.StartNew();
        var id = request["requestId"].AsString();
        var reply = await harness.WireAsync(label + "-start", "lifecycle_new_colony", request, cancellationToken).ConfigureAwait(false);
        var last = -1;
        for (var attempt = 0; attempt < PollBudget; attempt++)
        {
            if (reply["failure"].AsMap() is { } failure)
            {
                throw new AcceptanceException($"{label}: new colony failed: {failure["code"].AsString()}: {Show(failure["detail"])}");
            }

            if (TryCase(reply, "completed", out var completed))
            {
                if (!phases.Contains("SAVING", StringComparer.Ordinal))
                {
                    throw new AcceptanceException($"{label}: completed without reporting SAVING (phases [{string.Join(' ', phases)}])");
                }

                return new NewColonyRun
                {
                    Phases = phases,
                    Completed = completed,
                    Rerolls = rerolls,
                    PhaseMs = phaseMs,
                    TotalMs = began.ElapsedMilliseconds,
                };
            }

            if (!TryCase(reply, "pending", out var pending))
            {
                throw new AcceptanceException($"{label}: unexpected reply shape: {reply.ToJson()}");
            }

            var phase = pending["phase"].AsString();
            if (phase.StartsWith(NewColonyPhasePrefix, StringComparison.Ordinal))
            {
                phase = phase[NewColonyPhasePrefix.Length..];
            }

            var index = IndexOf(NewColonyPhaseOrder, phase);
            if (index < 0)
            {
                throw new AcceptanceException($"{label}: unknown phase \"{phase}\"");
            }

            if (index < last)
            {
                throw new AcceptanceException($"{label}: phase went backwards to {phase} after {phases[^1]}");
            }

            if (index != last)
            {
                phases.Add(phase);
                phaseMs[phase] = (long)pending["elapsedMs"].AsNumber();
                last = index;
            }

            rerolls = (int)pending["rerollCount"].AsNumber();
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            reply = await harness.WireAsync(
                string.Create(CultureInfo.InvariantCulture, $"{label}-poll-{attempt}"),
                "lifecycle_read_new_colony",
                new JsonObject { ["requestId"] = id },
                cancellationToken).ConfigureAwait(false);
        }

        throw new AcceptanceException($"{label}: new colony did not complete within the polling budget (phases [{string.Join(' ', phases)}])");
    }

    /// <summary>The world tile the save's colony settled.</summary>
    public static int SaveStartingTile(string savePath)
    {
        var match = StartingTilePattern.Match(File.ReadAllText(savePath));
        return !match.Success
            ? throw new AcceptanceException($"{savePath} records no starting tile")
            : int.Parse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static bool TryCase(JsonObject message, string name, out JsonObject value)
    {
        if (message.Count == 1 && message[name] is JsonObject found)
        {
            value = found;
            return true;
        }

        value = [];
        return false;
    }

    private static int IndexOf(IReadOnlyList<string> list, string want)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], want, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Show(JsonNode? value) => value is JsonValue json && json.TryGetValue<string>(out var text) ? text : value.ToJson();
}
