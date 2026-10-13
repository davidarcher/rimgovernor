using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The profile- and process-dependent half of the session lifecycle (<see cref="ISessionHost"/>), over this folder's
/// profile preparation, stale-package check, start cache, game open and world record. One instance serves the whole
/// process: <see cref="Default"/>.
/// </summary>
public sealed class SessionHost : ISessionHost
{
    /// <summary>The host every session of this process uses.</summary>
    public static SessionHost Default { get; } = new();

    /// <inheritdoc />
    public IStartCache StartCache => ProfileStartCache.Current;

    /// <inheritdoc />
    public async Task PrepareAsync(Config config, IReadOnlyList<string> saves, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(saves);
        foreach (var name in saves)
        {
            await config.EnsureSaveAsync(name, cancellationToken).ConfigureAwait(false);
        }

        if (saves.Count > 0 && config.Expansions is null)
        {
            await config.UseSaveExpansionsAsync(saves, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            config.PrepareConfig();
        }
        catch (AcceptanceException e) when (e is not StalePackageException)
        {
            throw new AcceptanceException($"prepare profile: {e.Message}", e);
        }
    }

    /// <inheritdoc />
    public JsonObject PackageFiles(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var workingDir = config.GameSection()["workingDir"].AsString();
        var files = new JsonObject();
        foreach (var (name, hash) in Harness.PackageFiles(workingDir))
        {
            files[name] = hash;
        }

        return files;
    }

    /// <inheritdoc />
    public async Task<IGameHold> OpenGameAsync(Config config, Report report, CancellationToken cancellationToken) =>
        await Game.OpenGameAsync(config, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public JsonObject RecordWorld(Config config, Start start, WorldSource source) =>
        WorldRecords.Record(config, start, source).ToJson();

    /// <inheritdoc />
    public string FixtureBuildHint(string op) => StalePackage.FixtureBuildHint(op);
}
