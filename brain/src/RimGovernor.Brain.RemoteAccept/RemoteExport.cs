using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RimGovernor.Brain.RemoteAccept;

/// <summary>One role's suite output and bootstrap file for <see cref="RemoteExport.ExportShard"/> (the <c>-jobs</c> file rows).</summary>
public sealed record ExportJob(string Role, string Output, string Bootstrap)
{
    /// <summary>Parses the strict <c>-jobs</c> JSON array of <c>{role, output, bootstrap}</c>.</summary>
    public static IReadOnlyList<ExportJob> ParseJobs(ReadOnlySpan<byte> json) =>
        StrictJson.Elements(StrictJson.ParseArray(json, "jobs"), n =>
        {
            var o = StrictJson.ElementObj(n);
            return new ExportJob(StrictJson.Str(o, "role"), StrictJson.Str(o, "output"), StrictJson.Str(o, "bootstrap"));
        });
}

/// <summary>The raw-byte budget shared by every file one shard export writes.</summary>
internal sealed class Allowance(long remaining)
{
    public long Remaining { get; set; } = remaining;
}

/// <summary>
/// The public-artifact export boundary (Go export.go). Only suite diagnostics are copied; worker layouts, profiles, saves
/// and bootstrap inputs never enter the tree. Text is scanned for restricted markers and secrets, rewritten and hashed
/// without being held in memory; PNG frames are validated and re-emitted clean.
/// </summary>
public static partial class RemoteExport
{
    /// <summary>The job-output subdirectory a shard records its colony snapshot streams into.</summary>
    public const string SnapshotDir = "snapshots";

    private const string AllowanceExhausted = "artifact allowance exhausted; required evidence is incomplete";
    private const int ScanOverlap = 32;
    private const int BufferSize = 1 << 20;

    private static readonly string[] RestrictedMarkers = ["AGE-SECRET-KEY-", "-----BEGIN PRIVATE KEY", "-----BEGIN OPENSSH PRIVATE KEY", "<savegame", "<savedgame", "UnityFS"];

    private static readonly string[] LoweredMarkers = [.. RestrictedMarkers.Select(static m => m.ToLowerInvariant())];

    [GeneratedRegex(@"[A-Za-z]:[\\/][^ \t\n\f\r""<>]*", RegexOptions.CultureInvariant)]
    private static partial Regex RunnerPath();

    /// <summary>
    /// Writes a portable attempts manifest after all role suites finish. Missing attempts or diagnostics are errors, never
    /// synthesized passes. The partial tree remains useful to the always-run upload and incomplete verdict.
    /// </summary>
    public static void ExportShard(string root, string shard, IReadOnlyList<ExportJob> jobs, IReadOnlyList<string> secrets)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(shard);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(secrets);
        EvidenceTree.Open(root);
        var run = Run.Parse(StrictJson.Parse(File.ReadAllBytes(Path.Combine(root, "run.json"))));
        var selection = Selection.Parse(StrictJson.Parse(File.ReadAllBytes(Path.Combine(root, "selection.json"))));
        var want = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in selection.Shards)
        {
            if (s.Id == shard)
            {
                want.UnionWith(s.Cases);
            }
        }

        if (want.Count == 0 || !EvidenceRules.ValidPath(shard) || shard.Contains('/', StringComparison.Ordinal))
        {
            throw new RemoteAcceptException("unknown shard");
        }

        if (run.Limits.Bytes < 0 || (run.Limits.Bytes > 0 && run.Limits.Bytes <= 64L << 20))
        {
            throw new RemoteAcceptException("artifact allowance cannot accommodate plan and verdict");
        }

        // Zero disables the operator's raw-byte cap. It is not a GitHub quota: GitHub stores compressed uploads, and
        // public-repository usage differs from the included private-repository storage allowance.
        var remaining = new Allowance(long.MaxValue);
        if (run.Limits.Bytes > 0)
        {
            remaining.Remaining = (run.Limits.Bytes - (64L << 20)) / (2 * (long)selection.Shards.Count); // shard artifacts + final aggregate, with plan/manifest reserve
        }

        var runRef = RemoteEvidence.FileRef(root, "run.json");
        var selectionRef = RemoteEvidence.FileRef(root, "selection.json");
        var items = new List<Attempt>();
        AttemptsRunner? runner = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            if (job.Role != "fixture" || !roles.Add(job.Role))
            {
                throw new RemoteAcceptException("invalid/duplicate role");
            }

            var source = Path.GetFullPath(job.Output);
            var e = new Exporter(root, shard + "/" + job.Role, source, remaining, secrets);
            e.Collect();

            // Copy the verdict and its reference closure first, then optional diagnostics.
            e.Copy("result.json");
            foreach (var p in e.Paths())
            {
                // Colony snapshot streams (RIMGOVERNOR_SNAPSHOT_DIR=<output>/snapshots) are optional recordings: under a
                // raw-byte cap they yield to the verdict's evidence rather than fail the shard.
                if (p.StartsWith(SnapshotDir + "/", StringComparison.Ordinal)
                    && new FileInfo(Path.Combine(source, p.Replace('/', Path.DirectorySeparatorChar))) is { Exists: true } info && info.Length > remaining.Remaining)
                {
                    continue;
                }

                e.Copy(p);
            }

            var suiteCases = StrictJson.List(
                StrictJson.ParseObject(File.ReadAllBytes(Path.Combine(root, e.Prefix.Replace('/', Path.DirectorySeparatorChar), "result.json")), "result.json"),
                "cases",
                n =>
                {
                    var o = StrictJson.ElementObj(n);
                    return (Name: StrictJson.Str(o, "name"), Attempts: StrictJson.List(o, "attempts", Attempt.Parse));
                });
            foreach (var row in suiteCases)
            {
                if (!want.Contains(row.Name) || !seen.Add(row.Name) || row.Attempts.Count == 0)
                {
                    throw new RemoteAcceptException($"missing/duplicate/unplanned attempts for {row.Name}");
                }

                items.AddRange(row.Attempts);
            }

            var boot = StrictJson.ParseObject(File.ReadAllBytes(job.Bootstrap), "bootstrap");
            var passed = StrictJson.Bool(boot, "passed");
            var os = StrictJson.Str(boot, "os");
            var image = StrictJson.Str(boot, "image_version");
            var cpus = StrictJson.Int(boot, "cpu_count");
            var memory = StrictJson.Int64(boot, "memory_bytes");
            var disk = StrictJson.Int64(boot, "free_disk_bytes");
            var cacheHit = StrictJson.OptNullableBool(boot, "cache_hit");
            var restore = StrictJson.OptInt64(boot, "restore_ms");
            var extract = StrictJson.OptInt64(boot, "extract_ms");
            var build = StrictJson.OptInt64(boot, "build_ms");
            var total = StrictJson.OptInt64(boot, "total_ms");
            var provision = StrictJson.OptInt64(boot, "provision_ms");
            if (!passed)
            {
                throw new RemoteAcceptException("bootstrap failed");
            }

            // A projection intentionally excludes absolute paths and dependency metadata.
            var projection = StrictJson.Compact(w =>
            {
                w.WriteStartObject();
                w.WriteBoolean("passed", passed);
                w.WriteString("os", os);
                w.WriteString("image_version", image);
                w.WriteNumber("cpu_count", cpus);
                w.WriteNumber("memory_bytes", memory);
                w.WriteNumber("free_disk_bytes", disk);
                if (cacheHit is { } hit)
                {
                    w.WriteBoolean("cache_hit", hit);
                }

                WriteIfNonZero(w, "restore_ms", restore);
                WriteIfNonZero(w, "extract_ms", extract);
                WriteIfNonZero(w, "build_ms", build);
                WriteIfNonZero(w, "total_ms", total);
                WriteIfNonZero(w, "provision_ms", provision);
                w.WriteEndObject();
            });
            var bootstrapPath = e.Prefix + "/bootstrap.json";
            e.Write(bootstrapPath, projection);
            runner ??= new AttemptsRunner(os, image, "x64", cpus, memory, disk, RemoteEvidence.FileRef(root, bootstrapPath));
        }

        if (seen.Count != want.Count || runner is null)
        {
            throw new RemoteAcceptException("shard coverage incomplete");
        }

        var attempts = new Attempts(1, runRef, selectionRef, shard, runner, items);
        RemoteEvidence.WriteJson(root, shard + "/attempts.json", attempts.Write);
    }

    /// <summary>The diagnostic extension of <paramref name="p"/>, lower-cased; a rotated OTLP ring segment (<c>logs.jsonl.2</c>) is a <c>.jsonl</c> file.</summary>
    internal static string DiagExt(string p)
    {
        var ext = Path.GetExtension(p).ToLowerInvariant();
        var n = ext.TrimStart('.');
        if (n.Length > 0 && n.All(static c => c is >= '0' and <= '9') && p[..^ext.Length].EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return ".jsonl";
        }

        return ext;
    }

    internal static bool IsTextExt(string ext) => ext is ".json" or ".jsonl" or ".log" or ".txt" or ".md";

    /// <summary>Throws unless <paramref name="p"/> carries a text diagnostic extension.</summary>
    internal static void PublicExt(string p)
    {
        if (!IsTextExt(DiagExt(p)))
        {
            throw new RemoteAcceptException($"non-diagnostic file {p}");
        }
    }

    /// <summary>
    /// Scans a text diagnostic in fixed chunks, so a multi-GB stream is checked without being held in memory. Markers
    /// spanning chunks are caught by carrying over the longest marker's tail. NUL bytes and restricted markers throw.
    /// </summary>
    internal static void PublicText(string p, Stream r)
    {
        var buf = new byte[BufferSize];
        var carry = 0;
        while (true)
        {
            var n = r.Read(buf, carry, buf.Length - carry);
            if (n == 0)
            {
                return;
            }

            var len = carry + n;
            if (buf.AsSpan(carry, n).Contains((byte)0))
            {
                throw new RemoteAcceptException($"binary diagnostic {p}");
            }

            var lower = Encoding.UTF8.GetString(buf, 0, len).ToLowerInvariant();
            foreach (var marker in LoweredMarkers)
            {
                if (lower.Contains(marker, StringComparison.Ordinal))
                {
                    throw new RemoteAcceptException($"restricted content in {p}");
                }
            }

            carry = Math.Min(ScanOverlap, len);
            Buffer.BlockCopy(buf, len - carry, buf, 0, carry);
        }
    }

    /// <summary>Validates one diagnostic as public content: a bounded valid PNG, or a text file with an allowed extension and no restricted content.</summary>
    internal static void PublicDiagnostic(string p, byte[] data)
    {
        if (DiagExt(p) == ".png")
        {
            PngSanitizer.Sanitize(data, p);
            return;
        }

        PublicExt(p);
        PublicText(p, new MemoryStream(data));
    }

    private static void WriteIfNonZero(Utf8JsonWriter w, string name, long value)
    {
        if (value != 0)
        {
            w.WriteNumber(name, value);
        }
    }

    /// <summary>Fails a copy that would exceed the remaining allowance instead of truncating it.</summary>
    private sealed class CapStream(Stream inner, Allowance remaining) : Stream
    {
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

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count > remaining.Remaining)
            {
                throw new RemoteAcceptException(AllowanceExhausted);
            }

            remaining.Remaining -= count;
            inner.Write(buffer, offset, count);
        }
    }

    private sealed class Exporter(string dest, string prefix, string source, Allowance remaining, IReadOnlyList<string> secrets)
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
        private readonly HashSet<string> _active = new(StringComparer.Ordinal);

        public string Prefix { get; } = prefix;

        /// <summary>Sorted ordinal list of the diagnostic files found by <see cref="Collect"/>.</summary>
        public List<string> Paths()
        {
            var paths = new List<string>(_files.Values);
            paths.Sort(StringComparer.Ordinal);
            return paths;
        }

        /// <summary>Walks the source tree, refusing links and unsafe names, and records the diagnostic files.</summary>
        public void Collect()
        {
            if (!Directory.Exists(source))
            {
                throw new RemoteAcceptException($"diagnostic output not found: {source}");
            }

            EvidenceRules.RejectLinkedPath(source, "linked diagnostic");
            Walk(source, "");
        }

        public Ref Copy(string p)
        {
            if (!EvidenceRules.ValidPath(p) || !_files.TryGetValue(p.ToLowerInvariant(), out var spelled) || spelled != p)
            {
                throw new RemoteAcceptException($"missing/unsafe diagnostic {p}");
            }

            var target = Prefix + "/" + p;
            if (_active.Contains(p))
            {
                throw new RemoteAcceptException($"cyclic diagnostic reference {p}");
            }

            var dst = Path.Combine(dest, target.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(dst) || Directory.Exists(dst))
            {
                return RemoteEvidence.FileRef(dest, target);
            }

            _active.Add(p);
            try
            {
                var src = Path.Combine(source, p.Replace('/', Path.DirectorySeparatorChar));
                var size = new FileInfo(src).Length;
                if (size > remaining.Remaining)
                {
                    throw new RemoteAcceptException($"artifact allowance exhausted at {p}");
                }

                var ext = DiagExt(p);
                if (ext == ".png")
                {
                    var bytes = File.ReadAllBytes(src);
                    // Re-emit generated frames clean: critical chunks only, no metadata, no trailing payload.
                    Write(target, PngSanitizer.Sanitize(bytes, p));
                    return RemoteEvidence.FileRef(dest, target);
                }

                // Text diagnostics stream: a multi-GB recording is scanned, rewritten and hashed without being held in
                // memory, and exhausting the allowance fails the copy instead of dropping or truncating the file.
                PublicExt(p);
                CopyText(p, ext, src, dst);
                return RemoteEvidence.FileRef(dest, target);
            }
            finally
            {
                _active.Remove(p);
            }
        }

        /// <summary>Writes already-cleaned <paramref name="data"/> to <paramref name="p"/> under the destination, charging the allowance after the public checks.</summary>
        public void Write(string p, byte[] data)
        {
            if (data.Length > remaining.Remaining)
            {
                throw new RemoteAcceptException(AllowanceExhausted);
            }

            PublicDiagnostic(p, data);
            var path = Path.Combine(dest, p.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
            remaining.Remaining -= data.Length;
        }

        private void Walk(string dir, string relDir)
        {
            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
            foreach (var name in Directory.GetFileSystemEntries(dir, "*", options).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal))
            {
                var full = Path.Combine(dir, name);
                var rel = relDir.Length == 0 ? name : relDir + "/" + name;
                var info = new FileInfo(full);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new RemoteAcceptException("linked diagnostic");
                }

                if ((info.Attributes & FileAttributes.Directory) != 0)
                {
                    // Numeric suite roots share the workers prefix with case diagnostics.
                    if (IsWorkerRoot(rel) || rel == "checkpoints" || rel == "profile")
                    {
                        continue;
                    }

                    Walk(full, rel);
                    continue;
                }

                if (!EvidenceRules.ValidPath(rel))
                {
                    throw new RemoteAcceptException("unsafe diagnostic path");
                }

                if ((info.Attributes & FileAttributes.Device) != 0)
                {
                    throw new RemoteAcceptException("nonregular diagnostic");
                }

                var ext = DiagExt(rel);
                if (IsTextExt(ext) || ext == ".png")
                {
                    if (!_files.TryAdd(rel.ToLowerInvariant(), rel))
                    {
                        throw new RemoteAcceptException("case-colliding diagnostic");
                    }
                }
            }
        }

        private static bool IsWorkerRoot(string rel) =>
            rel.StartsWith("workers/", StringComparison.Ordinal) && rel.Length > 8 && rel[8..].All(static c => c is >= '0' and <= '9');

        private void CopyText(string p, string ext, string src, string dst)
        {
            using (var scan = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                PublicText(p, scan);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            var tmp = Path.Combine(Path.GetDirectoryName(dst)!, ".export-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var capped = new CapStream(file, remaining))
                using (var output = new BufferedStream(capped, BufferSize))
                {
                    if (ext is ".json" or ".jsonl")
                    {
                        CopyJson(src, output);
                    }
                    else
                    {
                        CopyLines(src, output);
                    }

                    output.Flush();
                }

                // The scan of the source cannot see text a JSON escape spells out (AGE-SECRET-KEY-) that decoding
                // reveals; scan what is about to be published as well.
                using (var check = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    PublicText(p, check);
                }

                File.Move(tmp, dst, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
        }

        private void CopyLines(string src, Stream output)
        {
            using var reader = new StreamReader(src, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, BufferSize);
            var chunk = new char[1 << 16];
            var pending = new StringBuilder();
            int n;
            while ((n = reader.Read(chunk, 0, chunk.Length)) > 0)
            {
                var start = 0;
                for (var i = 0; i < n; i++)
                {
                    if (chunk[i] == '\n')
                    {
                        pending.Append(chunk, start, i - start + 1);
                        EmitText(output, pending.ToString());
                        pending.Clear();
                        start = i + 1;
                    }
                }

                pending.Append(chunk, start, n - start);
            }

            if (pending.Length > 0)
            {
                EmitText(output, pending.ToString());
            }
        }

        private void EmitText(Stream output, string line) => output.Write(Encoding.UTF8.GetBytes(Clean(line)));

        private void CopyJson(string src, Stream output)
        {
            // First pass: duplicate keys (also case aliases) and malformed JSON anywhere in the stream fail before any output.
            foreach (var unused in JsonValues.Enumerate(src, parse: false))
            {
                _ = unused;
            }

            var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            foreach (var node in JsonValues.Enumerate(src, parse: true))
            {
                writer.Reset(output);
                StrictJson.WriteSorted(writer, Value(node));
                writer.Flush();
                output.WriteByte((byte)'\n');
            }
        }

        private JsonNode? Value(JsonNode? v)
        {
            switch (v)
            {
                case JsonValue s when s.GetValueKind() == JsonValueKind.String:
                    return JsonValue.Create(Clean(s.GetValue<string>()));
                case JsonArray a:
                    var array = new JsonArray();
                    foreach (var item in a)
                    {
                        array.Add(Value(item));
                    }

                    return array;
                case JsonObject o:
                    if (o.Count == 2 && o["path"] is { } path && o["sha256"] is { } digest)
                    {
                        if (path is not JsonValue pv || pv.GetValueKind() != JsonValueKind.String)
                        {
                            throw new RemoteAcceptException("unsafe diagnostic reference \"\"");
                        }

                        var p = pv.GetValue<string>();
                        if (!EvidenceRules.ValidPath(p) || !_files.TryGetValue(p.ToLowerInvariant(), out var spelled) || spelled != p)
                        {
                            throw new RemoteAcceptException($"unsafe diagnostic reference \"{p}\"");
                        }

                        if (digest is not JsonValue dv || dv.GetValueKind() != JsonValueKind.String)
                        {
                            throw new RemoteAcceptException("invalid diagnostic digest");
                        }

                        var got = EvidenceRules.HashFile(Path.Combine(source, p.Replace('/', Path.DirectorySeparatorChar)));
                        if (got != dv.GetValue<string>())
                        {
                            throw new RemoteAcceptException($"source diagnostic digest mismatch: {p}");
                        }

                        var copied = Copy(p);
                        return new JsonObject { ["path"] = copied.Path, ["sha256"] = copied.Sha256 };
                    }

                    var obj = new JsonObject();
                    foreach (var (key, child) in o)
                    {
                        obj[key] = Value(child);
                    }

                    return obj;
                default:
                    return v?.DeepClone();
            }
        }

        /// <summary>Redacts secrets and scrubs runner paths; paths within the diagnostic tree stay navigable, other runner paths disclose no useful portable identity.</summary>
        private string Clean(string s)
        {
            foreach (var secret in secrets)
            {
                if (secret.Length > 0)
                {
                    s = s.Replace(secret, "[redacted]", StringComparison.Ordinal);
                }
            }

            s = s.Replace(source + Path.DirectorySeparatorChar, Prefix + "/", StringComparison.Ordinal);
            s = s.Replace(source.Replace('\\', '/') + "/", Prefix + "/", StringComparison.Ordinal);
            if (s == source)
            {
                return Prefix;
            }

            s = RunnerPath().Replace(s, "[runner-path]");
            return s.Replace('\\', '/');
        }
    }
}
