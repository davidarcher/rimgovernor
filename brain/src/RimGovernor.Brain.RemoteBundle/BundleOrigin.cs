using System.Globalization;
using System.Text.Json;

namespace RimGovernor.Brain.RemoteBundle;

/// <summary>
/// The authenticated metadata and download boundary of the pinned release. The GitHub CLI owns token handling; tokens
/// are never serialized or passed to child build processes (Go <c>OriginClient</c>), over the <c>gh</c> CLI (Go
/// <c>GitHub</c>).
/// </summary>
public static class GitHubOriginClient
{
    /// <summary>Reads the pinned public repository's release and all of its assets.</summary>
    public static async Task<Release> ReleaseAsync(Origin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        BundleException.Require(BundleFiles.IsRepository(origin.Repository) && origin.ReleaseId > 0, "invalid origin");
        var repository = await Api(["api", "repos/" + origin.Repository], "read origin repository metadata", cancellationToken).ConfigureAwait(false);
        using (var document = JsonDocument.Parse(repository))
        {
            var root = document.RootElement;
            var fullName = root.TryGetProperty("full_name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
            var isPrivate = root.TryGetProperty("private", out var priv) && priv.ValueKind == JsonValueKind.True;
            BundleException.Require(fullName == origin.Repository && !isPrivate, $"origin must be the pinned public repository {origin.Repository}");
        }

        var releaseId = origin.ReleaseId.ToString(CultureInfo.InvariantCulture);
        var release = BundleJson.DecodeRelease(await Api(["api", $"repos/{origin.Repository}/releases/{releaseId}"], "read pinned release", cancellationToken).ConfigureAwait(false));
        BundleException.Require(release.Id == origin.ReleaseId && !release.Draft, "origin release is missing, draft or mismatched");
        var pages = BundleJson.DecodeAssetPages(await Api(["api", "--paginate", "--slurp", $"repos/{origin.Repository}/releases/{releaseId}/assets?per_page=100"], "read release assets", cancellationToken).ConfigureAwait(false));
        return release with { Assets = [.. pages.SelectMany(page => page)] };
    }

    /// <summary>Streams one part's release asset into <paramref name="destination"/>.</summary>
    public static Task DownloadAsync(Origin origin, Part part, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(part);
        var id = part.AssetId.ToString(CultureInfo.InvariantCulture);
        return ToolProcess.PipeOutputAsync(
            "gh",
            ["api", "-H", "Accept: application/octet-stream", $"repos/{origin.Repository}/releases/assets/{id}"],
            destination,
            $"download asset {id}",
            cancellationToken);
    }

    private static async Task<byte[]> Api(string[] arguments, string failure, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await ToolProcess.PipeOutputAsync("gh", arguments, buffer, failure, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }
}

/// <summary>Restoring the ciphertext cache from, and publishing parts to, the pinned release (Go <c>origin.go</c>).</summary>
public static class BundleOrigin
{
    /// <summary>The cache directory name for a manifest digest.</summary>
    public static string CacheKey(string digest) => "rimgovernor-bundle-v1-windows-x64-" + digest;

    /// <summary>Confirms the pinned release is published and carries every part with its expected size.</summary>
    public static async Task VerifyOriginAsync(Manifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var release = await GitHubOriginClient.ReleaseAsync(manifest.Origin, cancellationToken).ConfigureAwait(false);
        BundleException.Require(release.Id == manifest.Origin.ReleaseId && !release.Draft, "release identity mismatch or unpublished release");
        foreach (var part in manifest.Parts)
        {
            BundleException.Require(
                release.Assets.Any(a => a.Id == part.AssetId && a.Name == part.Name && a.Size == part.Bytes),
                $"asset {part.AssetId} ({part.Name}) is not in pinned release with expected size");
        }
    }

    /// <summary>
    /// Verifies cache hits too: a corrupt cache is an error, never a fallback. Only encrypted parts are written to the
    /// cache directory; manifest and inventory stay in the trusted input directory and plaintext goes to a separate
    /// per-job layout. Returns whether the cache already held the parts (Go <c>Restore</c>).
    /// </summary>
    public static async Task<bool> RestoreAsync(string cache, string digest, Manifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        BundleException.Require(BundleFiles.IsDigest(digest), "invalid cache identity");
        await VerifyOriginAsync(manifest, cancellationToken).ConfigureAwait(false);
        var directory = Path.Combine(cache, CacheKey(digest));
        if (BundleFiles.Exists(directory))
        {
            BundleArchive.VerifyParts(directory, manifest);
            return true;
        }

        Directory.CreateDirectory(cache);
        var temporary = Path.Combine(cache, "download-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)));
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (var part in manifest.Parts)
            {
                var target = Path.Combine(temporary, part.Name);
                await using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                {
                    await GitHubOriginClient.DownloadAsync(manifest.Origin, part, new LimitedStream(file, part.Bytes), cancellationToken).ConfigureAwait(false);
                }

                BundleFiles.VerifyFile(target, part.Bytes, part.Sha256);
            }

            BundleArchive.VerifyParts(temporary, manifest);
            Directory.Move(temporary, directory);
            return false;
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
    }

    /// <summary>
    /// The explicit maintainer operation. Uploads never clobber; the final manifest is produced only after the asset IDs
    /// are read back from the origin. Returns the published manifest's file name (Go <c>Publish</c>).
    /// </summary>
    public static async Task<string> PublishAsync(string directory, Manifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate(published: false);
        BundleArchive.VerifyParts(directory, manifest);
        ManifestRules.ReadInventory(directory, manifest);
        var release = await GitHubOriginClient.ReleaseAsync(manifest.Origin, cancellationToken).ConfigureAwait(false);

        async Task Upload(string file)
        {
            var result = await ToolProcess.CaptureAsync("", "gh", ["release", "upload", release.Tag, file, "--repo", manifest.Origin.Repository], scrub: false, cancellationToken).ConfigureAwait(false);
            BundleException.Require(result.ExitCode == 0, $"immutable asset upload: exit status {result.ExitCode}: {result.Combined}");
        }

        foreach (var part in manifest.Parts)
        {
            await Upload(Path.Combine(directory, part.Name)).ConfigureAwait(false);
        }

        var inventoryName = "inventory-" + manifest.Inventory.Sha256 + ".json";
        BundleArchive.Materialize(Path.Combine(directory, manifest.Inventory.Path), Path.Combine(directory, inventoryName));
        manifest = manifest with { Inventory = manifest.Inventory with { Path = inventoryName } };
        await Upload(Path.Combine(directory, inventoryName)).ConfigureAwait(false);
        release = await GitHubOriginClient.ReleaseAsync(manifest.Origin, cancellationToken).ConfigureAwait(false);
        var parts = new List<Part>();
        foreach (var part in manifest.Parts)
        {
            var asset = release.Assets.FirstOrDefault(a => a.Name == part.Name && a.Size == part.Bytes);
            parts.Add(asset is null ? part : part with { AssetId = asset.Id });
        }

        manifest = manifest with { Parts = parts };
        manifest.Validate(published: true);
        var temporary = Path.Combine(directory, "bundle.published.json");
        BundleJson.WriteManifest(temporary, manifest);
        var (digest, _) = BundleFiles.HashFile(temporary);
        var name = "bundle-" + digest + ".json";
        File.Move(temporary, Path.Combine(directory, name), overwrite: true);
        await Upload(Path.Combine(directory, name)).ConfigureAwait(false);
        return name;
    }
}

/// <summary>A write-only stream that fails a write past the declared size (Go <c>limitedWriter</c>).</summary>
internal sealed class LimitedStream(Stream inner, long limit) : Stream
{
    private long _remaining = limit;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Take(buffer.Length);
        inner.Write(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Take(buffer.Length);
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Take(int count)
    {
        BundleException.Require(count <= _remaining, "asset exceeds declared size");
        _remaining -= count;
    }
}
