using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>lifecycle/*</c> acceptance cases (Go <c>cases/lifecycle</c>): the process lifecycle (save, load, new colony,
/// unload, fault, reuse) driven through the native tools. <see cref="Register"/> replaces the Go package's <c>init()</c>.
/// </summary>
public static class LifecycleCases
{
    /// <summary>Registers every <c>lifecycle/*</c> case.</summary>
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
    }

    /// <summary>A unique suffix for request ids and save names (Go <c>time.Now().UnixNano()</c>).</summary>
    internal static string Stamp() => DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);

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
}
