using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>combatlab/*</c> acceptance cases (Go <c>cases/combatlab</c>): the standard combat fixtures staged on the blank
/// lab, and the tactic cases that run on them. <see cref="Register"/> replaces the Go package's <c>init()</c>.
/// </summary>
public static class CombatLabCases
{
    /// <summary>Registers every <c>combatlab/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(CombatLabStage.Case());
        registry.Register(CombatLabSeededStaging.Case());
        registry.Register(CombatLabReseed.Case());
        registry.Register(CombatLabSiegeStage.Case());
        registry.Register(CombatLabMirror.Case());
        registry.Register(CombatLabOrders.Case());
        registry.Register(CombatLabStops.Case());
    }

    /// <summary>An array of the strings.</summary>
    internal static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);
}
