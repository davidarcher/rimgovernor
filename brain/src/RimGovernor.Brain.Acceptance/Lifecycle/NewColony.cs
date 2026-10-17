using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

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

/// <summary>The new-colony op's driver (Go <c>newcolony.go</c>).</summary>
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
