using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

public sealed partial class Config
{
    /// <summary>
    /// Runs Prepare (headless) or PrepareRendered (windowed) against <see cref="Root"/> and records the resolved
    /// configuration directory and, for the cached debug start, the profile it prepared.
    /// </summary>
    public void PrepareConfig()
    {
        var expansions = Expansions ?? Brain.Acceptance.Expansions.FromEnvironment();
        string configuration;
        try
        {
            configuration = Headless
                ? HeadlessProfile.Prepare(Root, FixtureOps, expansions, Graphics)
                : HeadlessProfile.PrepareRendered(Root, FixtureOps, expansions);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AcceptanceException($"prepare profile: {e.Message}", e);
        }

        Configuration = configuration;
        ProfileStartCache.Current.Configure(HeadlessProfile.MustAbs(Root), Headless, expansions ?? []);
    }

    /// <summary>Reads back <c>games.&lt;GameId&gt;</c> from the resolved configuration directory's config.json, e.g. to recover workingDir for the package files.</summary>
    public JsonObject GameSection() => HeadlessProfile.GameSection(HeadlessProfile.LoadConfig(Path.Combine(Configuration, "config.json")), GameId);

    /// <summary>
    /// The game log of the instance under <see cref="Root"/>: the stdout the launcher captures into its state directory
    /// (<c>&lt;Root&gt;/config-headless</c> or <c>&lt;Root&gt;/config</c>), per root. 1.7 ignores -logFile and writes
    /// Player.log to one shared LocalLow path, so nothing reads that file.
    /// </summary>
    public string StartupLogPath() => GameProcess.LogPath(StateDirectory(), GameId);

    /// <summary>The launcher's state directory for this configuration's root: where the running game's records and log live.</summary>
    public string StateDirectory() => Path.Combine(Root, Headless ? "config-headless" : "config");

    /// <summary>
    /// Sets <see cref="Expansions"/> to the union of the official expansions the named saves were recorded with, so
    /// <see cref="PrepareConfig"/> keeps exactly those active. Empty names are skipped; when every name is empty
    /// <see cref="Expansions"/> is left as it was (null: the environment variable, else every installed expansion).
    /// </summary>
    public async Task UseSaveExpansionsAsync(IReadOnlyList<string> saveNames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(saveNames);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        var any = false;
        foreach (var name in saveNames)
        {
            if (name.Length == 0)
            {
                continue;
            }

            any = true;
            await EnsureSaveAsync(name, cancellationToken).ConfigureAwait(false);
            foreach (var id in HeadlessProfile.SaveExpansions(Root, name))
            {
                if (seen.Add(id))
                {
                    result.Add(id);
                }
            }
        }

        if (any)
        {
            Expansions = result;
        }
    }
}
