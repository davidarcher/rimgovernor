using System.Globalization;
using System.Text;

namespace RimGovernor.Brain.RemoteBundle;

/// <summary>Reading and validating the manifest and its inventory.</summary>
public static class ManifestRules
{
    /// <summary>The largest encrypted part a release asset may carry.</summary>
    public const long MaxPartBytes = 1_900_000_000;

    /// <summary>Reads the manifest at <paramref name="fileName"/> whose bytes must hash to <paramref name="expected"/>; every part must carry its release asset ID.</summary>
    public static Manifest ReadManifest(string fileName, string expected) => Read(fileName, expected, published: true);

    /// <summary>
    /// Reads a manifest that is not published yet: release asset IDs may be missing, for offline verification before
    /// publication. Bootstrap always uses <see cref="ReadManifest"/>.
    /// </summary>
    public static Manifest ReadLocalDraft(string fileName, string expected) => Read(fileName, expected, published: false);

    /// <summary>Reads an unpublished manifest without a pinned digest (the maintainer's own draft, for <c>publish</c>).</summary>
    public static Manifest ReadDraft(string fileName) => BundleJson.DecodeManifest(File.ReadAllBytes(fileName));

    /// <summary>Validates the manifest; <paramref name="published"/> requires distinct positive asset IDs.</summary>
    public static void Validate(this Manifest manifest, bool published)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        BundleException.Require(
            manifest.SchemaVersion == 1 && manifest.Game.Version.Length > 0 && manifest.Game.Platform == "windows-x64",
            "bundle requires schema 1, exact game version and Core-only windows-x64");
        BundleException.Require(
            BundleFiles.IsRepository(manifest.Origin.Repository) && manifest.Origin.ReleaseId > 0,
            "bundle requires pinned origin repository and release ID");
        BundleException.Require(
            manifest.Encryption.Format == "age-v1" && manifest.Encryption.KeyId.Length > 0,
            "bundle requires age-v1 encryption and key_id");
        BundleException.Require(
            manifest.UnpackedBytes >= 0 && manifest.UnpackedBytes <= 1L << 40 && BundleFiles.IsSafePath(manifest.Inventory.Path) && BundleFiles.IsDigest(manifest.Inventory.Sha256),
            "invalid inventory reference or unpacked size");
        BundleException.Require(
            manifest.Parts.Count is > 0 and <= 1024 && manifest.Components.Count > 0,
            "empty parts or components");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<long>();
        foreach (var part in manifest.Parts)
        {
            var key = part.Name.ToLowerInvariant();
            var bad = !BundleFiles.IsSafePath(part.Name) || part.Name.Contains('/', StringComparison.Ordinal) || part.Bytes <= 0
                || part.Bytes > MaxPartBytes || !BundleFiles.IsDigest(part.Sha256) || names.Contains(key)
                || (published && (part.AssetId <= 0 || ids.Contains(part.AssetId)));
            BundleException.Require(!bad, $"invalid or duplicate archive part \"{part.Name}\"");
            names.Add(key);
            ids.Add(part.AssetId);
        }

        names.Clear();
        for (var i = 0; i < manifest.Components.Count; i++)
        {
            var component = manifest.Components[i];
            var bad = component.Name.Length == 0 || component.Version.Length == 0 || names.Contains(component.Name)
                || !component.PathPrefix.EndsWith('/') || !BundleFiles.IsSafePath(component.PathPrefix[..^1])
                || !BundleFiles.IsDigest(component.Sha256);
            BundleException.Require(!bad, $"invalid component \"{component.Name}\"");
            names.Add(component.Name);
            for (var j = 0; j < i; j++)
            {
                var a = component.PathPrefix.ToLowerInvariant();
                var b = manifest.Components[j].PathPrefix.ToLowerInvariant();
                BundleException.Require(!a.StartsWith(b, StringComparison.Ordinal) && !b.StartsWith(a, StringComparison.Ordinal), "overlapping component prefixes");
            }
        }
    }

    /// <summary>Reads the inventory the manifest references from under <paramref name="root"/>, which must not escape it, and validates it.</summary>
    public static Inventory ReadInventory(string root, Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var fileName = Path.Combine(root, manifest.Inventory.Path.Replace('/', Path.DirectorySeparatorChar));
        var resolved = BundleFiles.ResolveSource(fileName);
        var resolvedRoot = BundleFiles.ResolveSource(root);
        BundleException.Require(BundleFiles.Within(resolvedRoot, resolved), "inventory reference escapes evidence root");
        var bytes = File.ReadAllBytes(fileName);
        BundleException.Require(BundleFiles.Digest(bytes) == manifest.Inventory.Sha256, "inventory digest mismatch");
        var inventory = BundleJson.DecodeInventory(bytes);
        ValidateInventory(inventory, manifest);
        return inventory;
    }

    /// <summary>Checks the inventory against the manifest: sorted unique safe paths, sizes, component ownership and digests.</summary>
    public static void ValidateInventory(Inventory inventory, Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(manifest);
        BundleException.Require(inventory.SchemaVersion == 1, "inventory requires schema 1 and files");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var previous = "";
        long total = 0;
        var componentLines = manifest.Components.Select(_ => new StringBuilder()).ToArray();
        foreach (var file in inventory.Files)
        {
            var bad = !BundleFiles.IsSafePath(file.Path) || string.CompareOrdinal(file.Path, previous) <= 0
                || seen.Contains(file.Path.ToLowerInvariant()) || file.Bytes < 0 || !BundleFiles.IsDigest(file.Sha256);
            BundleException.Require(!bad, $"invalid, unsorted or duplicate inventory file \"{file.Path}\"");
            seen.Add(file.Path.ToLowerInvariant());
            previous = file.Path;
            BundleException.Require(file.Bytes <= manifest.UnpackedBytes - total, "inventory size exceeds manifest");
            total += file.Bytes;
            var owner = -1;
            for (var i = 0; i < manifest.Components.Count; i++)
            {
                if (file.Path.StartsWith(manifest.Components[i].PathPrefix, StringComparison.Ordinal))
                {
                    owner = i;
                    break;
                }
            }

            BundleException.Require(owner >= 0, $"file {file.Path} has no component");
            componentLines[owner].Append(Line(file));
        }

        foreach (var file in inventory.Files)
        {
            for (var directory = ParentOf(file.Path); directory != "."; directory = ParentOf(directory))
            {
                BundleException.Require(!seen.Contains(directory.ToLowerInvariant()), $"file/directory collision {directory}");
            }
        }

        BundleException.Require(total == manifest.UnpackedBytes, "unpacked size mismatch");
        for (var i = 0; i < manifest.Components.Count; i++)
        {
            BundleException.Require(
                BundleFiles.Digest(componentLines[i].ToString()) == manifest.Components[i].Sha256,
                $"component {manifest.Components[i].Name} inventory digest mismatch");
        }
    }

    /// <summary>Fills each component's digest and the unpacked size from the inventory.</summary>
    public static Manifest BindInventory(Manifest manifest, Inventory inventory)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(inventory);
        var components = new List<Component>();
        foreach (var component in manifest.Components)
        {
            var lines = new StringBuilder();
            foreach (var file in inventory.Files.Where(f => f.Path.StartsWith(component.PathPrefix, StringComparison.Ordinal)))
            {
                lines.Append(Line(file));
            }

            components.Add(component with { Sha256 = BundleFiles.Digest(lines.ToString()) });
        }

        return manifest with { Components = components, UnpackedBytes = inventory.Files.Sum(f => f.Bytes) };
    }

    /// <summary>The inventory line a digest is taken over: path, size and digest, tab separated.</summary>
    internal static string Line(BundleFile file) => string.Create(CultureInfo.InvariantCulture, $"{file.Path}\t{file.Bytes}\t{file.Sha256}\n");

    /// <summary>The parent directory of a clean relative slash path: "." for a bare name.</summary>
    internal static string ParentOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "." : path[..slash];
    }

    private static Manifest Read(string fileName, string expected, bool published)
    {
        var bytes = File.ReadAllBytes(fileName);
        BundleException.Require(BundleFiles.IsDigest(expected) && BundleFiles.Digest(bytes) == expected, "bundle manifest digest mismatch");
        var manifest = BundleJson.DecodeManifest(bytes);
        manifest.Validate(published);
        return manifest;
    }
}
