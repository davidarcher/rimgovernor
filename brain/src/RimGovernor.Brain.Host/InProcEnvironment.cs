namespace RimGovernor.Brain.Host;

/// <summary>
/// What the mod resolves from the launch environment before it starts the host (#3115): the paths and switches the Go
/// controller took as <c>--state</c>, <c>--profile</c>, <c>--resume</c> and <c>--layout-overlay</c>. A pure function of
/// the environment variables and the game's save-data folder, so the mod's bootstrap holds no parsing of its own.
/// </summary>
public sealed record InProcInputs
{
    public required string StatePath { get; init; }

    public required string ProfileDirectory { get; init; }

    public required string OtlpDirectory { get; init; }

    public required string RunId { get; init; }

    /// <summary>Runs the bot for every fresh observed world without a player click (off in observe mode).</summary>
    public required bool AutoResume { get; init; }

    /// <summary>Draws the colony layout plan as a native overlay (off in observe mode).</summary>
    public required bool LayoutOverlay { get; init; }
}

/// <summary>
/// Resolves <see cref="InProcInputs"/>. The variables are the in-process host's settings surface (the mod has no CLI):
/// <list type="bullet">
/// <item><c>RIMGOVERNOR_STATE</c>: the state database; default <c>&lt;save-data&gt;/RimGovernor/brain-state.db</c>, or a
/// stamped <c>brain-state-yyyyMMdd-HHmmss.db</c> beside it when <c>RIMGOVERNOR_CONTINUE_STATE</c> is off. An explicit
/// path wins over the switch.</item>
/// <item><c>RIMGOVERNOR_PROFILE</c>: the shared game profile directory; default the game's save-data folder.</item>
/// <item><c>RIMGOVERNOR_OTLP_DIR</c> and <c>RIMGOVERNOR_RUN_ID</c>: the Bridge's existing variables; defaults
/// <c>&lt;save-data&gt;/RimGovernor/otlp</c> and a fresh GUID.</item>
/// <item>The launcher's settings: <c>RIMGOVERNOR_OBSERVE</c> (observe only: no auto-resume, no layout overlay),
/// <c>RIMGOVERNOR_CONTINUE_STATE</c> (default on) and <c>RIMGOVERNOR_LAYOUT_OVERLAY</c> (default on). Booleans are
/// 1/0, true/false or yes/no.</item>
/// </list>
/// Paths must be absolute; a bad value is an error, never a silent default.
/// </summary>
public static class InProcEnvironment
{
    public const string State = "RIMGOVERNOR_STATE";
    public const string Profile = "RIMGOVERNOR_PROFILE";
    public const string OtlpDirectory = "RIMGOVERNOR_OTLP_DIR";
    public const string RunId = "RIMGOVERNOR_RUN_ID";
    public const string Observe = "RIMGOVERNOR_OBSERVE";
    public const string ContinueState = "RIMGOVERNOR_CONTINUE_STATE";
    public const string LayoutOverlay = "RIMGOVERNOR_LAYOUT_OVERLAY";

    /// <summary>The directory under the game's save-data folder the defaults live in.</summary>
    public const string DefaultFolder = "RimGovernor";

    /// <summary>
    /// Resolves the inputs from <paramref name="get"/> (an environment lookup returning null for an unset variable).
    /// Returns false with <paramref name="error"/> naming the variable that is wrong.
    /// </summary>
    public static bool TryResolve(Func<string, string?> get, string saveDataFolder, DateTimeOffset now, out InProcInputs? inputs, out string? error)
    {
        ArgumentNullException.ThrowIfNull(get);
        inputs = null;
        error = null;
        if (!Path.IsPathRooted(saveDataFolder))
        {
            error = "the game's save-data folder is not an absolute path";
            return false;
        }
        if (!TryFlag(get, Observe, false, out var observe, out error)
            || !TryFlag(get, ContinueState, true, out var continueState, out error)
            || !TryFlag(get, LayoutOverlay, true, out var overlay, out error)
            || !TryPath(get, State, null, out var state, out error)
            || !TryPath(get, Profile, saveDataFolder, out var profile, out error)
            || !TryPath(get, OtlpDirectory, Path.Combine(saveDataFolder, DefaultFolder, "otlp"), out var otlp, out error))
        {
            return false;
        }

        var home = Path.Combine(saveDataFolder, DefaultFolder);
        state ??= Path.Combine(home, continueState ? "brain-state.db" : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"brain-state-{now:yyyyMMdd-HHmmss}.db"));
        var runId = get(RunId);
        inputs = new InProcInputs
        {
            StatePath = state,
            ProfileDirectory = profile!,
            OtlpDirectory = otlp!,
            RunId = string.IsNullOrWhiteSpace(runId) ? Guid.NewGuid().ToString("N") : runId.Trim(),
            AutoResume = !observe,
            LayoutOverlay = overlay && !observe,
        };
        return true;
    }

    private static bool TryFlag(Func<string, string?> get, string name, bool fallback, out bool value, out string? error)
    {
        error = null;
        var raw = get(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = fallback;
            return true;
        }
        switch (raw.Trim().ToLowerInvariant())
        {
            case "1" or "true" or "yes":
                value = true;
                return true;
            case "0" or "false" or "no":
                value = false;
                return true;
            default:
                value = fallback;
                error = $"{name} must be 1/0, true/false or yes/no, not '{raw}'";
                return false;
        }
    }

    private static bool TryPath(Func<string, string?> get, string name, string? fallback, out string? value, out string? error)
    {
        error = null;
        var raw = get(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = fallback;
            return true;
        }
        value = raw.Trim();
        if (!Path.IsPathRooted(value))
        {
            error = $"{name} must be an absolute path, not '{raw}'";
            return false;
        }
        return true;
    }
}
