namespace RimGovernor.Brain.Acceptance;

// The narrow interfaces the runner needs from machinery other slices own: the pre-run doctor/heal pass and the
// session host. The tool host wires an implementation into RunDependencies.

/// <summary>What the pre-run doctor/heal pass decided.</summary>
public sealed record PreflightResult(bool Ok, IReadOnlyList<string> Healed);

/// <summary>Everything the runner needs wired from outside this folder.</summary>
public sealed class RunDependencies
{
    /// <summary>The profile and process half of the session lifecycle; <see cref="SessionHost.Default"/> when null.</summary>
    public ISessionHost? Host { get; init; }

    /// <summary>The dependencies <see cref="RunCommand.RunAsync(IReadOnlyList{string}, CaseRegistry, TextWriter, TextWriter, CancellationToken)"/> uses; the tool host sets it once at startup.</summary>
    public static RunDependencies? Current { get; set; }
}
