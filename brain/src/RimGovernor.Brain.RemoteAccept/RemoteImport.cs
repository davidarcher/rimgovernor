using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.RemoteAccept;

/// <summary>Reads authenticated GitHub endpoints (Go <c>API</c>) through the <c>gh api</c> CLI (Go <c>GitHub</c>): the operator's own authenticated session.</summary>
public static class GhCliApi
{
    /// <summary>GETs <paramref name="endpoint"/> (a path such as <c>repos/o/r/actions/runs/1</c>) and streams the body to <paramref name="destination"/>.</summary>
    public static async Task GetAsync(string endpoint, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(destination);
        var start = new ProcessStartInfo("gh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("api");
        start.ArgumentList.Add(endpoint);
        using var process = Process.Start(start) ?? throw new RemoteAcceptException("gh api: could not start gh");
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardOutput.BaseStream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new RemoteAcceptException($"gh api: exit status {process.ExitCode}: {(await stderr.ConfigureAwait(false)).Trim()}");
        }
    }
}

/// <summary>Importing a GitHub Actions evidence artifact into a new local directory (Go import.go and source.go).</summary>
public static class RemoteImport
{
    /// <summary>Reads the operator's trust pins from the repository's local git config (<c>rimgovernor.acceptance*</c>), never from artifact contents.</summary>
    public static async Task<Trust> LocalTrustAsync(string repo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in new[] { "repository", "workflow", "workflowCommit", "bundleSHA256" })
        {
            try
            {
                values[key] = await GitAsync(repo, ["config", "--local", "--get", "rimgovernor.acceptance" + key], cancellationToken).ConfigureAwait(false);
            }
            catch (RemoteAcceptException e)
            {
                throw new RemoteAcceptException($"configure local rimgovernor.acceptance{key} trust pin: {e.Message}", e);
            }
        }

        return new Trust(values["repository"], values["workflow"], values["workflowCommit"], values["bundleSHA256"]);
    }

    /// <summary>Authenticates the artifact against the pinned completed workflow run and returns its archive digest (Go <c>Authenticate</c>).</summary>
    public static async Task<string> AuthenticateAsync(Provenance provenance, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        return (await AuthenticateArtifactAsync(provenance, cancellationToken).ConfigureAwait(false)).Digest;
    }

    /// <summary>
    /// Authenticates, downloads into the new directory <paramref name="output"/> (preserving the archive and extracting only
    /// regular diagnostic files), verifies the whole graph and writes <c>evidence/result.json</c> with the import
    /// provenance. A failing verdict throws after the result is written.
    /// </summary>
    public static async Task DownloadAsync(Provenance provenance, string output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(output);
        var identity = await AuthenticateArtifactAsync(provenance, cancellationToken).ConfigureAwait(false);
        var p = provenance with { ArchiveSha256 = identity.Digest };
        if (Directory.Exists(output) || File.Exists(output))
        {
            throw new RemoteAcceptException($"new import directory: {output} already exists");
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(output));
        if (parent is null || !Directory.Exists(parent))
        {
            throw new RemoteAcceptException($"new import directory: parent of {output} does not exist");
        }

        Directory.CreateDirectory(output);
        var archive = Path.Combine(output, "actions-artifact.zip");
        await using (var file = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await using var limited = new BoundedStream(file, identity.Size);
            await GhCliApi.GetAsync($"repos/{p.Trust.Repository}/actions/artifacts/{p.ArtifactId.ToString(CultureInfo.InvariantCulture)}/zip", limited, cancellationToken).ConfigureAwait(false);
        }

        if (EvidenceRules.HashFile(archive) != identity.Digest)
        {
            throw new RemoteAcceptException("archive digest mismatch");
        }

        var root = Path.Combine(output, "evidence");
        Directory.CreateDirectory(root);
        Extract(archive, root);
        var aggregateRef = RemoteEvidence.FileRef(root, "aggregate.json");
        var e = RemoteEvidence.Verify(root, aggregateRef);
        MatchProvenance(e.Run!, p);
        RejectSynthetic(e);
        e.Report = e.Report with { Remote = e.Report.Remote with { Import = p } };

        // The result stays beside untouched manifests, preserving every relative link.
        RemoteEvidence.WriteReport(root, "result.json", e.Report);
        if (!e.Report.Passed)
        {
            throw new RemoteAcceptException($"remote acceptance {e.Aggregate.Status}: {e.Report.Error}");
        }
    }

    private static async Task<ArtifactIdentity> AuthenticateArtifactAsync(Provenance p, CancellationToken cancellationToken)
    {
        var t = p.Trust;
        if (!EvidenceRules.IsRepository(t.Repository) || !EvidenceRules.ValidPath(t.Workflow) || !t.Workflow.StartsWith(".github/workflows/", StringComparison.Ordinal)
            || !EvidenceRules.IsOid(t.WorkflowCommit) || !EvidenceRules.IsDigest(t.BundleSha256) || p.ArtifactId <= 0 || p.RunId <= 0 || p.Attempt < 1)
        {
            throw new RemoteAcceptException("missing/invalid operator trust pins or Actions identity");
        }

        var run = await ApiJsonAsync($"repos/{t.Repository}/actions/runs/{p.RunId.ToString(CultureInfo.InvariantCulture)}/attempts/{p.Attempt.ToString(CultureInfo.InvariantCulture)}", cancellationToken).ConfigureAwait(false);
        var runRepository = StrictJson.LooseObj(run, "repository");
        var runHeadRepository = StrictJson.LooseObj(run, "head_repository");
        var runEvent = StrictJson.LooseStr(run, "event");
        if (StrictJson.LooseInt64(run, "id") != p.RunId || StrictJson.LooseInt64(run, "run_attempt") != p.Attempt
            || StrictJson.LooseStr(run, "head_sha") != t.WorkflowCommit || StrictJson.LooseStr(run, "path") != t.Workflow
            || (runRepository is null ? "" : StrictJson.LooseStr(runRepository, "full_name")) != t.Repository
            || (runHeadRepository is null ? "" : StrictJson.LooseStr(runHeadRepository, "full_name")) != t.Repository
            || (runEvent != "workflow_dispatch" && runEvent != "schedule") || StrictJson.LooseStr(run, "status") != "completed")
        {
            throw new RemoteAcceptException("actions run is not the pinned completed same-repository workflow");
        }

        var conclusion = StrictJson.LooseStr(run, "conclusion");
        if (conclusion != "success" && conclusion != "failure")
        {
            throw new RemoteAcceptException("actions run was cancelled, timed out, or has an unknown conclusion");
        }

        var artifact = await ApiJsonAsync($"repos/{t.Repository}/actions/artifacts/{p.ArtifactId.ToString(CultureInfo.InvariantCulture)}", cancellationToken).ConfigureAwait(false);
        var fullDigest = StrictJson.LooseStr(artifact, "digest");
        var digest = fullDigest.StartsWith("sha256:", StringComparison.Ordinal) ? fullDigest["sha256:".Length..] : fullDigest;
        var size = StrictJson.LooseInt64(artifact, "size_in_bytes");
        var workflowRun = StrictJson.LooseObj(artifact, "workflow_run");
        if (StrictJson.LooseInt64(artifact, "id") != p.ArtifactId || StrictJson.LooseBool(artifact, "expired")
            || (workflowRun is null ? 0 : StrictJson.LooseInt64(workflowRun, "id")) != p.RunId
            || (workflowRun is null ? "" : StrictJson.LooseStr(workflowRun, "head_sha")) != t.WorkflowCommit
            || size < 1 || !EvidenceRules.IsDigest(digest) || fullDigest != "sha256:" + digest)
        {
            throw new RemoteAcceptException("artifact metadata/digest does not match the trusted run");
        }

        if (p.ArchiveSha256.Length > 0 && p.ArchiveSha256 != digest)
        {
            throw new RemoteAcceptException("artifact digest changed");
        }

        return new ArtifactIdentity(digest, size);
    }

    private static async Task<JsonObject> ApiJsonAsync(string endpoint, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        await GhCliApi.GetAsync(endpoint, body, cancellationToken).ConfigureAwait(false);
        return StrictJson.ParseLoose(body.ToArray());
    }

    private static void Extract(string archive, string root)
    {
        using var zip = ZipFile.OpenRead(archive);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ulong total = 0;
        foreach (var f in zip.Entries)
        {
            var name = f.FullName;
            var p = name.EndsWith('/') ? name[..^1] : name;
            var unix = (f.ExternalAttributes >>> 16) & 0xF000;
            if (!EvidenceRules.ValidPath(p) || unix == 0xA000)
            {
                throw new RemoteAcceptException($"unsafe archive entry {name}");
            }

            if (!seen.Add(p.ToLowerInvariant()))
            {
                throw new RemoteAcceptException($"duplicate archive path {p}");
            }

            if (name.EndsWith('/'))
            {
                continue;
            }

            if (unix is not (0 or 0x8000))
            {
                throw new RemoteAcceptException("nonregular archive entry");
            }

            if (!(RemoteExport.IsTextExt(RemoteExport.DiagExt(p)) || RemoteExport.DiagExt(p) == ".png"))
            {
                throw new RemoteAcceptException($"non-diagnostic archive entry {p}");
            }

            var length = (ulong)f.Length;
            if (length >= long.MaxValue || total > long.MaxValue - length)
            {
                throw new RemoteAcceptException("expanded evidence exceeds size limit");
            }

            total += length;
        }

        foreach (var f in zip.Entries)
        {
            var p = Path.Combine(root, f.FullName.Replace('/', Path.DirectorySeparatorChar));
            if (f.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(p);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            ExtractFile(f, p);
            if (RemoteExport.DiagExt(p) == ".png")
            {
                RemoteExport.PublicDiagnostic(p, File.ReadAllBytes(p));
            }
        }

        EvidenceTree.Open(root);
    }

    private static void ExtractFile(ZipArchiveEntry entry, string path)
    {
        using var input = entry.Open();
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[1 << 16];
        long written = 0;
        uint crc = 0;
        int n;
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            written += n;
            if (written > entry.Length)
            {
                throw new RemoteAcceptException($"archive entry {entry.FullName} is larger than its declared size");
            }

            crc = Crc32.Update(crc, buffer.AsSpan(0, n));
            output.Write(buffer, 0, n);
        }

        if (written != entry.Length || crc != entry.Crc32)
        {
            throw new RemoteAcceptException($"archive entry {entry.FullName} fails its size or checksum");
        }
    }

    private static void MatchProvenance(Run r, Provenance p)
    {
        if (r.Repository != p.Trust.Repository || r.WorkflowCommit != p.Trust.WorkflowCommit || r.Bundle.Sha256 != p.Trust.BundleSha256
            || r.Trigger.RunId != p.RunId || r.Trigger.Attempt != p.Attempt)
        {
            throw new RemoteAcceptException("manifest does not match authenticated provenance");
        }
    }

    private static void RejectSynthetic(Evaluation e)
    {
        // #381 currently ships native-read-v1-empty. No production classifier authorizes a retry; a supplied
        // classification alone cannot enable one.
        foreach (var c in e.Aggregate.Cases)
        {
            if (c.AttemptCount > 1)
            {
                throw new RemoteAcceptException($"production retry is not enabled by native-read-v1-empty: {c.Name}");
            }
        }

        foreach (var row in e.Report.Cases)
        {
            if (StrictJson.LooseBool(row, "fixture_only"))
            {
                throw new RemoteAcceptException("synthetic fixture is not native acceptance evidence");
            }
        }
    }

    private static async Task<string> GitAsync(string repo, string[] args, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }

        using var process = Process.Start(start) ?? throw new RemoteAcceptException("git: could not start git");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var combined = (await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            throw new RemoteAcceptException($"git {string.Join(' ', args)}: exit status {process.ExitCode}: {combined}");
        }

        return combined;
    }

    private readonly record struct ArtifactIdentity(string Digest, long Size);

    /// <summary>A write-only stream that refuses to take more than the artifact's declared size (Go <c>boundedWriter</c>).</summary>
    private sealed class BoundedStream(Stream inner, long left) : Stream
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
            if (count > left)
            {
                throw new RemoteAcceptException("artifact exceeds size limit");
            }

            inner.Write(buffer, offset, count);
            left -= count;
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.FlushAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
