using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>lab/*</c> acceptance cases (Go <c>cases/lab</c>): lab contract cases that open on the blank Soil lab, spawn what
/// they need with <see cref="DebugGame.LabSpawnAsync"/> and make one op or read assertion in 10-30 s.
/// <see cref="Register"/> replaces the Go package's <c>init()</c>.
/// </summary>
public static class LabCases
{
    /// <summary>Registers every <c>lab/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(LabGridThings.Case());
        registry.Register(LabSpawn.Case());
        registry.Register(LabSpoilagePick.Case());
        registry.Register(LabTraining.Case());
        registry.Register(LabSkillLearning.Case());
        foreach (var c in LabTrainingRange.Cases())
        {
            registry.Register(c);
        }

        foreach (var c in LabCaptureProfile.Cases())
        {
            registry.Register(c);
        }

        registry.Register(LabSparring.Case());
        registry.Register(LabSparringWatch.Case());
        foreach (var c in LabSparringMeasure.Cases())
        {
            registry.Register(c);
        }
    }

    /// <summary>The ids a reply array holds as strings (Go <c>stringList</c>).</summary>
    internal static List<string> StringList(JsonNode? value) => [.. value.AsSlice().Select(e => e.AsString())];

    /// <summary>The integers a reply array holds (Go <c>intList</c>).</summary>
    internal static List<int> IntList(JsonNode? value) => [.. value.AsSlice().Select(e => (int)e.AsNumber())];

    /// <summary>A boolean reply member, false when absent or not a boolean.</summary>
    internal static bool Flag(JsonObject? row, string key) => row?[key].AsBool() == true;

    /// <summary>Whether a native fixture reply says <c>success</c>.</summary>
    internal static bool Succeeded(JsonObject reply) => reply["success"].AsBool() == true;
}
