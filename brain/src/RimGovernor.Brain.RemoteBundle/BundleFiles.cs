using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace RimGovernor.Brain.RemoteBundle;

/// <summary>Paths, digests and tree checks shared by every bundle operation (Go <c>manifest.go</c>, <c>origin.go</c> helpers and the Windows source files).</summary>
public static partial class BundleFiles
{
    private static readonly System.Buffers.SearchValues<char> UnsafeCharacters = System.Buffers.SearchValues.Create("\\:\0\r\n\t*?\"<>|");

    private const uint FileReadAttributes = 0x80;
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;

    /// <summary>The 64-hex-digit SHA-256 form.</summary>
    public static bool IsDigest(string text) => DigestPattern().IsMatch(text);

    /// <summary>The <c>owner/repository</c> form.</summary>
    public static bool IsRepository(string text) => RepositoryPattern().IsMatch(text);

    /// <summary>The lowercase hex SHA-256 of <paramref name="bytes"/> (Go <c>Digest</c>).</summary>
    public static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The lowercase hex SHA-256 of the UTF-8 text.</summary>
    public static string Digest(string text) => Digest(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Rejects Windows aliases as well as traversal, even on non-Windows hosts (Go <c>SafePath</c>). The path is a
    /// forward-slash relative path.
    /// </summary>
    public static void SafePath(string path)
    {
        if (path.Length == 0 || CleanSlashPath(path) != path || path.StartsWith('/') || path.AsSpan().IndexOfAny(UnsafeCharacters) >= 0)
        {
            throw new BundleException($"unsafe bundle path \"{path}\"");
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment is "." or ".." || segment.TrimEnd(' ', '.') != segment)
            {
                throw new BundleException($"unsafe bundle path \"{path}\"");
            }

            var dot = segment.IndexOf('.', StringComparison.Ordinal);
            var stem = (dot < 0 ? segment : segment[..dot]).ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || ReservedDevice().IsMatch(stem))
            {
                throw new BundleException($"reserved Windows path \"{path}\"");
            }
        }
    }

    /// <summary>Whether <see cref="SafePath"/> accepts the path.</summary>
    public static bool IsSafePath(string path)
    {
        try
        {
            SafePath(path);
            return true;
        }
        catch (BundleException)
        {
            return false;
        }
    }

    /// <summary>Whether <paramref name="child"/> is <paramref name="parent"/> or below it, compared without case (Go <c>within</c>).</summary>
    public static bool Within(string parent, string child)
    {
        var relative = Path.GetRelativePath(parent.ToLowerInvariant(), child.ToLowerInvariant());
        if (Path.IsPathRooted(relative))
        {
            return false;
        }

        return relative == "." || (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    /// <summary>
    /// Resolves by the opened handle: the final path follows NTFS junction chains and exposes cycles reached through
    /// different junction names (Go <c>resolveSource</c>).
    /// </summary>
    public static string ResolveSource(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            var full = Path.GetFullPath(name);
            FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
            return info.ResolveLinkTarget(true)?.FullName ?? full;
        }

        using var handle = CreateFile(name, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new BundleException($"open {name}: {new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }

        var buffer = new char[32768];
        uint length;
        unsafe
        {
            fixed (char* p = buffer)
            {
                length = GetFinalPathNameByHandle(handle, p, (uint)buffer.Length, 0);
            }
        }

        if (length == 0)
        {
            throw new BundleException($"resolve {name}: {new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }

        if (length >= buffer.Length)
        {
            throw new BundleException("resolved source path exceeds Windows path buffer");
        }

        var resolved = new string(buffer, 0, (int)length);
        return resolved.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)
            ? @"\\" + resolved[@"\\?\UNC\".Length..]
            : resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ? resolved[@"\\?\".Length..] : resolved;
    }

    /// <summary>
    /// Resolves a destination that may not exist yet: the deepest existing ancestor is resolved and the missing tail is
    /// appended (Go <c>resolveDestination</c>).
    /// </summary>
    public static string ResolveDestination(string dest)
    {
        var path = Path.GetFullPath(dest);
        var tail = new List<string>();
        while (true)
        {
            if (Exists(path))
            {
                var real = ResolveSource(path);
                for (var i = tail.Count - 1; i >= 0; i--)
                {
                    real = Path.Combine(real, tail[i]);
                }

                return real;
            }

            var parent = Path.GetDirectoryName(path);
            if (parent is null || parent == path)
            {
                throw new BundleException("no existing destination ancestor");
            }

            tail.Add(Path.GetFileName(path));
            path = parent;
        }
    }

    /// <summary>The SHA-256 and length of a file (Go <c>HashFile</c>).</summary>
    public static (string Sha256, long Bytes) HashFile(string fileName)
    {
        using var stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return (Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length);
    }

    /// <summary>Refuses anything but a regular file of the expected size and digest (Go <c>VerifyFile</c>).</summary>
    public static void VerifyFile(string fileName, long size, string digest)
    {
        var info = new FileInfo(fileName);
        if (!info.Exists || !IsRegular(info) || info.Length != size)
        {
            throw new BundleException($"{fileName}: not a regular file of expected size {size}");
        }

        if (HashFile(fileName).Sha256 != digest)
        {
            throw new BundleException($"{fileName}: SHA-256 mismatch");
        }
    }

    /// <summary>Refuses links and extra files, including a profile accidentally archived (Go <c>VerifyTree</c>).</summary>
    public static void VerifyTree(string root, Inventory inventory)
    {
        var want = inventory.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        foreach (var (path, relative, isDirectory) in Walk(root, "archive link/reparse point"))
        {
            if (isDirectory)
            {
                continue;
            }

            if (!want.TryGetValue(relative, out var file))
            {
                throw new BundleException($"unlisted archive file {relative}");
            }

            VerifyFile(path, file.Bytes, file.Sha256);
            want.Remove(relative);
        }

        BundleException.Require(want.Count == 0, $"archive missing {want.Count} inventory files");
    }

    /// <summary>Lists the regular files under <paramref name="root"/> with their hashes, sorted by path; links fail (Go <c>InventoryTree</c>).</summary>
    public static Inventory InventoryTree(string root)
    {
        var files = new List<BundleFile>();
        foreach (var (path, relative, isDirectory) in Walk(root, "staging contains link"))
        {
            if (isDirectory)
            {
                continue;
            }

            SafePath(relative);
            var (digest, bytes) = HashFile(path);
            files.Add(new BundleFile(relative, bytes, digest));
        }

        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new Inventory(1, files);
    }

    /// <summary>Whether the entry is a regular file: not a directory, link or reparse point.</summary>
    internal static bool IsRegular(FileSystemInfo info) =>
        (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;

    /// <summary>
    /// Every entry below <paramref name="root"/>, depth first, with its forward-slash relative path. A link or reparse
    /// point, directory or file, fails with <paramref name="linkMessage"/>.
    /// </summary>
    internal static IEnumerable<(string Path, string Relative, bool IsDirectory)> Walk(string root, string linkMessage)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new BundleException($"{linkMessage} {entry.FullName}");
                }

                var relative = Path.GetRelativePath(root, entry.FullName).Replace(Path.DirectorySeparatorChar, '/');
                var isDirectory = entry is DirectoryInfo;
                yield return (entry.FullName, relative, isDirectory);
                if (entry is DirectoryInfo sub)
                {
                    pending.Push(sub);
                }
            }
        }
    }

    /// <summary>The environment a build, compression or decryption child gets: no token, secret, password or private key.</summary>
    public static void ScrubEnvironment(System.Diagnostics.ProcessStartInfo start)
    {
        ArgumentNullException.ThrowIfNull(start);
        foreach (var key in start.Environment.Keys.ToList())
        {
            var upper = key.ToUpperInvariant();
            if (upper.Contains("TOKEN", StringComparison.Ordinal) || upper.Contains("SECRET", StringComparison.Ordinal)
                || upper.Contains("PASSWORD", StringComparison.Ordinal) || upper.Contains("PRIVATE_KEY", StringComparison.Ordinal))
            {
                start.Environment.Remove(key);
            }
        }
    }

    /// <summary>The bytes free to the caller on the volume holding <paramref name="directory"/> (Go <c>freeDisk</c>).</summary>
    internal static ulong FreeDisk(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (ulong)new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory)) ?? "/").AvailableFreeSpace;
        }

        if (GetDiskFreeSpaceEx(directory, out var available, out _, out _) == 0)
        {
            throw new BundleException($"free disk of {directory}: {new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }

        return available;
    }

    /// <summary>Lstat: true for a link itself, even a dangling one.</summary>
    internal static bool Exists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Go <c>path.Clean</c> for a slash path.</summary>
    private static string CleanSlashPath(string path)
    {
        var rooted = path.StartsWith('/');
        var parts = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (parts.Count > 0 && parts[^1] != "..")
                {
                    parts.RemoveAt(parts.Count - 1);
                }
                else if (!rooted)
                {
                    parts.Add("..");
                }

                continue;
            }

            parts.Add(segment);
        }

        var joined = string.Join('/', parts);
        return rooted ? "/" + joined : joined.Length == 0 ? "." : joined;
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DigestPattern();

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex("^(COM|LPT)[0-9]$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ReservedDevice();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint length, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetDiskFreeSpaceEx(string directory, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);
}
