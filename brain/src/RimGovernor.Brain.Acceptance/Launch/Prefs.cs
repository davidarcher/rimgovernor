using System.Collections.Frozen;
using System.Text;
using System.Text.RegularExpressions;

namespace RimGovernor.Brain.Acceptance;

/// <summary>Prefs.xml adjustments for the acceptance profiles (Go <c>prefs.go</c>).</summary>
public static class ProfilePrefs
{
    /// <summary>
    /// What a headless profile's Prefs.xml is set to: no autosaves in practice (a harness never wants the pause and disk
    /// write; the interval is game-days and must stay under ~35791 so that <c>(int)(days * 60000f)</c> does not
    /// overflow: overflowed, the autosaver's threshold goes negative and it saves every tick), the process kept running
    /// unfocused, the smallest window and no eye candy so a headless player computes as little as possible, and a crash
    /// leaving ModsConfig.xml alone so the next launch still loads the mod. The learning helper is off: its readout and
    /// concept popups are player UI a driven game never dismisses.
    /// </summary>
    public static readonly FrozenDictionary<string, string> HeadlessPrefs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["autosaveIntervalDays"] = "1000",
        ["runInBackground"] = "True",
        ["screenWidth"] = "640",
        ["screenHeight"] = "480",
        ["fullscreen"] = "False",
        ["uiScale"] = "1",
        ["customCursorEnabled"] = "False",
        ["plantWindSway"] = "False",
        ["screenShakeIntensity"] = "0",
        ["textureCompression"] = "True",
        ["volumeMaster"] = "0",
        ["resetModsConfigOnCrash"] = "False",
        ["openLogOnWarnings"] = "False",
        ["pauseOnError"] = "False",
        ["adaptiveTrainingEnabled"] = "False",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// What a rendered (windowed) profile's Prefs.xml is set to. RimWorld applies the saved resolution and fullscreen
    /// preference itself at startup, which overrides the -screen-fullscreen / -screen-width / -screen-height launch
    /// arguments, so a player Prefs.xml saved full screen came up full screen whatever the arguments said.
    /// </summary>
    public static readonly FrozenDictionary<string, string> RenderedPrefs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["screenWidth"] = "1280",
        ["screenHeight"] = "720",
        ["fullscreen"] = "False",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private const string PrefsEnd = "</PrefsData>";

    /// <summary>
    /// Rewrites the Prefs.xml at <paramref name="path"/> with <see cref="HeadlessPrefs"/>: each element present is
    /// replaced in place, each absent one is added before the closing tag. Anything else is kept as the player set it.
    /// </summary>
    public static void TrimPrefs(string path) => SetPrefs(path, HeadlessPrefs);

    /// <summary>
    /// Rewrites the Prefs.xml at <paramref name="path"/> with the given elements, the way <see cref="TrimPrefs"/>
    /// applies <see cref="HeadlessPrefs"/>.
    /// </summary>
    public static void SetPrefs(string path, IReadOnlyDictionary<string, string> prefs)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false).GetString(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AcceptanceException($"read {path}: {e.Message}", e);
        }

        string rewritten;
        try
        {
            rewritten = SetPrefsText(text, prefs);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{path}: {e.Message}", e);
        }

        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(rewritten));
    }

    /// <summary>The Prefs.xml text with <paramref name="prefs"/> applied; the text must be a Prefs.xml.</summary>
    public static string SetPrefsText(string text, IReadOnlyDictionary<string, string> prefs)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(prefs);
        if (!text.Contains(PrefsEnd, StringComparison.Ordinal))
        {
            throw new AcceptanceException($"not a Prefs.xml: no {PrefsEnd}");
        }

        var missing = new List<string>();
        foreach ((string name, string value) in prefs)
        {
            string escaped = Regex.Escape(name);
            var pattern = new Regex($"<{escaped}>[^<]*</{escaped}>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            string replacement = $"<{name}>{value}</{name}>";
            if (pattern.IsMatch(text))
            {
                text = pattern.Replace(text, _ => replacement);
            }
            else
            {
                missing.Add($"  {replacement}\n");
            }
        }

        // Deterministic order for the additions: a dictionary walk is not.
        missing.Sort(StringComparer.Ordinal);
        int end = text.IndexOf(PrefsEnd, StringComparison.Ordinal);
        return string.Concat(text.AsSpan(0, end), string.Concat(missing), text.AsSpan(end));
    }
}
