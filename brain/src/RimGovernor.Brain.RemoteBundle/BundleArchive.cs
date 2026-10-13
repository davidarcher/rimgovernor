using System.Globalization;
using System.Text;

namespace RimGovernor.Brain.RemoteBundle;

/// <summary>Staging, encrypting, verifying and extracting the bundle archive (Go <c>archive.go</c>).</summary>
public static class BundleArchive
{
    private const string AgeHeader = "age-encryption.org/v1\n";

    /// <summary>
    /// Copies a selected dependency subtree, following source junctions, but creates only regular destination files.
    /// Cycles and destination overlap fail (Go <c>Materialize</c>).
    /// </summary>
    public static void Materialize(string source, string dest)
    {
        source = Path.GetFullPath(BundleFiles.ResolveSource(source));
        dest = BundleFiles.ResolveDestination(dest);
        BundleException.Require(!BundleFiles.Within(source, dest) && !BundleFiles.Within(dest, source), "source and staging destination overlap");
        CopyMaterialized(source, dest, new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Writes one encrypted 7z stream, then immutable digest-named parts. The caller owns a fresh output directory; no
    /// existing file is replaced (Go <c>EncryptArchive</c>).
    /// </summary>
    public static async Task<IReadOnlyList<Part>> EncryptArchiveAsync(Tools tools, string tree, string output, string recipient, long partBytes, CancellationToken cancellationToken)
    {
        BundleException.Require(partBytes > 0 && partBytes <= ManifestRules.MaxPartBytes, "part size must be 1..1900000000 bytes");
        var archive = Path.Combine(output, "payload.7z");
        var encrypted = archive + ".age";
        try
        {
            await ToolProcess.RunPublicAsync(tree, tools.SevenZip, ["a", "-t7z", "-mx=7", "-mmt=2", "-mtc=off", "-mta=off", "-mtm=off", archive, "."], cancellationToken).ConfigureAwait(false);
            await ToolProcess.RunPublicAsync("", tools.Age, ["-r", recipient, "-o", encrypted, archive], cancellationToken).ConfigureAwait(false);
            var parts = new List<Part>();
            await using var input = new FileStream(encrypted, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
            for (var index = 1; ; index++)
            {
                var temporary = Path.Combine(output, string.Create(CultureInfo.InvariantCulture, $"part-{index:D4}.tmp"));
                long copied;
                await using (var part = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                {
                    copied = await CopyAtMostAsync(input, part, partBytes, cancellationToken).ConfigureAwait(false);
                }

                if (copied == 0)
                {
                    File.Delete(temporary);
                    break;
                }

                var (digest, _) = BundleFiles.HashFile(temporary);
                var name = string.Create(CultureInfo.InvariantCulture, $"bundle-{digest}-{index:D4}.7z.age.part");
                File.Move(temporary, Path.Combine(output, name), overwrite: false);
                parts.Add(new Part(0, name, copied, digest));
                if (copied < partBytes)
                {
                    break;
                }
            }

            return parts;
        }
        finally
        {
            File.Delete(archive);
            File.Delete(encrypted);
        }
    }

    /// <summary>
    /// Checks every part's size and digest and that the concatenation starts with the age v1 header, across part
    /// boundaries: a mislabeled plaintext archive must never enter a public release or cache (Go <c>VerifyParts</c>).
    /// </summary>
    public static void VerifyParts(string directory, Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        foreach (var part in manifest.Parts)
        {
            try
            {
                BundleFiles.VerifyFile(Path.Combine(directory, part.Name), part.Bytes, part.Sha256);
            }
            catch (BundleException e)
            {
                throw new BundleException($"archive part {part.Name}: {e.Message}", e);
            }
        }

        var header = new List<byte>(AgeHeader.Length);
        foreach (var part in manifest.Parts)
        {
            var remaining = AgeHeader.Length - header.Count;
            if (remaining == 0)
            {
                break;
            }

            using var stream = new FileStream(Path.Combine(directory, part.Name), FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[remaining];
            var read = stream.ReadAtLeast(buffer, 1, throwOnEndOfStream: false);
            header.AddRange(buffer.AsSpan(0, Math.Min(read, remaining)).ToArray());
        }

        BundleException.Require(
            Encoding.Latin1.GetString([.. header]) == AgeHeader,
            "archive is not age-v1 ciphertext; refusing plaintext or unsupported encryption");
    }

    /// <summary>
    /// Checks 7-Zip's technical listing before any extraction. Unknown link metadata fails closed; file sizes and paths
    /// must exactly match the inventory (Go <c>ValidateListing</c>).
    /// </summary>
    public static void ValidateListing(string listing, Inventory inventory)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(inventory);
        var want = inventory.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in inventory.Files)
        {
            for (var parent = ManifestRules.ParentOf(file.Path); parent != "."; parent = ManifestRules.ParentOf(parent))
            {
                directories.Add(parent);
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var text = listing.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        foreach (var block in text.Split("\n\n"))
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in block.Split('\n'))
            {
                var cut = line.IndexOf(" = ", StringComparison.Ordinal);
                if (cut < 0)
                {
                    continue;
                }

                var key = line[..cut];
                BundleException.Require(fields.TryAdd(key, line[(cut + 3)..]), $"duplicate archive metadata {key}");
            }

            var path = fields.GetValueOrDefault("Path", "").Replace('\\', '/');
            BundleException.Require(path.Length > 0, "archive listing lacks path");
            BundleFiles.SafePath(path);
            BundleException.Require(seen.Add(path.ToLowerInvariant()), $"duplicate archive entry {path}");
            foreach (var (key, value) in fields)
            {
                BundleException.Require(!((key.Contains("link", StringComparison.OrdinalIgnoreCase) || key == "Reparse") && value.Length > 0), $"archive link {path}");
            }

            var attributes = fields.GetValueOrDefault("Attributes", "");
            BundleException.Require(!attributes.Contains('l', StringComparison.Ordinal) && !attributes.Contains('L', StringComparison.Ordinal), $"archive link attributes {path}");
            if (fields.GetValueOrDefault("Folder", "") == "+" || attributes.StartsWith('D'))
            {
                BundleException.Require(directories.Contains(path), $"unlisted archive directory {path}");
                continue;
            }

            var sized = long.TryParse(fields.GetValueOrDefault("Size", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            BundleException.Require(want.TryGetValue(path, out var file) && sized && size == file.Bytes, $"unlisted or wrong-size archive entry {path}");
            want.Remove(path);
        }

        BundleException.Require(want.Count == 0, $"archive listing missing {want.Count} files");
    }

    /// <summary>
    /// Verifies the parts, decrypts and lists the archive, validates the listing against the inventory, extracts and
    /// verifies the tree, then moves it to <paramref name="dest"/>, which must not exist (Go <c>Extract</c>).
    /// </summary>
    public static async Task ExtractAsync(Tools tools, string partsDirectory, string dest, string identity, Manifest manifest, Inventory inventory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(inventory);
        VerifyParts(partsDirectory, manifest);
        BundleException.Require(!BundleFiles.Exists(dest), $"extraction destination must not exist: {dest}");
        var work = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dest)) ?? ".", "bundle-decrypt-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)));
        Directory.CreateDirectory(work);
        try
        {
            var cipher = Path.Combine(work, "payload.age");
            await using (var output = new FileStream(cipher, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                foreach (var part in manifest.Parts)
                {
                    await using var input = new FileStream(Path.Combine(partsDirectory, part.Name), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }
            }

            var archive = Path.Combine(work, "payload.7z");
            await ToolProcess.RunPublicAsync("", tools.Age, ["-d", "-i", identity, "-o", archive, cipher], cancellationToken).ConfigureAwait(false);
            var listing = await ToolProcess.CaptureAsync("", tools.SevenZip, ["l", "-slt", "-ba", "-sccUTF-8", archive], scrub: true, cancellationToken).ConfigureAwait(false);
            BundleException.Require(listing.ExitCode == 0, $"list decrypted archive: exit status {listing.ExitCode}");
            ValidateListing(listing.Stdout, inventory);
            var tree = Path.Combine(work, "tree");
            await ToolProcess.RunPublicAsync("", tools.SevenZip, ["x", "-y", "-o" + tree, archive], cancellationToken).ConfigureAwait(false);
            BundleFiles.VerifyTree(tree, inventory);
            Directory.Move(tree, dest);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static void CopyMaterialized(string source, string dest, HashSet<string> ancestors)
    {
        var real = BundleFiles.ResolveSource(source);
        if (Directory.Exists(real))
        {
            var key = real.ToLowerInvariant();
            BundleException.Require(ancestors.Add(key), $"source junction cycle at {source}");
            try
            {
                Directory.CreateDirectory(dest);
                foreach (var entry in new DirectoryInfo(real).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
                {
                    BundleFiles.SafePath(entry.Name);
                    try
                    {
                        CopyMaterialized(Path.Combine(real, entry.Name), Path.Combine(dest, entry.Name), ancestors);
                    }
                    catch (BundleException e)
                    {
                        throw new BundleException($"copy {Path.Combine(real, entry.Name)}: {e.Message}", e);
                    }
                }
            }
            finally
            {
                ancestors.Remove(key);
            }

            return;
        }

        var info = new FileInfo(real);
        BundleException.Require(info.Exists && BundleFiles.IsRegular(info), $"nonregular source {source}");
        Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? ".");
        using var input = new FileStream(real, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        input.CopyTo(output);
    }

    /// <summary>Copies up to <paramref name="limit"/> bytes; returns how many arrived (fewer at the end of the input).</summary>
    private static async Task<long> CopyAtMostAsync(Stream input, Stream output, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[1 << 20];
        long copied = 0;
        while (copied < limit)
        {
            var want = (int)Math.Min(buffer.Length, limit - copied);
            var read = await input.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
        }

        return copied;
    }
}
