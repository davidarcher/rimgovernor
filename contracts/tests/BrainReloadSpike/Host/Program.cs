using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using StringValue = Google.Protobuf.WellKnownTypes.StringValue;
using RimGovernor.BrainSpike.Contracts;

// Spike: can a brain assembly be loaded into a collectible AssemblyLoadContext, run, and be
// unloaded again, and which common hazards stop the unload? See
// docs/developers/architecture/brain-hot-reload-feasibility.md.

var brainPath = Path.Combine(AppContext.BaseDirectory, "brain", "RimGovernor.BrainSpike.Brain.dll");
var contractsPath = typeof(IBrain).Assembly.Location;
var brainBytes = File.ReadAllBytes(brainPath);

var failures = 0;

Console.WriteLine($"runtime {RuntimeInformation.FrameworkDescription}, brain {brainBytes.Length} bytes");
Console.WriteLine();
Console.WriteLine($"{"scenario",-20} {"expect",-9} {"actual",-9} {"ok",-3} detail");

foreach (var (scenario, expect) in Scenarios.All)
{
    var host = new SpikeHost();
    var (alc, outcome, detail) = Spike.RunOnce(scenario, brainBytes, contractsPath, host);
    var unloaded = Spike.WaitUnloaded(alc, Spike.UnloadTimeoutMs);
    GC.KeepAlive(host);

    var actual = outcome switch
    {
        "CastFailed" => "CastFails",
        "Error" => "Error",
        _ => unloaded ? "Unloads" : "Blocked"
    };
    var ok = expect == "Observe" || actual == expect;
    if (!ok)
        failures++;
    Console.WriteLine($"{scenario,-20} {expect,-9} {actual,-9} {(ok ? "yes" : "NO"),-3} {detail}");
}

Console.WriteLine();
failures += Spike.ReloadCycles(brainBytes, contractsPath, cycles: 25);
failures += Spike.FileLockChecks(brainPath, brainBytes, contractsPath);

Console.WriteLine();
Console.WriteLine(failures == 0 ? "RESULT: all expectations met" : $"RESULT: {failures} expectation(s) not met");
return failures == 0 ? 0 : 1;

internal static class Scenarios
{
    // Unloads: must unload. Blocked: must NOT unload (negative control or known pin).
    // CastFails: the brain loaded its own copy of the contracts. Observe: record, don't assert.
    // The protobuf and json-* expectations were set from observed runs on .NET 10.0.12, not predicted.
    // json-*+flag rows are root-cause probes: clear/accessor/options act on System.Text.Json's
    // caches by reflection before the unload; evict runs host-side JSON work after the cache expiry.
    public static readonly (string Name, string Expect)[] All =
    [
        ("baseline", "Unloads"),
        ("protobuf", "Unloads"),
        ("json-default", "Blocked"),
        ("json-own-options", "Blocked"),
        ("json-host-options", "Blocked"),
        ("json-serialize-only", "Blocked"),
        ("json-poco", "Blocked"),
        ("json-sourcegen", "Unloads"),
        ("json-poco+clear", "Unloads"),
        ("json-poco+evict", "Blocked"),
        ("json-own-options+accessor+options", "Unloads"),
        ("json-unique-options", "Blocked"),
        ("json-unique-options+accessor", "Unloads"),
        ("json-unique-options+evict", "Unloads"),
        ("json-own-options+accessor", "Blocked"),
        ("json-own-options+options", "Blocked"),
        ("json-default+accessor", "Blocked"),
        ("json-default+options", "Blocked"),
        ("json-host-options+accessor", "Blocked"),
        ("json-host-options+options", "Blocked"),
        ("json-default+accessor+options", "Unloads"),
        ("json-default+clear", "Unloads"),
        ("task-clean", "Unloads"),
        ("task-leak", "Blocked"),
        ("channel-clean", "Unloads"),
        ("timer-clean", "Unloads"),
        ("timer-leak", "Blocked"),
        ("thread-clean", "Unloads"),
        ("thread-leak", "Blocked"),
        ("event-clean", "Unloads"),
        ("event-leak", "Blocked"),
        ("retained-type", "Blocked"),
        ("private-contracts", "CastFails")
    ];
}

internal sealed class SpikeHost : IBrainHost
{
    public JsonSerializerOptions SharedJson { get; } = new();

    public event Action<string>? Observation;

    public void Raise(string message) => Observation?.Invoke(message);
}

/// <summary>
/// The sharing policy under test: the brain assembly is loaded from bytes into this context;
/// everything else (contracts, Google.Protobuf, the BCL) falls through to the default context
/// so type identity holds across the boundary.
/// </summary>
internal sealed class BrainLoadContext(string name, string contractsPath, bool shareContracts)
    : AssemblyLoadContext(name, isCollectible: true)
{
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (!shareContracts && assemblyName.Name == "RimGovernor.BrainSpike.Contracts")
            return LoadFromAssemblyPath(contractsPath);
        return null;
    }
}

internal static class Spike
{
    public static readonly int UnloadTimeoutMs =
        int.TryParse(Environment.GetEnvironmentVariable("BRAINSPIKE_TIMEOUT_MS"), out var ms) ? ms : 1500;

    // Simulates a host-side scan like AppDomain.GetAssemblies() that keeps a brain Type.
    private static readonly List<Type> Retained = [];

    /// <summary>
    /// Loads, runs and unloads one brain. Returns only a weak reference and plain strings so no
    /// stack slot or exception keeps the context alive after the call.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static (WeakReference Alc, string Outcome, string Detail) RunOnce(
        string scenario, byte[] brainBytes, string contractsPath, SpikeHost host)
    {
        // "name+flag+flag": probe flags that act on System.Text.Json's caches before the unload.
        var parts = scenario.Split('+');
        scenario = parts[0];
        var clearJson = parts.Contains("clear");
        var clearAccessor = parts.Contains("accessor");
        var clearOptions = parts.Contains("options");
        var evictJson = parts.Contains("evict");
        var ctx = new BrainLoadContext("brain", contractsPath, shareContracts: scenario != "private-contracts");
        var weak = new WeakReference(ctx);
        var outcome = "Loaded";
        var detail = "";
        try
        {
            using var stream = new MemoryStream(brainBytes);
            var assembly = ctx.LoadFromStream(stream);
            var factoryType = assembly.GetType("RimGovernor.BrainSpike.Brain.BrainFactory", throwOnError: true)!;
            var instance = Activator.CreateInstance(factoryType);
            if (instance is not IBrainFactory factory)
            {
                outcome = "CastFailed";
                detail = "factory is not this context's IBrainFactory (private contracts copy)";
            }
            else
            {
                var brain = factory.Create(scenario, host);
                if (scenario == "retained-type")
                    Retained.Add(brain.GetType());
                for (var i = 0; i < 3; i++)
                {
                    var reply = brain.Tick(new StringValue { Value = "f" + i });
                    if (reply.Value != "ok:f" + i)
                        throw new InvalidOperationException("unexpected reply " + reply.Value);
                }

                host.Raise("observation");
                Thread.Sleep(30);
                detail = brain.Describe();
                brain.Dispose();
            }
        }
        catch (Exception ex)
        {
            outcome = "Error";
            detail = ex.GetType().Name + ": " + ex.Message;
        }

        if (clearJson)
            detail += " | " + ClearJsonCaches();
        if (evictJson)
            detail += " | " + ExerciseJsonCache();
        if (clearAccessor)
            detail += " | " + ClearAccessorOnly();
        if (clearOptions)
            detail += " | " + ClearOptionsOnly(host);
        ctx.Unload();
        return (weak, outcome, detail);
    }

    private const BindingFlags NonPublic =
        BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>Clears only the static ReflectionEmitCachingMemberAccessor cache.</summary>
    private static string ClearAccessorOnly()
    {
        var resolver = typeof(JsonSerializer).Assembly
            .GetType("System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver", throwOnError: true)!;
        var accessor = resolver.GetField("s_memberAccessor", NonPublic)!.GetValue(null)!;
        accessor.GetType().GetMethod("Clear", NonPublic)!.Invoke(accessor, null);
        return "member accessor cache cleared";
    }

    /// <summary>Clears only the per-options type caches (Default and the host-owned options).</summary>
    private static string ClearOptionsOnly(SpikeHost host)
    {
        var clear = typeof(JsonSerializerOptions).GetMethod("ClearCaches", NonPublic)!;
        clear.Invoke(JsonSerializerOptions.Default, null);
        clear.Invoke(host.SharedJson, null);
        return "options caches cleared";
    }

    private sealed class HostPoco
    {
        public string Name { get; set; } = "";
    }

    /// <summary>Root-cause probe: ordinary host-side JSON use after the cache's sliding expiry.</summary>
    private static string ExerciseJsonCache()
    {
        Thread.Sleep(2500);
        for (var i = 0; i < 3; i++)
        {
            _ = JsonSerializer.Serialize(new HostPoco { Name = "host" }, new JsonSerializerOptions());
            Thread.Sleep(300);
        }

        return "host JSON activity after 2.5 s";
    }

    /// <summary>Root-cause probe: what the runtime's own hot-reload hook does to System.Text.Json caches.</summary>
    private static string ClearJsonCaches()
    {
        var handler = typeof(System.Text.Json.JsonSerializer).Assembly
            .GetType("System.Text.Json.JsonSerializerOptionsUpdateHandler", throwOnError: true)!;
        handler.GetMethod("ClearCache", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(null, [null]);
        return "STJ ClearCache(null) invoked";
    }

    public static bool WaitUnloaded(WeakReference alc, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (alc.IsAlive && sw.ElapsedMilliseconds < timeoutMs)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(10);
        }

        return !alc.IsAlive;
    }

    /// <summary>Load, run and unload the same brain repeatedly; every context must go away.</summary>
    public static int ReloadCycles(byte[] brainBytes, string contractsPath, int cycles)
    {
        var stuck = 0;
        for (var i = 0; i < cycles; i++)
        {
            var host = new SpikeHost();
            var (alc, outcome, _) = RunOnce("baseline", brainBytes, contractsPath, host);
            if (outcome != "Loaded" || !WaitUnloaded(alc, UnloadTimeoutMs))
                stuck++;
        }

        var remaining = AssemblyLoadContext.All.Count(c => c.Name == "brain");
        var ok = stuck == 0 && remaining == 0;
        Console.WriteLine($"reload x{cycles}: stuck={stuck}, live 'brain' contexts={remaining} -> {(ok ? "ok" : "NO")}");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// Loading from bytes must leave the DLL on disk writable so a rebuild can overwrite it
    /// while the game runs. Loading from a path is reported for contrast (Windows locks it).
    /// </summary>
    public static int FileLockChecks(string brainPath, byte[] brainBytes, string contractsPath)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "brain-spike-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var copy = Path.Combine(scratch, Path.GetFileName(brainPath));
        File.Copy(brainPath, copy);
        var failures = 0;
        try
        {
            var (fromBytesWritable, fromPathWritable) = Probe(copy, brainBytes, contractsPath);
            Console.WriteLine($"file after load from bytes: writable={fromBytesWritable} -> {(fromBytesWritable ? "ok" : "NO")}");
            Console.WriteLine($"file after load from path:  writable={fromPathWritable} (informational)");
            if (!fromBytesWritable)
                failures++;
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The path-loaded context may still hold the copy; cleanup is best effort.
            }
        }

        return failures;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (bool FromBytes, bool FromPath) Probe(string copy, byte[] brainBytes, string contractsPath)
    {
        var byBytes = new BrainLoadContext("lock-bytes", contractsPath, shareContracts: true);
        using (var stream = new MemoryStream(brainBytes))
            byBytes.LoadFromStream(stream);
        var bytesWritable = CanWrite(copy);
        byBytes.Unload();

        var byPath = new BrainLoadContext("lock-path", contractsPath, shareContracts: true);
        byPath.LoadFromAssemblyPath(copy);
        var pathWritable = CanWrite(copy);
        byPath.Unload();
        return (bytesWritable, pathWritable);
    }

    private static bool CanWrite(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
