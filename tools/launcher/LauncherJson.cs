using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimGovernor.Launcher;

/// <summary>The launcher's JSON dialect: the page's camelCase names, absent rather than null, case-insensitive on read.</summary>
public static class LauncherJson
{
    /// <summary>Compact form for the page and the game's HTTP calls.</summary>
    public static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The settings file's form: the same names, indented.</summary>
    public static readonly JsonSerializerOptions Indented = new(Compact) { WriteIndented = true };
}
