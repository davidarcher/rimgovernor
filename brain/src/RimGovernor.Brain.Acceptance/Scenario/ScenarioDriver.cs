using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The scenario clock the case session binds (Go <c>session.Runtime</c> and <c>session.Advance</c> in cases/run.go):
/// a runtime over a controller clock acquired on first use, and <see cref="AdvanceGame"/> with the case's Letters as
/// the expected interruption letters when it declared any, the case's own options following and free to override them.
/// </summary>
public sealed class ScenarioDriver : IScenarioDriver
{
    /// <inheritdoc />
    public async Task<IScenarioRuntime> CreateRuntimeAsync(Harness harness, JsonObject identity, Report report, IReadOnlyList<string> tools, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        var clock = new ScenarioClock(harness.WireFunc(), identity, Draft.Controller, report);
        _ = await clock.AcquireAsync("acquire", cancellationToken).ConfigureAwait(false);
        return new ScenarioRuntime(harness.CallAsync, clock, report, tools);
    }

    /// <inheritdoc />
    public Task<JsonObject> AdvanceAsync(
        IScenarioRuntime runtime,
        ulong ticks,
        IReadOnlyList<(string Label, string LetterDef)>? expectedLetters,
        IReadOnlyList<IScenarioAdvanceOption> options,
        CancellationToken cancellationToken)
    {
        if (runtime is not ScenarioRuntime scenario)
        {
            throw new AcceptanceException("the scenario runtime was not built by ScenarioDriver");
        }

        var all = new List<IScenarioAdvanceOption>();
        if (expectedLetters is not null)
        {
            all.Add(AdvanceOptions.WithExpectedLetters([.. expectedLetters]));
        }

        all.AddRange(options);
        return AdvanceGame.RunAsync(scenario, ticks, all, cancellationToken);
    }
}
