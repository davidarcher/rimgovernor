using System.Security.Cryptography;
using System.Text;
using RimGovernor.Brain.Acceptance;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3296: the native sourceTree hash sits behind a (path, length, last-write time) fingerprint, so an unchanged tree
/// answers from memory or the worktree stamp without reading file contents. Same trust model as rg's SelfBuild: an edit
/// that keeps a file's length and last-write time is deliberately not seen.
/// </summary>
public sealed class SourceTreeFingerprintTests : IDisposable
{
    private static readonly string[] FileInputs = [".md", ".props", ".ps1"];

    private readonly string repo = Path.Combine(Path.GetTempPath(), "rg-source-tree-" + Guid.NewGuid().ToString("N"));

    private int reads;

    public SourceTreeFingerprintTests()
    {
        foreach (var root in NativeSourceInputs.NativeSourceRoots())
        {
            if (FileInputs.Any(ext => root.EndsWith(ext, StringComparison.Ordinal)))
            {
                Write(root, "file " + root);
            }
            else
            {
                Write(root + "/Source.cs", "class C {} // " + root);
            }
        }

        // Build output under an input is not a build input.
        Write("brain/src/RimGovernor.Brain.Domain/obj/Ignored.cs", "ignored");
    }

    public void Dispose()
    {
        if (Directory.Exists(repo))
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private byte[] CountingRead(string path)
    {
        reads++;
        return File.ReadAllBytes(path);
    }

    private string Hash()
    {
        reads = 0;
        return NativeSourceInputs.SourceTreeHash(repo, CountingRead);
    }

    /// <summary>The content hash as build_native_mod.ps1 records it, computed independently of the cache.</summary>
    private string ContentHash()
    {
        var lines = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(repo, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith(".rimgovernor/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }

            lines[relative] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        }

        var text = string.Concat(lines.Select(line => $"{line.Key}\t{line.Value}\n"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void FirstHashReadsEveryInputAndMatchesTheContentHash()
    {
        var hash = Hash();

        Assert.Equal(NativeSourceInputs.NativeSourceRoots().Count, reads);
        Assert.Equal(ContentHash(), hash);
        Assert.Equal(hash, NativeSourceInputs.SourceTreeHash(repo));
    }

    [Fact]
    public void UnchangedTreeAnswersFromMemoryWithoutReadingContents()
    {
        var first = Hash();

        Assert.Equal(first, Hash());
        Assert.Equal(0, reads);
    }

    [Fact]
    public void UnchangedTreeAnswersFromTheStampInAnotherProcess()
    {
        var first = Hash();
        NativeSourceInputs.ForgetCachedHashes();

        Assert.True(File.Exists(Path.Combine(repo, NativeSourceInputs.StampFile)));
        Assert.Equal(first, Hash());
        Assert.Equal(0, reads);
    }

    [Fact]
    public void SizeChangeRecomputes()
    {
        var first = Hash();
        Write("brain/src/RimGovernor.Brain.Policy/Source.cs", "class C { int grown; }");

        var second = Hash();

        Assert.True(reads > 0);
        Assert.NotEqual(first, second);
        Assert.Equal(ContentHash(), second);
    }

    [Fact]
    public void LastWriteTimeChangeRecomputes()
    {
        var first = Hash();
        var path = Path.Combine(repo, "THIRD_PARTY.md");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(-5));

        Assert.Equal(first, Hash());
        Assert.True(reads > 0);
    }

    [Fact]
    public void AddedFileRecomputes()
    {
        var first = Hash();
        Write("integrations/rimgovernor-native/Defs/New.xml", "<Defs/>");

        var second = Hash();

        Assert.True(reads > 0);
        Assert.NotEqual(first, second);
        Assert.Equal(ContentHash(), second);
    }

    [Fact]
    public void RemovedFileRecomputes()
    {
        var extra = Write("integrations/rimgovernor-native/Defs/Extra.xml", "<Defs/>");
        var first = Hash();
        File.Delete(extra);

        var second = Hash();

        Assert.True(reads > 0);
        Assert.NotEqual(first, second);
        Assert.Equal(ContentHash(), second);
    }

    /// <summary>
    /// The documented limit: a content change that keeps the length and last-write time is not detected; the cached
    /// hash stands until the size or time changes.
    /// </summary>
    [Fact]
    public void SameSizeSameTimeContentChangeIsNotRequiredToBeSeen()
    {
        var path = Path.Combine(repo, "THIRD_PARTY.md");
        var first = Hash();
        var time = File.GetLastWriteTimeUtc(path);
        var content = File.ReadAllText(path);
        File.WriteAllText(path, new string('x', content.Length));
        File.SetLastWriteTimeUtc(path, time);

        Assert.Equal(first, Hash());
        Assert.Equal(0, reads);
    }

    [Fact]
    public void MissingInputIsReported()
    {
        File.Delete(Path.Combine(repo, "THIRD_PARTY.md"));

        _ = Assert.Throws<AcceptanceException>(Hash);
    }
}
