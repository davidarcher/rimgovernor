using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RimGovernor.Brain.Acceptance;

// The files the launch, setup and game-host code reads and writes (#3410), as typed records whose serialized shape is
// the file's: config.json, endpoint.json, exit.json, native-manifest.json and the launch snapshots. Absent members stay
// absent (null is not written), so a partial file round-trips.

/// <summary>A configuration directory's <c>config.json</c>: the games the launcher can start.</summary>
public sealed class HarnessConfig
{
    public Dictionary<string, HarnessGame>? Games { get; set; }

    public string? Version { get; set; }

    /// <summary>Members the harness does not read, kept so a rewrite leaves them in place.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One <c>games.&lt;id&gt;</c> entry of <see cref="HarnessConfig"/>: a DirectPath launch of the game copy.</summary>
public sealed class HarnessGame
{
    public List<string>? Args { get; set; }

    public Dictionary<string, string>? Env { get; set; }

    public string? Id { get; set; }

    public string? LaunchMode { get; set; }

    public string? Name { get; set; }

    public string? StopProcessName { get; set; }

    public string? Target { get; set; }

    public string? WorkingDir { get; set; }

    /// <summary>Members the harness does not read, kept so a rewrite leaves them in place.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary><c>endpoint.json</c>'s layout (<see cref="GameHostRecord"/>).</summary>
internal sealed record GameEndpointFile
{
    public int SchemaVersion { get; init; }

    public string? GameId { get; init; }

    public long Pid { get; init; }

    public long PidStartTime { get; init; }

    /// <summary>The launch time, round-trip ("O") formatted in UTC.</summary>
    public string? LaunchedAt { get; init; }

    public long Generation { get; init; }
}

/// <summary><c>exit.json</c>: the exit code of one launch generation, written by the launcher's reaper.</summary>
internal sealed record GameExitFile
{
    public long? Generation { get; init; }

    public int? Code { get; init; }
}

/// <summary>One dependency row of <see cref="SetupPackageManifest"/>.</summary>
public sealed record ManifestDependency
{
    public string Package { get; init; } = "";

    public string Version { get; init; } = "";

    public string File { get; init; } = "";
}

/// <summary>One hashed file of <see cref="SetupPackageManifest"/>.</summary>
public sealed record ManifestFile
{
    public string Path { get; init; } = "";

    public string Sha256 { get; init; } = "";
}

/// <summary>
/// The report's <c>installed_package</c>: what the stale-package check (<see cref="StalePackage.RequireCurrent"/>) found.
/// A member the check did not reach is absent.
/// </summary>
public sealed record InstalledPackageSummary
{
    public string Path { get; init; } = "";

    public bool Checked { get; init; }

    public string? Role { get; init; }

    public IReadOnlyList<string>? Fixtures { get; init; }

    public string? SourceRevision { get; init; }

    public bool? SourceDirty { get; init; }

    public string? Worktree { get; init; }

    public string? Skipped { get; init; }

    public string? Method { get; init; }

    public string? SourceTree { get; init; }

    public string? WorktreeSourceTree { get; init; }

    public string? Note { get; init; }
}

/// <summary>Reads and writes the launch files through their source-generated type info.</summary>
public static class LaunchJson
{
    /// <summary>Indented, relaxed escaping: config.json and the launch snapshots.</summary>
    private static readonly JsonWriterOptions Relaxed = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Indented, default escaping: endpoint.json, exit.json and native-manifest.json.</summary>
    private static readonly JsonWriterOptions Plain = new() { Indented = true };

    internal static LaunchFileJsonContext Files => LaunchFileJsonContext.Default;

    internal static LaunchReportJsonContext Reports => LaunchReportJsonContext.Default;

    /// <summary>A config.json's text, indented with relaxed escaping.</summary>
    public static string Config(HarnessConfig config) => Text(config, Files.HarnessConfig, Relaxed);

    /// <summary>Parses a config.json.</summary>
    public static HarnessConfig ParseConfig(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize(utf8, Files.HarnessConfig) ?? throw new JsonException("config.json is JSON null");

    /// <summary>A launch snapshot (name to value, in the given order), indented with relaxed escaping.</summary>
    public static string Snapshot(Dictionary<string, string> entries) => Text(entries, Files.DictionaryStringString, Relaxed);

    /// <summary>Parses a launch snapshot; a JSON null value reads as "".</summary>
    public static Dictionary<string, string> ParseSnapshot(string text)
    {
        var entries = JsonSerializer.Deserialize(text, Files.DictionaryStringString) ?? throw new JsonException("the snapshot is JSON null");
        return entries.ToDictionary(pair => pair.Key, pair => pair.Value ?? "", StringComparer.Ordinal);
    }

    internal static string Endpoint(GameEndpointFile file) => Text(file, Files.GameEndpointFile, Plain);

    internal static GameEndpointFile ParseEndpoint(string text) =>
        JsonSerializer.Deserialize(text, Files.GameEndpointFile) ?? throw new JsonException("endpoint record is not a JSON object");

    internal static string Exit(GameExitFile file) => Text(file, Files.GameExitFile, Plain);

    internal static GameExitFile? ParseExit(string text) => JsonSerializer.Deserialize(text, Files.GameExitFile);

    /// <summary>native-manifest.json's bytes, indented with default escaping.</summary>
    public static byte[] Manifest(SetupPackageManifest manifest) => System.Text.Encoding.UTF8.GetBytes(Text(manifest, Files.SetupPackageManifest, Plain));

    /// <summary>Parses a native-manifest.json.</summary>
    public static SetupPackageManifest ParseManifest(string text) =>
        JsonSerializer.Deserialize(text, Files.SetupPackageManifest) ?? throw new JsonException(SetupPackage.PackageManifestName + ": not an object");

    /// <summary>The report's installed_package block.</summary>
    public static JsonObject Node(InstalledPackageSummary summary) => (JsonObject)OutputJson.Node(summary, Reports.InstalledPackageSummary);

    /// <summary>A report's world block read back.</summary>
    public static WorldRecord World(JsonObject block) => block.Deserialize(Reports.WorldRecord) ?? new WorldRecord();

    private static string Text<T>(T value, JsonTypeInfo<T> info, JsonWriterOptions options)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, options))
        {
            JsonSerializer.Serialize(writer, value, info);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HarnessConfig))]
[JsonSerializable(typeof(GameEndpointFile))]
[JsonSerializable(typeof(GameExitFile))]
[JsonSerializable(typeof(SetupPackageManifest))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class LaunchFileJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InstalledPackageSummary))]
[JsonSerializable(typeof(WorldRecord))]
internal sealed partial class LaunchReportJsonContext : JsonSerializerContext;
