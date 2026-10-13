using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>Process-wide facts a run learns once and Finalize reports (Go package-level state such as <c>installedPackage</c>).</summary>
public static class RunState
{
    /// <summary>What the stale-package check found, for Finalize's <c>installed_package</c>.</summary>
    public static JsonObject? InstalledPackage { get; set; }
}
