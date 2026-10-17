using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RimGovernor.Brain.Acceptance;

/// <summary>What <see cref="NativeModBuild.BuildAsync"/> builds, from where, and into which fresh directory.</summary>
public sealed record NativeModBuildOptions
{
    /// <summary>The checkout whose sources the mod is built from.</summary>
    public required string Repo { get; init; }

    /// <summary>The directory holding the game's <c>Assembly-CSharp.dll</c>, <c>UnityStubs.dll</c> and <c>Waylith.dll</c>.</summary>
    public required string RimWorldDir { get; init; }

    /// <summary>The Harmony workshop item's <c>0Harmony.dll</c>.</summary>
    public required string Harmony { get; init; }

    /// <summary>The fixtures to compile in (names <see cref="FixtureInputs.FixtureNames"/> lists); empty is the production build.</summary>
    public IReadOnlyList<string> Fixtures { get; init; } = [];

    /// <summary>A directory that must not exist yet; it receives <c>build/</c> (logs, the source copy) and the package <c>RimGovernor/</c>.</summary>
    public required string OutputRoot { get; init; }

    /// <summary>Receives progress lines.</summary>
    public TextWriter Log { get; init; } = TextWriter.Null;
}

/// <summary>
/// Builds the native mod package (Bridge, Runtime, the vendored host and the brain assemblies they load) the way the
/// game loads it, and records how in <c>native-manifest.json</c> (#3080, #3115, #3291). It compiles a private copy of
/// the sources <see cref="NativeSourceInputs"/> names, so concurrent fixture and production builds share no obj or
/// restore state and never write under the checkout; the manifest's <c>sourceTree</c> is the hash of that copy, which
/// <see cref="NativeSourceInputs.SourceTreeHash(string)"/> reproduces from a worktree.
/// </summary>
public static class NativeModBuild
{
    /// <summary>What every fixture build carries: the acceptance harnesses quiet and start the debug colony through them.</summary>
    public static readonly IReadOnlyList<string> AlwaysFixtures = ["DebugStartFixture", "FreezeNeedsFixture", "LetterFixture", "QuietStorytellerFixture"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The game's assemblies, in the RimWorld install, the build compiles against.</summary>
    private static readonly string[] GameAssemblies = ["Assembly-CSharp.dll", "UnityStubs.dll", "Waylith.dll"];

    /// <summary>The brain assemblies the package must carry beside the Bridge.</summary>
    private static readonly string[] BrainAssemblies =
    [
        "Domain", "Policy", "Contracts", "Bridge", "Bridge.Contracts", "NativeFacade", "HttpChannel", "Telemetry", "Host",
        "Snapshot", "Observation", "Store", "Executor", "BuildingRuntime", "PlayerApi", "Simulation",
    ];

    /// <summary>The fixture list a build asked for <paramref name="wanted"/> records: none, or those plus <see cref="AlwaysFixtures"/>, sorted.</summary>
    public static IReadOnlyList<string> RecordedFixtures(IEnumerable<string> wanted)
    {
        var set = wanted.ToHashSet(StringComparer.Ordinal);
        return set.Count == 0 ? [] : [.. set.Concat(AlwaysFixtures).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// True when <paramref name="package"/> holds a build of exactly these sources, game inputs and fixtures, so a
    /// caller that only needs a current package (the test gate) can keep it.
    /// </summary>
    public static bool IsCurrent(string package, NativeModBuildOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        var path = Path.Combine(package, SetupPackage.PackageManifestName);
        if (!File.Exists(path))
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            var fixtures = root.GetProperty("fixtures").EnumerateArray().Select(f => f.GetString() ?? "").Order(StringComparer.Ordinal);
            var inputs = root.GetProperty("inputs").EnumerateArray()
                .ToDictionary(i => i.GetProperty("path").GetString() ?? "", i => i.GetProperty("sha256").GetString() ?? "", StringComparer.OrdinalIgnoreCase);
            var wantedInputs = GameInputs(o);
            return root.GetProperty("sourceTree").GetString() == NativeSourceInputs.SourceTreeHash(o.Repo)
                && fixtures.SequenceEqual(RecordedFixtures(o.Fixtures), StringComparer.Ordinal)
                && inputs.Count == wantedInputs.Count
                && wantedInputs.All(f => inputs.TryGetValue(f, out var sha) && sha == Sha256(f));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>Builds the package and returns its directory (<c>&lt;OutputRoot&gt;/RimGovernor</c>).</summary>
    public static async Task<string> BuildAsync(NativeModBuildOptions o, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(o);
        var fixtures = RecordedFixtures(o.Fixtures);
        var known = FixtureInputs.FixtureNames(o.Repo);
        var unknown = fixtures.Where(f => !known.Contains(f)).ToList();
        if (unknown.Count > 0)
        {
            throw new SetupException($"unknown fixture {string.Join(", ", unknown)}: no source under {FixtureInputs.FixtureRoot} and no build flag of {FixtureInputs.NativeProject}");
        }
        var role = fixtures.Count == 0 ? "production" : "fixture";
        var output = Path.GetFullPath(o.OutputRoot);
        if (Directory.Exists(output) || File.Exists(output))
        {
            throw new SetupException("build output must be fresh: " + output);
        }
        var repo = Path.GetFullPath(o.Repo);
        var native = Path.Combine(repo, "integrations", "rimgovernor-native");
        var rimWorld = Path.GetFullPath(o.RimWorldDir);
        var harmony = Path.GetFullPath(o.Harmony);
        foreach (var required in new[]
        {
            Path.Combine(native, "src", "Bridge", "RimGovernor.Bridge.csproj"), Path.Combine(rimWorld, "Assembly-CSharp.dll"), harmony,
            Path.Combine(repo, "integrations", "rimgovernor-host", "src", "Host", "RimGovernor.Host.csproj"),
            Path.Combine(native, "Notices", "host", "PROVENANCE.md"), Path.Combine(native, "Notices", "headless", "LICENSE"),
            Path.Combine(native, "Notices", "headless", "PROVENANCE.md"), Path.Combine(native, "Notices", "companion", "PROVENANCE.md"),
        })
        {
            if (!File.Exists(required))
            {
                throw new SetupException("required native build input missing: " + required);
            }
        }
        var sdk = (await RunAsync(repo, ["--version"], null, ct).ConfigureAwait(false)).Trim();
        if (!Version.TryParse(sdk.Split('-')[0], out var version) || version.Major < 10)
        {
            throw new SetupException($"the native build needs .NET SDK 10 (net10.0); found {sdk}");
        }

        var build = Path.Combine(output, "build");
        var package = Path.Combine(output, "RimGovernor");
        var source = Path.Combine(build, "source");
        var compiled = Path.Combine(build, "compiled");
        var hostCompiled = Path.Combine(build, "host-compiled");
        _ = Directory.CreateDirectory(package);
        o.Log.WriteLine($"native build: copying sources into {source}");
        var copied = NativeSourceInputs.CopySources(repo, source);
        var sourceTree = NativeSourceInputs.HashLines(copied);

        await CompileAsync(repo, source, copied, fixtures, role, rimWorld, harmony, build, compiled, hostCompiled, o.Log, ct).ConfigureAwait(false);

        var bridgeTools = Path.Combine(package, "BridgeTools", "RimGovernor");
        var assemblies = Path.Combine(package, "Assemblies");
        _ = Directory.CreateDirectory(bridgeTools);
        _ = Directory.CreateDirectory(assemblies);
        CopyRequired(Path.Combine(compiled, "RimGovernor.Runtime.dll"), assemblies);
        CopyRequired(Path.Combine(compiled, "RimGovernor.Bridge.dll"), bridgeTools);
        // The host ships in the mod's general Assemblies/ (the game loads it as a mod assembly): its own assemblies plus
        // the resolved NuGet runtime DLLs, each with a retained notice.
        foreach (var dll in Directory.EnumerateFiles(hostCompiled, "RimGovernor.Host*.dll"))
        {
            File.Copy(dll, Path.Combine(assemblies, Path.GetFileName(dll)), overwrite: true);
        }
        if (!File.Exists(Path.Combine(assemblies, "RimGovernor.Host.dll")))
        {
            throw new SetupException("expected host output missing: RimGovernor.Host.dll");
        }
        var copiedNative = Path.Combine(source, "integrations", "rimgovernor-native");
        var hostDependencies = new List<Dependency>();
        foreach (var dep in Dependencies(Path.Combine(hostCompiled, "runtime-dependencies.tsv"), allowUnidentified: true))
        {
            RequireNotice(copiedNative, "host", dep);
            File.Copy(Path.Combine(hostCompiled, dep.File), Path.Combine(assemblies, dep.File), overwrite: true);
            hostDependencies.Add(dep);
        }
        // Only resolved NuGet runtime DLLs are redistributed, beside their requesting Bridge assembly where the host's
        // scoped resolver searches first. Native SQLite (Brain.Store) builds to runtimes/<rid>/native and is listed once
        // per RID; the game is win-x64 and probes the requesting assembly's own directory, so only that binary ships, flat.
        var runtimeDependencies = new List<Dependency>();
        foreach (var dep in Dependencies(Path.Combine(compiled, "runtime-dependencies.tsv"), allowUnidentified: false))
        {
            RequireNotice(copiedNative, "bridge", dep);
            var dll = Path.Combine(compiled, dep.File);
            if (!File.Exists(dll))
            {
                dll = Path.Combine(compiled, "runtimes", "win-x64", "native", dep.File);
            }
            if (!File.Exists(dll))
            {
                throw new SetupException($"runtime dependency output missing: {dep.Package}|{dep.Version}|{dep.File}");
            }
            if (runtimeDependencies.Any(d => d.Package == dep.Package && d.File == dep.File))
            {
                continue;
            }
            File.Copy(dll, Path.Combine(bridgeTools, dep.File), overwrite: true);
            runtimeDependencies.Add(dep);
        }
        // The brain assemblies ride beside the Bridge: the host resolves a Bridge reference by simple name from there.
        foreach (var dll in Directory.EnumerateFiles(compiled, "RimGovernor.Brain.*.dll"))
        {
            File.Copy(dll, Path.Combine(bridgeTools, Path.GetFileName(dll)), overwrite: true);
        }
        foreach (var brain in BrainAssemblies)
        {
            if (!File.Exists(Path.Combine(bridgeTools, $"RimGovernor.Brain.{brain}.dll")))
            {
                throw new SetupException($"expected brain output missing: RimGovernor.Brain.{brain}.dll");
            }
        }
        foreach (var dir in new[] { "About", "Defs", "Textures", "Notices" })
        {
            CopyTree(Path.Combine(copiedNative, dir), Path.Combine(package, dir), _ => true);
        }
        File.Copy(Path.Combine(copiedNative, "README.md"), Path.Combine(package, "README.md"));
        // The source snapshot the package was built from, without binaries or the game's generated def table.
        CopyTree(source, Path.Combine(package, "Source"), relative =>
            Path.GetExtension(relative).ToLowerInvariant() is not (".dll" or ".pdb" or ".exe")
            && !relative.EndsWith("/csharp/Defs.cs", StringComparison.Ordinal)
            && !relative.Split('/').Any(part => part is "obj" or "bin"));

        await WriteManifestAsync(o, repo, package, role, fixtures, runtimeDependencies, hostDependencies, sourceTree, sdk, ct).ConfigureAwait(false);
        o.Log.WriteLine($"native build: {role} package {package}");
        return package;
    }

    /// <summary>
    /// Compiles in a work directory that persists between builds, so a rebuild recompiles only what changed. It is shared
    /// by every worktree (under the main checkout's .rimgovernor): MSBuild's incremental state holds absolute paths, so
    /// only the same directory lets a new worktree start from an earlier build's cache. One directory per fixture set,
    /// since the fixture flags are compile-time properties MSBuild's up-to-date check does not see. A machine-wide mutex
    /// per directory serializes the builds that share it.
    /// </summary>
    private static async Task CompileAsync(
        string repo, string source, IReadOnlyDictionary<string, string> copied, IReadOnlyList<string> fixtures, string role,
        string rimWorld, string harmony, string build, string compiled, string hostCompiled, TextWriter log, CancellationToken ct)
    {
        var mainRepo = await MainCheckoutAsync(repo, ct).ConfigureAwait(false);
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(',', fixtures))))[..8];
        var work = Path.Combine(mainRepo, ".rimgovernor", "native-builds", "_work", $"{role}-{key}");
        var lockName = @"Global\rimgovernor-native-build-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(work.ToLowerInvariant())))[..24];
        log.WriteLine($"native build: waiting for the work directory {work}");
        // A Mutex belongs to the thread that took it: one dedicated thread creates, takes, releases and disposes it, so
        // a build cancelled while waiting lets go the moment the wait ends.
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource();
        var abandoned = false;
        var owner = new Thread(() =>
        {
            using var mutex = new Mutex(false, lockName);
            try
            {
                _ = mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                // The previous holder died holding it; its half-built outputs are rebuilt incrementally.
                abandoned = true;
            }
            held.SetResult();
            release.Task.Wait(CancellationToken.None);
            mutex.ReleaseMutex();
        }) { IsBackground = true };
        owner.Start();
        try
        {
            await held.Task.WaitAsync(ct).ConfigureAwait(false);
            if (abandoned)
            {
                log.WriteLine("native build: the previous build of this work directory died; rebuilding over its output");
            }
            SyncWork(source, work, copied);
            var published = Path.Combine(work, "_out", "published");
            var workCompiled = Path.Combine(work, "_out", "compiled");
            var workHostCompiled = Path.Combine(work, "_out", "host-compiled");
            // Each project is published win-x64 ReadyToRun (#3291), so the bridge and brain start without JIT-compiling
            // ~27 MB of IL. OutputPath still receives the plain build of the whole graph; PublishDir the ReadyToRun set.
            string[] publish = ["-c", "Release", "-v", "minimal", "-p:RestoreLockedMode=true", "-r", "win-x64", "--self-contained", "false",
                "-p:PublishReadyToRun=true", $"-p:RimWorldDir={rimWorld}", $"-p:HarmonyAssembly={harmony}"];
            log.WriteLine("native build: host");
            await PublishAsync(work, ["publish", Path.Combine(work, "integrations", "rimgovernor-host", "src", "Host", "RimGovernor.Host.csproj"),
                $"-p:OutputPath={workHostCompiled}/", $"-p:PublishDir={published}/host/", .. publish], Path.Combine(build, "host-build.log"), "host", ct).ConfigureAwait(false);
            // The Bridge publish set holds the Bridge and the brain assemblies; RimGovernor.Runtime is a private=false
            // reference (it ships in Assemblies/), so it is published on its own with the same global properties.
            foreach (var (name, project) in new[] { ("bridge", "Bridge/RimGovernor.Bridge.csproj"), ("runtime", "Runtime/RimGovernor.Runtime.csproj") })
            {
                log.WriteLine("native build: " + name);
                await PublishAsync(work, ["publish", Path.Combine(work, "integrations", "rimgovernor-native", "src", project),
                    $"-p:OutputPath={workCompiled}/", $"-p:PublishDir={published}/{name}/", .. publish, .. fixtures.Select(f => $"-p:{f}=true")],
                    Path.Combine(build, "build.log"), name, ct).ConfigureAwait(false);
            }
            // Another worktree's build may reuse this directory the moment the lock is released: take this build's output first.
            CopyTree(workCompiled, compiled, _ => true);
            CopyTree(workHostCompiled, hostCompiled, _ => true);
            // Our own assemblies ship as their ReadyToRun copies; NuGet runtime DLLs ship as restored.
            foreach (var (from, to) in new[] { ("bridge", compiled), ("runtime", compiled), ("host", hostCompiled) })
            {
                foreach (var dll in Directory.EnumerateFiles(Path.Combine(published, from), "RimGovernor.*.dll"))
                {
                    var target = Path.Combine(to, Path.GetFileName(dll));
                    if (File.Exists(target))
                    {
                        File.Copy(dll, target, overwrite: true);
                    }
                }
            }
            // Work directories of fixture sets nobody has built for a while would otherwise pile up.
            foreach (var stale in Directory.EnumerateDirectories(Path.GetDirectoryName(work)!))
            {
                if (!string.Equals(stale, work, StringComparison.OrdinalIgnoreCase) && Directory.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddDays(-7))
                {
                    TryDelete(stale, log);
                }
            }
        }
        finally
        {
            release.SetResult();
            if (held.Task.IsCompleted)
            {
                owner.Join();
            }
        }
    }

    /// <summary>
    /// Mirrors the pristine copy into the work directory, leaving its build output (obj, bin, _out) alone. A file whose
    /// content changed is rewritten with the current time, so it is newer than the outputs built from the previous one;
    /// an unchanged file keeps its timestamp, so MSBuild sees nothing to do. Unchanged means the file and this sync's own
    /// record of what it last wrote both match: a file some other writer put there (the deleted build_native_mod.ps1
    /// kept the source's old timestamp, so outputs built from other content looked current) is rewritten, and a work
    /// directory without the record is rewritten whole, once.
    /// </summary>
    internal static void SyncWork(string source, string work, IReadOnlyDictionary<string, string> copied)
    {
        _ = Directory.CreateDirectory(work);
        Directory.SetLastWriteTimeUtc(work, DateTime.UtcNow);
        var recordPath = Path.Combine(work, "_out", "synced-sources.txt");
        var record = File.Exists(recordPath)
            ? File.ReadLines(recordPath).Select(line => line.Split('\t')).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal)
            : [];
        var now = DateTime.UtcNow;
        foreach (var (relative, sha) in copied)
        {
            var target = Path.Combine(work, relative.Replace('/', Path.DirectorySeparatorChar));
            if (record.GetValueOrDefault(relative) == sha && File.Exists(target) && Sha256(target) == sha)
            {
                continue;
            }
            _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(source, relative.Replace('/', Path.DirectorySeparatorChar)), target, overwrite: true);
            File.SetLastWriteTimeUtc(target, now);
        }
        DeleteRemoved(work, work, copied);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
        File.WriteAllLines(recordPath, copied.Select(c => $"{c.Key}\t{c.Value}"));
    }

    /// <summary>Deletes the files under <paramref name="dir"/> the copy no longer has, without descending into build output.</summary>
    private static void DeleteRemoved(string work, string dir, IReadOnlyDictionary<string, string> copied)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (!copied.ContainsKey(Path.GetRelativePath(work, file).Replace(Path.DirectorySeparatorChar, '/')))
            {
                File.Delete(file);
            }
        }
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (Path.GetFileName(sub) is not ("obj" or "bin" or "_out"))
            {
                DeleteRemoved(work, sub, copied);
            }
        }
    }

    private static async Task WriteManifestAsync(
        NativeModBuildOptions o, string repo, string package, string role, IReadOnlyList<string> fixtures,
        List<Dependency> runtimeDependencies, List<Dependency> hostDependencies, string sourceTree, string sdk, CancellationToken ct)
    {
        var revision = (await GitAsync(repo, ["rev-parse", "HEAD"], ct).ConfigureAwait(false)).Trim();
        var dirty = (await GitAsync(repo, ["status", "--porcelain"], ct).ConfigureAwait(false)).Trim().Length > 0;
        var files = Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var manifest = new
        {
            formatVersion = 1,
            packageId = SetupPackage.NativePackage,
            role,
            fixtures,
            runtimeDependencies = runtimeDependencies.Select(d => new { package = d.Package, version = d.Version, file = d.File }),
            hostDependencies = hostDependencies.Select(d => new { package = d.Package, version = d.Version, file = d.File }),
            sourceRevision = revision,
            sourceDirty = dirty,
            sourceTree,
            dotnetSdk = sdk,
            inputs = GameInputs(o).Select(f => new { path = f, sha256 = Sha256(f) }),
            files = files.Select(f => new { path = Path.GetRelativePath(package, f).Replace(Path.DirectorySeparatorChar, '/'), sha256 = Sha256(f) }),
        };
        await File.WriteAllBytesAsync(
            Path.Combine(package, SetupPackage.PackageManifestName),
            JsonSerializer.SerializeToUtf8Bytes(manifest, Indented),
            ct).ConfigureAwait(false);
    }

    /// <summary>The game files the build compiles against, as the manifest records them: full paths, sorted, distinct.</summary>
    private static List<string> GameInputs(NativeModBuildOptions o)
    {
        var rimWorld = Path.GetFullPath(o.RimWorldDir);
        return [.. new[] { Path.GetFullPath(o.Harmony) }
            .Concat(GameAssemblies.Select(f => Path.Combine(rimWorld, f)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    private sealed record Dependency(string Package, string Version, string File);

    /// <summary>The <c>package|version|file</c> rows a native project writes; a row without a package is the project's own output.</summary>
    private static IEnumerable<Dependency> Dependencies(string tsv, bool allowUnidentified)
    {
        foreach (var line in File.ReadAllLines(tsv))
        {
            var fields = line.Split('|');
            if (fields.Length != 3 || (!allowUnidentified && fields[0].Length == 0))
            {
                throw new SetupException("malformed runtime dependency: " + line);
            }
            if (fields[0].Length > 0)
            {
                yield return new Dependency(fields[0], fields[1], fields[2]);
            }
        }
    }

    private static void RequireNotice(string copiedNative, string kind, Dependency dep)
    {
        var notice = Path.Combine(copiedNative, "Notices", kind, dep.Package.ToLowerInvariant(), dep.Version, "LICENSE");
        if (!File.Exists(notice))
        {
            throw new SetupException($"missing {kind} dependency notice: {dep.Package}|{dep.Version}|{dep.File}");
        }
    }

    private static void CopyRequired(string file, string dir)
    {
        if (!File.Exists(file))
        {
            throw new SetupException("expected native output missing: " + file);
        }
        File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
    }

    /// <summary>Copies the files under <paramref name="from"/> that <paramref name="keep"/> admits (by forward-slash relative path), keeping timestamps.</summary>
    private static void CopyTree(string from, string to, Func<string, bool> keep)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, file).Replace(Path.DirectorySeparatorChar, '/');
            if (!keep(relative))
            {
                continue;
            }
            var target = Path.Combine(to, relative.Replace('/', Path.DirectorySeparatorChar));
            _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static async Task<string> MainCheckoutAsync(string repo, CancellationToken ct)
    {
        try
        {
            var common = (await GitAsync(repo, ["rev-parse", "--path-format=absolute", "--git-common-dir"], ct).ConfigureAwait(false)).Trim();
            return common.Length > 0 ? Path.GetDirectoryName(Path.GetFullPath(common))! : repo;
        }
        catch (SetupException)
        {
            return repo;
        }
    }

    private static Task<string> GitAsync(string repo, string[] args, CancellationToken ct) => RunAsync(repo, ["-C", repo, .. args], null, ct, "git");

    private static async Task PublishAsync(string dir, string[] args, string logFile, string name, CancellationToken ct)
    {
        try
        {
            _ = await RunAsync(dir, args, logFile, ct).ConfigureAwait(false);
        }
        catch (SetupException e)
        {
            throw new SetupException($"native build ({name}) failed; inspect {logFile}", e);
        }
    }

    /// <summary>Runs a command to completion; its output is returned, or appended to <paramref name="logFile"/> when given. A non-zero exit throws.</summary>
    private static async Task<string> RunAsync(string dir, string[] args, string? logFile, CancellationToken ct, string file = "dotnet")
    {
        var info = new ProcessStartInfo(file)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        using var process = Process.Start(info) ?? throw new SetupException($"{file}: did not start");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        var output = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
        if (logFile is not null)
        {
            await File.AppendAllTextAsync(logFile, output, ct).ConfigureAwait(false);
        }
        if (process.ExitCode != 0)
        {
            throw new SetupException(string.Create(CultureInfo.InvariantCulture, $"{file} {string.Join(' ', args)}: exit status {process.ExitCode}{(logFile is null ? ": " + output.Trim() : "")}"));
        }
        return output;
    }

    private static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Best effort: a directory still in use is left for the next build to prune.</summary>
    private static void TryDelete(string dir, TextWriter log)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.WriteLine($"native build: left {dir}: {e.Message}");
        }
    }
}
