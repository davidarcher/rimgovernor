using System.Runtime.CompilerServices;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Telemetry;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The tests' telemetry sink (#3753 D3): it records nothing, but a <c>required_fact</c> decision (a planner run skipped
/// for an unread fact) throws, so any test that exercises a planner over missing facts fails. Stage the fact in the
/// test's input; never weaken the sink. A test that asserts the skip itself opens <see cref="Expect"/>, which records the
/// facts instead of throwing for the code it runs (async flow included).
/// </summary>
public sealed class RequiredFactSink : ITelemetrySink
{
    public static readonly RequiredFactSink Instance = new();

    private static readonly AsyncLocal<List<string>?> s_expected = new();

    private RequiredFactSink()
    {
    }

    /// <summary>An emitter over the sink: what a test hands code that takes a <see cref="TelemetryEmitter"/>.</summary>
    public static TelemetryEmitter Emitter() => new(Instance);

    /// <summary>Installs the sink under the runtime's static emitter before any test runs.</summary>
#pragma warning disable CA2255 // the test assembly's one-time setup
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Install() => Tel.Emitter = Emitter();

    /// <summary>Records the facts of the required_fact decisions the calling flow emits from here on instead of throwing.</summary>
    public static List<string> Expect()
    {
        var seen = new List<string>();
        s_expected.Value = seen;
        return seen;
    }

    public bool Enabled(TelemetryLevel severity, string kind) => kind == RequiredFactTelemetry.Kind;

    public void Emit(TelemetryRecord record)
    {
        if (RequiredFactTelemetry.FactOf(record) is not { } fact)
        {
            return;
        }
        if (s_expected.Value is { } seen)
        {
            lock (seen)
            {
                seen.Add(fact);
            }
            return;
        }
        throw new InvalidOperationException($"a planner run was skipped for the unread required fact '{fact}' (required_fact): stage the fact in the test's input");
    }
}
