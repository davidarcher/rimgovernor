using System.Text.Json.Serialization;

namespace RimGovernor.Brain.RemoteBundle;

/// <summary>A file named by relative path and its SHA-256.</summary>
public sealed record Reference(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>One file of an inventory.</summary>
public sealed record BundleFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>The sorted file list of the unpacked tree.</summary>
public sealed record Inventory(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("files")] IReadOnlyList<BundleFile> Files);

/// <summary>A versioned slice of the tree under one path prefix, with the digest of its inventory lines.</summary>
public sealed record Component(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("path_prefix")] string PathPrefix,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>One encrypted archive part and the release asset that carries it.</summary>
public sealed record Part(
    [property: JsonPropertyName("asset_id")] long AssetId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>The pinned public release the parts are published to.</summary>
public sealed record Origin(
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("release_id")] long ReleaseId);

/// <summary>The game the bundle was cut from.</summary>
public sealed record Game(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("core_only")] bool CoreOnly);

/// <summary>The encryption format and the nonsecret identity rotation label.</summary>
public sealed record Encryption(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("key_id")] string KeyId);

/// <summary>The bundle manifest, schema 1.</summary>
public sealed record Manifest(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("game")] Game Game,
    [property: JsonPropertyName("components")] IReadOnlyList<Component> Components,
    [property: JsonPropertyName("origin")] Origin Origin,
    [property: JsonPropertyName("parts")] IReadOnlyList<Part> Parts,
    [property: JsonPropertyName("unpacked_bytes")] long UnpackedBytes,
    [property: JsonPropertyName("inventory")] Reference Inventory,
    [property: JsonPropertyName("encryption")] Encryption Encryption);

/// <summary>The cached-start compatibility record shipped under <c>starts/</c>.</summary>
public sealed record Starts(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("game_version")] string GameVersion,
    [property: JsonPropertyName("native_source_sha256")] string NativeSourceSha256,
    [property: JsonPropertyName("dependencies_sha256")] string DependenciesSha256,
    [property: JsonPropertyName("generated")] IReadOnlyList<BundleFile> Generated);

/// <summary>
/// Supplied by the trusted default-branch workflow after review of the exact tested commit; never accepted from the
/// tested checkout or a pull request.
/// </summary>
public sealed record Trust(
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("tested_commit")] string TestedCommit,
    [property: JsonPropertyName("workflow_commit")] string WorkflowCommit,
    [property: JsonPropertyName("event")] string Event);

/// <summary>One release asset; the wire shape is GitHub's.</summary>
public sealed record Asset(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long Size);

/// <summary>A GitHub release as <see cref="OriginClient"/> reports it.</summary>
public sealed record Release(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("tag_name")] string Tag,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("assets")] IReadOnlyList<Asset> Assets);

/// <summary>What bootstrap measured and produced; written to <c>bootstrap.json</c> in the job directory.</summary>
public sealed record BootstrapReport(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("bundle_sha256")] string BundleSha256,
    [property: JsonPropertyName("cache_key")] string CacheKey,
    [property: JsonPropertyName("cache_hit")] bool CacheHit,
    [property: JsonPropertyName("os")] string Os,
    [property: JsonPropertyName("arch")] string Arch,
    [property: JsonPropertyName("cpu_count")] int CpuCount,
    [property: JsonPropertyName("image_version")] string ImageVersion,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("starts")] string Starts,
    [property: JsonPropertyName("restore_ms")] long RestoreMs,
    [property: JsonPropertyName("extract_ms")] long ExtractMs,
    [property: JsonPropertyName("build_ms")] long BuildMs,
    [property: JsonPropertyName("total_ms")] long TotalMs,
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("free_disk_bytes")] ulong FreeDiskBytes);
