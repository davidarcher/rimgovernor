using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>lifecycle/*</c> acceptance cases and <c>tools/startcost</c> (Go <c>cases/lifecycle</c>): the process
/// lifecycle (save, load, new colony, unload, fault, reuse) driven through the native tools.
/// <see cref="Register"/> replaces the Go package's <c>init()</c>.
/// </summary>
public static class LifecycleCases
{
    /// <summary>Registers every <c>lifecycle/*</c> case and <c>tools/startcost</c>.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(LifecycleCheckpoint.Case());
        registry.Register(LifecycleIdeoligionDesign.Case());
        registry.Register(LifecycleShutdown.Case());
        registry.Register(LifecycleRuntimeFault.Case());
        registry.Register(LifecycleLoad.Case());
        registry.Register(LifecycleNewColony.Case());
        registry.Register(LifecycleNewColonyParts.Case());
        registry.Register(LifecycleNewColonyTeam.Case());
        registry.Register(LifecycleQuietStoryteller.Case());
        registry.Register(LifecycleScenarioStartParts.Case());
        registry.Register(LifecycleReuse.Case());
        registry.Register(LifecycleHeadlessSoak.Case());
        registry.Register(ToolsStartCost.Case());
    }

    /// <summary>A unique suffix for request ids and save names (Go <c>time.Now().UnixNano()</c>).</summary>
    internal static string Stamp() => DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);

    /// <summary>The failure code of a wire reply that is a <c>failure</c> message; null when the reply is not one.</summary>
    internal static string? FailureCode(JsonObject reply) => reply["failure"].AsMap() is { } failure ? failure["code"].AsString() : null;

    /// <summary>The unavailable reason of a wire reply that is an <c>unavailable</c> message; null when it is not one.</summary>
    internal static string? UnavailableReason(JsonObject reply) => reply["unavailable"].AsMap() is { } unavailable ? unavailable["reason"].AsString() : null;

    /// <summary>Asserts the reply carries failure code <paramref name="want"/>.</summary>
    internal static void ExpectFailure(string label, JsonObject reply, string want)
    {
        var code = FailureCode(reply);
        if (code != want)
        {
            throw new AcceptanceException($"{label}: expected {want}, got {code ?? ""}");
        }
    }

    /// <summary>Harness.Outcome with the case label prefixed on a mismatch (Go <c>fmt.Errorf("%s: ...: %w")</c>).</summary>
    internal static (string Case, JsonObject Value) Outcome(string label, JsonObject reply, params string[] cases)
    {
        try
        {
            return Harness.Outcome(reply, cases);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{label}: {e.Message}", e);
        }
    }

    /// <summary>The identity and tick a <c>lifecycle_read_identity</c> reply says the loaded game has.</summary>
    internal static async Task<(JsonObject Loaded, JsonObject Identity, double Tick)> ReadIdentityAsync(Harness h, string label, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(label, "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var (_, loaded) = Harness.Outcome(reply, "loaded");
        var context = loaded["context"].AsMap();
        var identity = context?["identity"].AsMap() ?? throw new AcceptanceException($"{label}: missing identity in {loaded.ToJson()}");
        return (loaded, identity, context["tick"].AsNumber());
    }

    /// <summary>Starts a paused debug colony (the quiet op applied when available) and returns its identity.</summary>
    internal static async Task<JsonObject> NewGameAsync(ISession s, string label, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        _ = await DebugGame.StartDebugGameAsync(h, ProfileStartCache.Current, null, QuietMode.IfAvailable, cancellationToken).ConfigureAwait(false);
        _ = await h.CallAsync(label + "-pause", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        var (_, identity, _) = await ReadIdentityAsync(h, label + "-identity", cancellationToken).ConfigureAwait(false);
        return identity;
    }

    /// <summary>The native generation and the single authority state (<c>unavailable</c>, <c>inactive</c> or <c>active</c>) for an identity.</summary>
    internal static async Task<(ulong Generation, string State, JsonObject Value)> ReadAuthorityAsync(Harness h, string label, JsonObject identity, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(label, "authority_read_status", new JsonObject { ["identity"] = identity.DeepClone() }, cancellationToken).ConfigureAwait(false);
        var (_, status) = Harness.Outcome(reply, "status");
        var generation = (ulong)status["context"].Member("nativeGeneration").AsNumber();
        string? found = null;
        foreach (var key in new[] { "unavailable", "inactive", "active" })
        {
            if (status.ContainsKey(key))
            {
                if (found is not null)
                {
                    throw new AcceptanceException($"{label}: expected exactly one authority state, got {status.ToJson()}");
                }

                found = key;
            }
        }

        if (found is null)
        {
            throw new AcceptanceException($"{label}: expected exactly one authority state, got {status.ToJson()}");
        }

        if (found != "unavailable" && generation == 0)
        {
            throw new AcceptanceException($"{label}: missing native generation: {status.ToJson()}");
        }

        return (generation, found, status);
    }

    /// <summary>Issues SetMode(Auto) at the expected generation and returns the granted generation.</summary>
    internal static async Task<ulong> GrantAsync(Harness h, string label, JsonObject identity, ulong expected, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(
            label,
            "authority_control",
            new JsonObject
            {
                ["setMode"] = new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["expectedGeneration"] = expected,
                    ["mode"] = "MODE_AUTO",
                },
            },
            cancellationToken).ConfigureAwait(false);
        var (_, granted) = Harness.Outcome(reply, "granted");
        if (granted["authority"].AsMap()?["mode"].AsString() != "MODE_AUTO")
        {
            throw new AcceptanceException($"{label}: expected MODE_AUTO, got {granted.ToJson()}");
        }

        return (ulong)granted["context"].Member("nativeGeneration").AsNumber();
    }

    /// <summary>Asserts every named tool is in the discovered catalog.</summary>
    internal static void RequireTools(ISession s, params string[] tools)
    {
        foreach (var tool in tools)
        {
            if (!Dyn.Contains(s.Names, tool))
            {
                throw new AcceptanceException($"missing {tool} in discovery");
            }
        }
    }
}
