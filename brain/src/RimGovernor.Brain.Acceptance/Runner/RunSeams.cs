namespace RimGovernor.Brain.Acceptance;

// The narrow interfaces the runner needs from machinery other slices own: the pre-run doctor/heal pass and the
// session host. The tool host wires an implementation into RunDependencies.

/// <summary>What the pre-run doctor/heal pass decided (Go <c>preflight</c>'s two results).</summary>
public sealed record PreflightResult(bool Ok, IReadOnlyList<string> Healed);

/// <summary>The doctor preflight: only its failing checks print and a failure refuses the run, except a stale or fixture-less install, which it rebuilds and reinstalls unless <see cref="RunOptions.NoHeal"/> (Go <c>cmd/acceptance/heal.go</c>).</summary>
public interface IRunPreflight
{
    Task<PreflightResult> RunAsync(IReadOnlyList<AcceptanceCase> selected, RunOptions options, TextWriter stdout, CancellationToken cancellationToken);
}

/// <summary>Everything the runner needs wired from outside this folder.</summary>
public sealed class RunDependencies
{
    /// <summary>The doctor/heal pass; null skips the preflight.</summary>
    public IRunPreflight? Preflight { get; init; }

    /// <summary>The profile and process half of the session lifecycle; <see cref="SessionHost.Default"/> when null.</summary>
    public ISessionHost? Host { get; init; }

    /// <summary>The dependencies <see cref="RunCommand.RunAsync(IReadOnlyList{string}, CaseRegistry, TextWriter, TextWriter, CancellationToken)"/> uses; the tool host sets it once at startup.</summary>
    public static RunDependencies? Current { get; set; }
}
