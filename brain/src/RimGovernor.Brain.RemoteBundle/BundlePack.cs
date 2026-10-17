using System.Xml;
using System.Xml.Linq;
using RimGovernor.Brain.Acceptance;

namespace RimGovernor.Brain.RemoteBundle;

/// <summary>
/// What <see cref="BundlePack.PackAsync"/> packs. It deliberately names dependencies, never a whole profile or worktree.
/// </summary>
public sealed record PackOptions
{
    /// <summary>The tested source checkout.</summary>
    public required string Repo { get; init; }

    /// <summary>The source game directory (read only).</summary>
    public required string GameDir { get; init; }

    /// <summary><c>0Harmony.dll</c> the native build uses.</summary>
    public required string HarmonyDll { get; init; }

    /// <summary>A new private output directory.</summary>
    public required string Output { get; init; }

    /// <summary>The exact <c>Version.txt</c> value.</summary>
    public required string GameVersion { get; init; }

    /// <summary>The exact Harmony version.</summary>
    public required string HarmonyVersion { get; init; }

    /// <summary>The pinned public release the parts are published to.</summary>
    public required Origin Origin { get; init; }

    /// <summary>The age recipient public key.</summary>
    public required string Recipient { get; init; }

    /// <summary>The nonsecret identity rotation label.</summary>
    public required string KeyId { get; init; }

    /// <summary>The maximum encrypted bytes per part.</summary>
    public required long PartBytes { get; init; }

    /// <summary>The pinned 7-Zip and age executables.</summary>
    public required Tools Tools { get; init; }

    /// <summary>Optional generated starts with <c>compatibility.json</c> provenance; empty for none.</summary>
    public string StartsDir { get; init; } = "";

    /// <summary>The complete Harmony runtime mod directory.</summary>
    public string HarmonyMod { get; init; } = "";
}

/// <summary>Packing the game, Harmony and cached starts into the encrypted bundle, and checking cached starts.</summary>
public static class BundlePack
{
    /// <summary>The status <see cref="ValidateStarts"/> returns when saved starts match and should be staged.</summary>
    public const string ReadyToStage = "compatible generated starts ready to stage";

    /// <summary>
    /// The directories the 1.7 install ships that a run needs: the official content (every expansion; the baseline save
    /// records them all) and Rendering, whose shader sources Waylith reads at boot (without it Verse.BaseContent fails
    /// with "Unable to locate shader source"). The flat layout has no _Data or MonoBleedingEdge. Mods, Mods.disabled, the
    /// empty RimWorldWin64_Data remnant and driver cache directories are excluded.
    /// </summary>
    private static readonly string[] GameDirs = ["Data/Core", "Data/Royalty", "Data/Ideology", "Data/Biotech", "Data/Anomaly", "Data/Odyssey", "Rendering"];

    /// <summary>
    /// The files a 1.7 install cannot run without; packing fails when one is missing instead of shipping a bundle that
    /// fails at boot. Assets.zip (the texture and audio archive, about 340 MB) is a regular root file and ships: without
    /// it the world generator throws on null textures.
    /// </summary>
    private static readonly string[] GameRequired = ["RimWorld.exe", "Waylith.dll", "Assembly-CSharp.dll", "Assets.zip", "Version.txt"];

    private static readonly string[] CoreMods = ["ludeon.rimworld.royalty", "ludeon.rimworld.ideology", "ludeon.rimworld.biotech", "ludeon.rimworld.anomaly", "ludeon.rimworld.odyssey", "brrainz.harmony", "davidarcher.rimgovernor.native"];

    /// <summary>
    /// The inclusion list for a 1.7 install: every regular file in the install root (executable, runtime, Waylith and
    /// game assemblies, Assets.zip, notices) except debug symbols, plus the game directories. User profiles, workshop
    /// trees and unrelated mods are excluded.
    /// </summary>
    public static IReadOnlyList<string> GamePaths(string gameDir)
    {
        foreach (var required in GameRequired)
        {
            BundleException.Require(File.Exists(Path.Combine(gameDir, required)), $"not a 1.7 install: {required} is missing");
        }

        var paths = new List<string>();
        foreach (var entry in new DirectoryInfo(gameDir).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (entry is FileInfo && !entry.Name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(entry.Name);
            }
        }

        paths.AddRange(GameDirs);
        return paths;
    }

    /// <summary>Packs the dependencies; writes <c>bundle.draft.json</c>, the inventory and the encrypted parts into the new output directory.</summary>
    public static async Task<Manifest> PackAsync(PackOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        BundleException.Require(
            options.Recipient.Length > 0 && options.KeyId.Length > 0 && options.GameVersion.Length > 0 && options.HarmonyVersion.Length > 0,
            "recipient, key ID and exact dependency versions are required");
        BundleException.Require(
            BundleFiles.IsRepository(options.Origin.Repository) && options.Origin.ReleaseId > 0,
            "pinned origin repository and release ID are required");
        BundleException.Require(!BundleFiles.Exists(options.Output), $"output must be new: {options.Output} exists");
        Directory.CreateDirectory(options.Output);
        var tree = Path.Combine(options.Output, "staging");
        Directory.CreateDirectory(tree);
        try
        {
            foreach (var path in GamePaths(options.GameDir))
            {
                var relative = path.Replace('/', Path.DirectorySeparatorChar);
                try
                {
                    BundleArchive.Materialize(Path.Combine(options.GameDir, relative), Path.Combine(tree, "game", relative));
                }
                catch (BundleException e)
                {
                    throw new BundleException($"game input {path}: {e.Message}", e);
                }
            }

            var version = File.ReadAllText(Path.Combine(tree, "game", "Version.txt"));
            BundleException.Require(version.Trim() == options.GameVersion, "game version does not match Version.txt");
            BundleException.Require(options.HarmonyMod.Length > 0, "-harmony-mod must name the complete Harmony runtime package");
            foreach (var required in new[] { "About/About.xml", "LoadFolders.xml", "1.7/Assemblies/0Harmony.dll", "1.7/Assemblies/HarmonyMod.dll" })
            {
                BundleException.Require(File.Exists(Path.Combine(options.HarmonyMod, required.Replace('/', Path.DirectorySeparatorChar))), $"harmony runtime input {required} is missing");
            }

            var want = BundleFiles.HashFile(options.HarmonyDll).Sha256;
            var got = BundleFiles.HashFile(Path.Combine(options.HarmonyMod, "1.7", "Assemblies", "0Harmony.dll")).Sha256;
            BundleException.Require(want == got, "build Harmony DLL differs from the runtime package");
            BundleArchive.Materialize(options.HarmonyMod, Path.Combine(tree, "harmony"));
            var native = NativeSourceHash(options.Repo);
            var dependencyHash = DependencyIdentity(tree);
            var starts = new Starts(1, options.GameVersion, native, dependencyHash, []);
            Directory.CreateDirectory(Path.Combine(tree, "starts"));
            if (options.StartsDir.Length > 0)
            {
                var supplied = BundleJson.DecodeStarts(File.ReadAllBytes(Path.Combine(options.StartsDir, "compatibility.json")));
                BundleException.Require(
                    supplied.SchemaVersion == 1 && supplied.GameVersion == starts.GameVersion && supplied.NativeSourceSha256 == native && supplied.DependenciesSha256 == dependencyHash,
                    "cached starts incompatible with dependencies/native source; regenerate before packaging");
                CheckGenerated(options.StartsDir, supplied.Generated);
                foreach (var file in supplied.Generated)
                {
                    BundleArchive.Materialize(Path.Combine(options.StartsDir, file.Path), Path.Combine(tree, "starts", file.Path));
                }

                starts = starts with { Generated = supplied.Generated };
            }

            BundleJson.WriteStarts(Path.Combine(tree, "starts", "compatibility.json"), starts);
            var manifest = new Manifest(
                1,
                new Game(options.GameVersion, "windows-x64", false),
                [
                    new Component("game", options.GameVersion, "game/", ""),
                    new Component("harmony", options.HarmonyVersion, "harmony/", ""),
                    new Component("starts", native, "starts/", ""),
                ],
                options.Origin,
                [],
                0,
                new Reference("inventory.json", ""),
                new Encryption("age-v1", options.KeyId));
            var inventory = BundleFiles.InventoryTree(tree);
            manifest = ManifestRules.BindInventory(manifest, inventory);
            ManifestRules.ValidateInventory(inventory, manifest);
            var inventoryFile = Path.Combine(options.Output, "inventory.json");
            BundleJson.WriteInventory(inventoryFile, inventory);
            manifest = manifest with { Inventory = manifest.Inventory with { Sha256 = BundleFiles.HashFile(inventoryFile).Sha256 } };
            manifest = manifest with { Parts = await BundleArchive.EncryptArchiveAsync(options.Tools, tree, options.Output, options.Recipient, options.PartBytes, cancellationToken).ConfigureAwait(false) };
            manifest.Validate(published: false);
            BundleJson.WriteManifest(Path.Combine(options.Output, "bundle.draft.json"), manifest);
            return manifest;
        }
        finally
        {
            if (Directory.Exists(tree))
            {
                Directory.Delete(tree, recursive: true);
            }
        }
    }

    /// <summary>
    /// Refuses unproven generated saves. Fresh starts regenerate through the ordinary harness, which reports its cache
    /// miss. Committed saves come from the tested checkout; a bundle never overwrites them. Returns the status line.
    /// </summary>
    public static string ValidateStarts(string tree, string repo, Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var starts = BundleJson.DecodeStarts(File.ReadAllBytes(Path.Combine(tree, "starts", "compatibility.json")));
        BundleException.Require(
            starts.SchemaVersion == 1 && starts.GameVersion == manifest.Game.Version && BundleFiles.IsDigest(starts.NativeSourceSha256),
            "invalid start compatibility metadata");
        BundleException.Require(starts.DependenciesSha256 == DependencyIdentity(tree), "cached-start dependency identity mismatch");
        CheckGenerated(Path.Combine(tree, "starts"), starts.Generated);
        if (NativeSourceHash(repo) != starts.NativeSourceSha256)
        {
            return "native inputs changed; generated starts will regenerate";
        }

        return starts.Generated.Count > 0 ? ReadyToStage : "compatible; generated starts will regenerate on first use";
    }

    /// <summary>Copies the bundle's generated starts into both profiles of the layout root when <paramref name="status"/> says they are compatible.</summary>
    public static void StageStarts(string tree, string root, string status)
    {
        if (status != ReadyToStage)
        {
            return;
        }

        var starts = BundleJson.DecodeStarts(File.ReadAllBytes(Path.Combine(tree, "starts", "compatibility.json")));
        foreach (var file in starts.Generated)
        {
            foreach (var profile in new[] { "profile", "headless-profile" })
            {
                BundleArchive.Materialize(Path.Combine(tree, "starts", file.Path), Path.Combine(root, profile, "Saves", file.Path));
            }
        }
    }

    /// <summary>The digest of everything but the starts: what a generated start depends on.</summary>
    private static string DependencyIdentity(string tree)
    {
        var lines = new System.Text.StringBuilder();
        foreach (var file in BundleFiles.InventoryTree(tree).Files.Where(f => !f.Path.StartsWith("starts/", StringComparison.Ordinal)))
        {
            lines.Append(ManifestRules.Line(file));
        }

        return BundleFiles.Digest(lines.ToString());
    }

    private static void CheckGenerated(string directory, IReadOnlyList<BundleFile> files)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var bad = !BundleFiles.IsSafePath(file.Path) || file.Path.Contains('/', StringComparison.Ordinal)
                || !file.Path.StartsWith("RimGovernor-debug-", StringComparison.Ordinal) || !file.Path.EndsWith(".rws", StringComparison.Ordinal)
                || !seen.Add(file.Path.ToLowerInvariant());
            BundleException.Require(!bad, $"invalid cached-start name \"{file.Path}\"");
            var path = Path.Combine(directory, file.Path);
            BundleFiles.VerifyFile(path, file.Bytes, file.Sha256);
            CoreSave(path);
        }
    }

    /// <summary>Reads the save's metadata block (its first megabyte) and refuses a start with any mod beyond Core, the expansions, Harmony and the native mod.</summary>
    private static void CoreSave(string fileName)
    {
        var head = new byte[1 << 20];
        int length;
        using (var stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            length = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }

        try
        {
            using var reader = XmlReader.Create(new MemoryStream(head, 0, length), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "meta")
                {
                    continue;
                }

                var meta = (XElement)XNode.ReadFrom(reader);
                var core = false;
                foreach (var id in meta.Element("modIds")?.Elements("li").Select(li => li.Value) ?? [])
                {
                    if (id == "ludeon.rimworld")
                    {
                        core = true;
                    }
                    else if (!CoreMods.Contains(id, StringComparer.Ordinal))
                    {
                        throw new BundleException($"cached start {Path.GetFileName(fileName)} has unsupported mod {id}");
                    }
                }

                BundleException.Require(core, "cached start lacks Core metadata");
                return;
            }

            throw new BundleException("cached-start metadata: EOF");
        }
        catch (XmlException e)
        {
            throw new BundleException($"cached-start metadata: {e.Message}", e);
        }
    }

    private static string NativeSourceHash(string repo)
    {
        try
        {
            return NativeSourceInputs.SourceTreeHash(repo);
        }
        catch (AcceptanceException e)
        {
            throw new BundleException(e.Message, e);
        }
    }
}
