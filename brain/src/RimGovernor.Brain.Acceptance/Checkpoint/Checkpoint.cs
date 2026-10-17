using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One bundle's sidecar: what it holds and what it was taken under (the installed package, the Start and the
/// expansions, for whoever loads the save later). Its JSON (<see cref="CheckpointJsonContext"/>) is the sidecar and
/// ring entry format: snake_case keys in declaration order, the identity, prepared and serve blocks left out when empty.
/// </summary>
public sealed class Checkpoint
{
    public string Case { get; set; } = "";

    /// <summary>Names the bundle's directory: "t+7m" for the ring, "failed", or a phase name the window declared.</summary>
    public string Label { get; set; } = "";

    /// <summary>The run-phase offset the bundle was taken at (the label's t+...).</summary>
    public long OffsetMs { get; set; }

    public ulong Tick { get; set; }

    /// <summary>The .rws name.</summary>
    public string Save { get; set; } = "";

    /// <summary>Whether the bundle carries the service store.</summary>
    public bool Store { get; set; }

    [JsonIgnore]
    public JsonObject? Identity { get; set; }

    /// <summary>The start's prepared reply at capture, for the record.</summary>
    [JsonIgnore]
    public JsonObject? Prepared { get; set; }

    /// <summary>The window's serve spec at capture, for the record.</summary>
    [JsonIgnore]
    public JsonObject? Serve { get; set; }

    // The stored forms of the three blocks: an empty block is left out like an absent one.
    [JsonInclude]
    [JsonPropertyName("identity")]
    internal JsonObject? IdentityBlock { get => NonEmpty(Identity); set => Identity = value; }

    [JsonInclude]
    [JsonPropertyName("prepared")]
    internal JsonObject? PreparedBlock { get => NonEmpty(Prepared); set => Prepared = value; }

    [JsonInclude]
    [JsonPropertyName("serve")]
    internal JsonObject? ServeBlock { get => NonEmpty(Serve); set => Serve = value; }

    public string SourceRevision { get; set; } = "";

    /// <summary>Hashes the installed native package's files (<see cref="Checkpoints.PackageHash"/>).</summary>
    public string Package { get; set; } = "";

    /// <summary>Hashes the Start (its kind, save and args).</summary>
    public string Start { get; set; } = "";

    public IReadOnlyList<string> Expansions { get; set; } = [];

    /// <summary>Which path took the save: "bridge" (lifecycle_save on the harness session), "service" (/api/lifecycle/save) or "saver".</summary>
    public string Through { get; set; } = "";

    public string At { get; set; } = "";

    public long WallMs { get; set; }

    /// <summary>The bundle directory, set on read and capture, not stored.</summary>
    [JsonIgnore]
    public string Path { get; set; } = "";

    /// <summary>The sidecar's indented JSON text.</summary>
    internal string ToJsonText() => JsonSerializer.Serialize(this, CheckpointJsonContext.Default.Checkpoint);

    private static JsonObject? NonEmpty(JsonObject? block) => block is { Count: > 0 } ? block : null;
}

/// <summary>What a bundle was taken under: the installed native package, the Start and the profile's expansions.</summary>
public sealed record Fingerprint(string Package, string Start, IReadOnlyList<string> Expansions);

/// <summary>
/// The index a checkpoint directory keeps (<see cref="Checkpoints.CheckpointRingFile"/>): the bundles present, the
/// window's outcome and its newest entry. Its JSON (<see cref="CheckpointJsonContext"/>) is ring.json: snake_case keys
/// in declaration order, failed and failed_output left out when absent.
/// </summary>
public sealed class Ring
{
    public string Case { get; set; } = "";

    /// <summary>The bundles by label, oldest first by offset.</summary>
    public IList<Checkpoint> Entries { get; set; } = [];

    /// <summary>The failed window's final bundle, when one was taken.</summary>
    public Checkpoint? Failed { get; set; }

    /// <summary>Where the window failed; <see cref="FailedTick"/> is 0 when the failure could not be observed.</summary>
    public ulong FailedTick { get; set; }

    public long FailedOffsetMs { get; set; }

    /// <summary>The newest entry's label; "" when the ring is empty.</summary>
    public string Next { get; set; } = "";

    /// <summary>The window's.</summary>
    public string SourceRevision { get; set; } = "";

    /// <summary>The failed window's output directory.</summary>
    [JsonIgnore]
    public string FailedOutput { get; set; } = "";

    // The stored form: a window that did not fail leaves the field out.
    [JsonInclude]
    [JsonPropertyName("failed_output")]
    internal string? FailedOutputField { get => FailedOutput.Length > 0 ? FailedOutput : null; set => FailedOutput = value ?? ""; }

    /// <summary>Reads dir's index; a missing ring is null.</summary>
    public static Ring? Read(string dir)
    {
        var file = System.IO.Path.Combine(dir, Checkpoints.CheckpointRingFile);
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        Ring r;
        try
        {
            r = JsonSerializer.Deserialize(text, CheckpointJsonContext.Default.Ring) ?? throw new JsonException("the index is JSON null");
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"{Checkpoints.CheckpointRingFile}: {e.Message}", e);
        }

        foreach (var c in r.Entries)
        {
            c.Path = System.IO.Path.Combine(dir, c.Label);
        }

        if (r.Failed is { } failed)
        {
            failed.Path = System.IO.Path.Combine(dir, failed.Label);
        }

        return r;
    }

    /// <summary>Persists the index under dir.</summary>
    public void Write(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(System.IO.Path.Combine(dir, Checkpoints.CheckpointRingFile), JsonSerializer.Serialize(this, CheckpointJsonContext.Default.Ring));
    }

    /// <summary>The entry labelled label, null when absent.</summary>
    public Checkpoint? Entry(string label) => Entries.FirstOrDefault(e => string.Equals(e.Label, label, StringComparison.Ordinal));
}

/// <summary>The checkpoint sidecar and ring index JSON: source-generated, snake_case, indented, absent blocks left out.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Ring))]
[JsonSerializable(typeof(Checkpoint))]
internal sealed partial class CheckpointJsonContext : JsonSerializerContext;
