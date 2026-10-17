namespace RimGovernor.Launcher;

/// <summary>
/// The addresses the launcher page may open in the browser. The bind is an allowlist, not a
/// general open-URL: page content must not be able to launch arbitrary URLs.
/// </summary>
public static class Links
{
    public const string PlayerGuideUrl = "https://github.com/davidarcher/rimgovernor/blob/main/docs/players/README.md";

    /// <summary>Throws <see cref="LauncherException"/> unless <paramref name="url"/> is an allowlisted address, compared exactly.</summary>
    public static void RequireAllowed(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url == PlayerGuideUrl)
        {
            return;
        }

        throw new LauncherException($"link not allowed: \"{url}\"");
    }
}
