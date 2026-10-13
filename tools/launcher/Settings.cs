using System.Text;
using System.Text.Json;

namespace RimGovernor.Launcher;

/// <summary>
/// The launcher's persisted choices (<c>.rimgovernor/launcher.json</c>; Go <c>Settings</c>). The page edits them and
/// the file keeps the page's camelCase names. <see cref="ExtraArgs"/> and <see cref="LoadSave"/> act on a start today;
/// the in-process host reads no other input yet (#3115), so the remaining choices are kept for it.
/// </summary>
public sealed record Settings
{
    /// <summary>The launcher's JSON dialect: camelCase, case-insensitive on read, the page's own.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The game launch flag that asks the mod to host the brain (<c>InProcLaunch.Flag</c> of Brain.Host).</summary>
    public const string InProcFlag = "-rimgovernor-inproc";

    /// <summary>Observe only: no control, no writes.</summary>
    public bool Observe { get; init; }

    /// <summary>Reuse the brain's earlier state instead of starting a fresh one.</summary>
    public bool ContinueState { get; init; } = true;

    /// <summary>A save name (no .rws) to load once the game's channel answers; empty leaves the game at its main menu.</summary>
    public string LoadSave { get; init; } = "";

    public bool LayoutOverlay { get; init; } = true;

    /// <summary>Send telemetry to the local observability stack (the stack itself is started separately).</summary>
    public bool Telemetry { get; init; }

    /// <summary>Free text split on whitespace (double quotes group) and appended to the game's command line.</summary>
    public string ExtraArgs { get; init; } = "";

    /// <summary>Reads <paramref name="path"/> over the defaults; a missing file is the defaults. Throws <see cref="LauncherException"/> for an unreadable one.</summary>
    public static Settings Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return new Settings();
        }
        catch (DirectoryNotFoundException)
        {
            return new Settings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"{path}: {e.Message}", e);
        }

        try
        {
            return JsonSerializer.Deserialize<Settings>(data, Json) ?? new Settings();
        }
        catch (JsonException e)
        {
            throw new LauncherException($"{path}: {e.Message}", e);
        }
    }

    /// <summary>Writes the settings to <paramref name="path"/>.</summary>
    public void Save(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json) + "\n", new UTF8Encoding(false));
    }

    /// <summary>Refuses settings a start would refuse (an unclosed quote in the extra arguments), before a start.</summary>
    public void Validate() => _ = SplitArgs(ExtraArgs);

    /// <summary>
    /// The game's command line for these settings: the in-process flag, then the extra arguments. Throws
    /// <see cref="LauncherException"/> for settings that do not validate.
    /// </summary>
    public IReadOnlyList<string> GameArguments() => [InProcFlag, .. SplitArgs(ExtraArgs)];

    /// <summary>Splits free text on whitespace; double quotes group (Go <c>SplitArgs</c>).</summary>
    public static IReadOnlyList<string> SplitArgs(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var has = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                has = true;
            }
            else if (!quoted && c is ' ' or '\t' or '\n' or '\r')
            {
                if (has)
                {
                    parts.Add(current.ToString());
                    _ = current.Clear();
                    has = false;
                }
            }
            else
            {
                _ = current.Append(c);
                has = true;
            }
        }

        if (quoted)
        {
            throw new LauncherException("extra serve arguments have an unclosed quote");
        }

        if (has)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    /// <summary>The names (no .rws) of the saves in <paramref name="savesDir"/>, newest first (Go <c>ListSaves</c>).</summary>
    public static IReadOnlyList<string> ListSaves(string savesDir)
    {
        ArgumentNullException.ThrowIfNull(savesDir);
        if (!Directory.Exists(savesDir))
        {
            return [];
        }

        return [.. new DirectoryInfo(savesDir).EnumerateFiles("*.rws")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => Path.GetFileNameWithoutExtension(f.Name))];
    }

    /// <summary>The one game id config.json lists (<c>games.&lt;id&gt;</c>, the launch spec the harness reads; Go <c>ConfiguredGame</c>).</summary>
    public static string ConfiguredGame(string configJson)
    {
        ArgumentNullException.ThrowIfNull(configJson);
        try
        {
            using var document = JsonDocument.Parse(configJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("games", out var games)
                || games.ValueKind != JsonValueKind.Object)
            {
                throw new LauncherException("the launch configuration lists 0 games; expected one");
            }

            var ids = games.EnumerateObject().Select(p => p.Name).ToList();
            return ids.Count == 1
                ? ids[0]
                : throw new LauncherException($"the launch configuration lists {ids.Count} games; expected one");
        }
        catch (JsonException e)
        {
            throw new LauncherException("the launch configuration is not valid JSON: " + e.Message, e);
        }
    }
}
