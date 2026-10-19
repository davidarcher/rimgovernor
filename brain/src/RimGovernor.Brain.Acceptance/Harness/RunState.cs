using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>Process-wide facts a run learns once and Finalize reports.</summary>
public static class RunState
{
    /// <summary>What the stale-package check found, for the report's <c>installed_package</c> (<see cref="Report.StampInstalledPackage"/>).</summary>
    public static JsonObject? InstalledPackage { get; set; }
}
