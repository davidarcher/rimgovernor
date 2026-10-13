namespace RimGovernor.Launcher;

/// <summary>One provisioned dashboard the launcher opens, in button order (the page reads <c>name</c> and <c>url</c>).</summary>
public sealed record DashboardLink(string Name, string Url);

/// <summary>
/// The addresses the launcher page may open in the browser (Go <c>links.go</c>). The bind is an allowlist, not a
/// general open-URL: page content must not be able to launch arbitrary URLs.
/// </summary>
public static class Links
{
    public const string PlayerGuideUrl = "https://github.com/davidarcher/rimgovernor/blob/main/docs/players/README.md";

    /// <summary>The local observability stack's Grafana (tools/observability/compose.yml).</summary>
    public const string GrafanaUrl = "http://localhost:3000";

    /// <summary>The provisioned dashboards, in button order; the uids are the files' in tools/observability/dashboards.</summary>
    public static IReadOnlyList<DashboardLink> Dashboards { get; } =
    [
        new("Now", GrafanaUrl + "/d/rg-now"),
        new("Explain", GrafanaUrl + "/d/rg-concern-transitions"),
        new("Problems", GrafanaUrl + "/d/rg-problems"),
        new("Log", GrafanaUrl + "/d/rg-log"),
        new("Ledger", GrafanaUrl + "/d/rg-ledger"),
    ];

    /// <summary>Throws <see cref="LauncherException"/> unless <paramref name="url"/> is an allowlisted address, compared exactly.</summary>
    public static void RequireAllowed(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url == PlayerGuideUrl || url == GrafanaUrl || Dashboards.Any(d => d.Url == url))
        {
            return;
        }

        throw new LauncherException($"link not allowed: \"{url}\"");
    }
}
