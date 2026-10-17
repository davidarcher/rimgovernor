using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RimGovernor.Brain.Acceptance;

// The doctor's and the speed matrix's own records for the root's files they read: config/config.json (the launcher's
// games) and the installed package's native-manifest.json. They mirror only the fields those readers use.

/// <summary>A root's <c>config.json</c>: the launcher's games by id.</summary>
internal sealed record RootConfigFile
{
    public Dictionary<string, RootConfigGame>? Games { get; init; }
}

/// <summary>One <c>games.&lt;id&gt;</c> section of a root's <c>config.json</c>.</summary>
internal sealed record RootConfigGame
{
    public string? WorkingDir { get; init; }

    public string? Target { get; init; }

    /// <summary>The game's launch arguments, as written (the speed matrix records them whole).</summary>
    public JsonArray? Args { get; init; }
}

/// <summary>An installed package's <c>native-manifest.json</c>.</summary>
internal sealed record NativeManifestFile
{
    public string? Role { get; init; }

    public IReadOnlyList<string?>? Fixtures { get; init; }

    public string? SourceRevision { get; init; }

    public bool SourceDirty { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RootConfigFile))]
[JsonSerializable(typeof(RootConfigGame))]
[JsonSerializable(typeof(NativeManifestFile))]
internal sealed partial class RootConfigJsonContext : JsonSerializerContext;
