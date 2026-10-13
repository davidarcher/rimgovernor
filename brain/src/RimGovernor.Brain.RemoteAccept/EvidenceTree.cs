using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.RemoteAccept;

/// <summary>Identity predicates, path validity and digests shared by the evidence graph (Go json.go helpers).</summary>
internal static class EvidenceRules
{
    /// <summary>A lower-case 40 hex git object id (Go <c>oid</c> regexp).</summary>
    public static bool IsOid(string s) => IsLowerHex(s, 40);

    /// <summary>A lower-case 64 hex SHA-256 digest (Go <c>digest</c> regexp).</summary>
    public static bool IsDigest(string s) => IsLowerHex(s, 64);

    /// <summary>The <c>owner/name</c> repository shape (Go <c>repository</c> regexp).</summary>
    public static bool IsRepository(string s)
    {
        var slash = s.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && slash < s.Length - 1 && IsRepoPart(s.AsSpan(0, slash)) && IsRepoPart(s.AsSpan(slash + 1));
    }

    /// <summary>A positive decimal without sign or leading zero (Go <c>^[1-9][0-9]*$</c>).</summary>
    public static bool IsPositiveDecimal(string s) =>
        s.Length > 0 && s[0] is >= '1' and <= '9' && s.All(static c => c is >= '0' and <= '9');

    /// <summary>A shard id <c>s&lt;n&gt;</c> (Go <c>^s[1-9][0-9]*$</c>).</summary>
    public static bool IsShardId(string s) => s.Length > 1 && s[0] == 's' && IsPositiveDecimal(s[1..]);

    /// <summary>Whether <paramref name="p"/> is a safe slash-separated relative evidence path (Go <c>validPath</c>).</summary>
    public static bool ValidPath(string p)
    {
        if (p.Length == 0 || p == "." || p.StartsWith('/') || p.IndexOfAny(['\\', ':', '\0', '<', '>', '"', '|', '?', '*']) >= 0)
        {
            return false;
        }

        foreach (var c in p)
        {
            if (c < 32)
            {
                return false;
            }
        }

        // path.Clean(p) == p for a rooted-free path means: no empty, "." or ".." element.
        foreach (var part in p.Split('/'))
        {
            if (part.Length == 0 || part == "." || part == ".." || part.EndsWith('.') || part.EndsWith(' '))
            {
                return false;
            }

            var dot = part.IndexOf('.', StringComparison.Ordinal);
            var stem = (dot < 0 ? part : part[..dot]).ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL"
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>SHA-256 of <paramref name="data"/> as lower-case hex.</summary>
    public static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>SHA-256 of a file, streamed.</summary>
    public static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Throws when <paramref name="path"/> or any existing ancestor is a symbolic link, junction or other reparse point.</summary>
    public static void RejectLinkedPath(string path, string what)
    {
        for (var p = path; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        {
            if (IsReparsePoint(p))
            {
                throw new RemoteAcceptException($"{what}: {p}");
            }
        }
    }

    /// <summary>Whether <paramref name="path"/> exists and is a reparse point (symlink, junction, mount point).</summary>
    public static bool IsReparsePoint(string path)
    {
        var attributes = new FileInfo(path).Attributes;
        return attributes != (FileAttributes)(-1) && (attributes & FileAttributes.ReparsePoint) != 0;
    }

    /// <summary>A UTF-8 text of <paramref name="data"/>.</summary>
    public static string Text(ReadOnlySpan<byte> data) => Encoding.UTF8.GetString(data);

    private static bool IsRepoPart(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty)
        {
            return false;
        }

        foreach (var c in s)
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLowerHex(string s, int length)
    {
        if (s.Length != length)
        {
            return false;
        }

        foreach (var c in s)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// An opened evidence root (Go <c>tree</c>): every entry is a regular, unlinked, safely named, case-unique file or
/// directory, and every read is digest-pinned.
/// </summary>
internal sealed class EvidenceTree
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _used = new(StringComparer.Ordinal);

    private EvidenceTree(string root)
    {
        Root = root;
    }

    /// <summary>The absolute root.</summary>
    public string Root { get; }

    /// <summary>The paths read so far through <see cref="Read{T}"/> / <see cref="ReadBytes"/>, sorted.</summary>
    public IReadOnlyCollection<string> Used => _used;

    /// <summary>Opens and walks <paramref name="root"/>; links, redirected parents, unsafe names, case collisions and non-regular files throw.</summary>
    public static EvidenceTree Open(string root)
    {
        var abs = Path.GetFullPath(root);
        if (!Directory.Exists(abs))
        {
            throw new RemoteAcceptException($"evidence root not found: {abs}");
        }

        EvidenceRules.RejectLinkedPath(abs, "redirected evidence");
        var tree = new EvidenceTree(abs);
        tree.Walk(abs, "");
        return tree;
    }

    /// <summary>Whether <paramref name="p"/> names an entry of the tree with exactly this spelling (Go <c>t.files[lower(p)] == p</c>).</summary>
    public bool Has(string p) => _files.TryGetValue(p.ToLowerInvariant(), out var spelled) && spelled == p;

    /// <summary>Reads the file <paramref name="reference"/> names after the path, case and digest checks, and marks it used.</summary>
    public byte[] ReadBytes(Ref reference)
    {
        if (!EvidenceRules.ValidPath(reference.Path) || !EvidenceRules.IsDigest(reference.Sha256))
        {
            throw new RemoteAcceptException($"invalid reference {{{reference.Path} {reference.Sha256}}}");
        }

        if (!Has(reference.Path))
        {
            throw new RemoteAcceptException($"missing or case-mismatched evidence {reference.Path}");
        }

        var data = File.ReadAllBytes(Path.Combine(Root, reference.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (EvidenceRules.Hash(data) != reference.Sha256)
        {
            throw new RemoteAcceptException($"digest mismatch: {reference.Path}");
        }

        _used.Add(reference.Path);
        return data;
    }

    /// <summary>Reads and strictly parses the referenced file with <paramref name="parse"/>.</summary>
    public T Read<T>(Ref reference, Func<JsonNode?, T> parse)
    {
        var data = ReadBytes(reference);
        try
        {
            return parse(StrictJson.Parse(data));
        }
        catch (RemoteAcceptException e)
        {
            throw new RemoteAcceptException($"{reference.Path}: {e.Message}", e);
        }
    }

    /// <summary>Reads the referenced file as one strict JSON object.</summary>
    public JsonObject ReadObject(Ref reference) => Read(reference, n => n as JsonObject ?? throw new RemoteAcceptException("expected a JSON object"));

    private void Walk(string dir, string relDir)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        var names = Directory.GetFileSystemEntries(dir, "*", options).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList();
        foreach (var name in names)
        {
            var full = Path.Combine(dir, name);
            var rel = relDir.Length == 0 ? name : relDir + "/" + name;
            var info = new FileInfo(full);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new RemoteAcceptException($"linked evidence: {full}");
            }

            if (!EvidenceRules.ValidPath(rel))
            {
                throw new RemoteAcceptException($"unsafe path \"{rel}\"");
            }

            var key = rel.ToLowerInvariant();
            if (_files.ContainsKey(key))
            {
                throw new RemoteAcceptException($"case-colliding path {rel}");
            }

            _files[key] = rel;
            if ((info.Attributes & FileAttributes.Directory) != 0)
            {
                Walk(full, rel);
            }
            else if ((info.Attributes & FileAttributes.Device) != 0)
            {
                throw new RemoteAcceptException($"nonregular evidence {rel}");
            }
        }
    }
}
