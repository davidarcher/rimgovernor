using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One bundle's sidecar: what it holds and what it was taken under (the installed package, the Start and the
/// expansions, for whoever loads the save later).
/// </summary>
public sealed class Checkpoint
{
    public string Case { get; set; } = "";

    /// <summary>Names the bundle's directory: "t+7m" for the ring, "failed", or a phase name the window declared.</summary>
    public string Label { get; set; } = "";

    /// <summary>The run-phase offset the bundle was taken at (the label's t+...).</summary>
    public long OffsetMs { get; set; }

    public ulong Tick { get; set; }

    /// <summary>The .rws name.</summary>
    public string Save { get; set; } = "";

    /// <summary>Whether the bundle carries the service store.</summary>
    public bool Store { get; set; }

    public JsonObject? Identity { get; set; }

    /// <summary>The start's prepared reply at capture, for the record.</summary>
    public JsonObject? Prepared { get; set; }

    /// <summary>The window's serve spec at capture, for the record.</summary>
    public JsonObject? Serve { get; set; }

    public string SourceRevision { get; set; } = "";

    /// <summary>Hashes the installed native package's files (<see cref="Checkpoints.PackageHash"/>).</summary>
    public string Package { get; set; } = "";

    /// <summary>Hashes the Start (its kind, save and args).</summary>
    public string Start { get; set; } = "";

    public IReadOnlyList<string> Expansions { get; set; } = [];

    /// <summary>Which path took the save: "bridge" (lifecycle_save on the harness session), "service" (/api/lifecycle/save) or "saver".</summary>
    public string Through { get; set; } = "";

    public string At { get; set; } = "";

    public long WallMs { get; set; }

    /// <summary>The bundle directory, set on read and capture, not stored.</summary>
    public string Path { get; set; } = "";

    internal JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["case"] = Case,
            ["label"] = Label,
            ["offset_ms"] = OffsetMs,
            ["tick"] = Tick,
            ["save"] = Save,
            ["store"] = Store,
        };
        Put(o, "identity", Identity);
        Put(o, "prepared", Prepared);
        Put(o, "serve", Serve);
        o["source_revision"] = SourceRevision;
        o["package"] = Package;
        o["start"] = Start;
        o["expansions"] = new JsonArray([.. Expansions.Select(e => (JsonNode?)JsonValue.Create(e))]);
        o["through"] = Through;
        o["at"] = At;
        o["wall_ms"] = WallMs;
        return o;
    }

    internal static Checkpoint FromJson(JsonObject o) => new()
    {
        Case = o["case"].AsString(),
        Label = o["label"].AsString(),
        OffsetMs = (long)Math.Max(o["offset_ms"].AsNumber(), 0),
        Tick = o["tick"].AsUInt64() ?? 0,
        Save = o["save"].AsString(),
        Store = o["store"].AsBool() ?? false,
        Identity = o["identity"].AsMap()?.DeepCloneObject(),
        Prepared = o["prepared"].AsMap()?.DeepCloneObject(),
        Serve = o["serve"].AsMap()?.DeepCloneObject(),
        SourceRevision = o["source_revision"].AsString(),
        Package = o["package"].AsString(),
        Start = o["start"].AsString(),
        Expansions = [.. o["expansions"].AsSlice().Select(Dyn.AsString)],
        Through = o["through"].AsString(),
        At = o["at"].AsString(),
        WallMs = (long)Math.Max(o["wall_ms"].AsNumber(), 0),
    };

    private static void Put(JsonObject target, string key, JsonObject? value)
    {
        if (value is { Count: > 0 })
        {
            target[key] = value.DeepClone();
        }
    }
}

internal static class JsonObjectExtensions
{
    public static JsonObject DeepCloneObject(this JsonObject value) => (JsonObject)value.DeepClone();
}

/// <summary>What a bundle was taken under: the installed native package, the Start and the profile's expansions.</summary>
public sealed record Fingerprint(string Package, string Start, IReadOnlyList<string> Expansions);

/// <summary>
/// The index a checkpoint directory keeps (<see cref="Checkpoints.CheckpointRingFile"/>): the bundles present, the
/// window's outcome and its newest entry.
/// </summary>
public sealed class Ring
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public string Case { get; set; } = "";

    /// <summary>The bundles by label, oldest first by offset.</summary>
    public IList<Checkpoint> Entries { get; } = [];

    /// <summary>The failed window's final bundle, when one was taken.</summary>
    public Checkpoint? Failed { get; set; }

    /// <summary>Where the window failed; <see cref="FailedTick"/> is 0 when the failure could not be observed.</summary>
    public ulong FailedTick { get; set; }

    public long FailedOffsetMs { get; set; }

    /// <summary>The newest entry's label; "" when the ring is empty.</summary>
    public string Next { get; set; } = "";

    /// <summary>The window's.</summary>
    public string SourceRevision { get; set; } = "";

    /// <summary>The failed window's output directory.</summary>
    public string FailedOutput { get; set; } = "";

    /// <summary>Reads dir's index; a missing ring is null.</summary>
    public static Ring? Read(string dir)
    {
        var file = System.IO.Path.Combine(dir, Checkpoints.CheckpointRingFile);
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        JsonObject o;
        try
        {
            o = Dyn.ParseObject(text);
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"{Checkpoints.CheckpointRingFile}: {e.Message}", e);
        }

        var r = new Ring
        {
            Case = o["case"].AsString(),
            FailedTick = o["failed_tick"].AsUInt64() ?? 0,
            FailedOffsetMs = (long)Math.Max(o["failed_offset_ms"].AsNumber(), 0),
            Next = o["next"].AsString(),
            SourceRevision = o["source_revision"].AsString(),
            FailedOutput = o["failed_output"].AsString(),
        };
        foreach (var entry in o["entries"].AsSlice())
        {
            if (entry.AsMap() is { } map)
            {
                var c = Checkpoint.FromJson(map);
                c.Path = System.IO.Path.Combine(dir, c.Label);
                r.Entries.Add(c);
            }
        }

        if (o["failed"].AsMap() is { } failed)
        {
            r.Failed = Checkpoint.FromJson(failed);
            r.Failed.Path = System.IO.Path.Combine(dir, r.Failed.Label);
        }

        return r;
    }

    /// <summary>Persists the index under dir.</summary>
    public void Write(string dir)
    {
        Directory.CreateDirectory(dir);
        var o = new JsonObject
        {
            ["case"] = Case,
            ["entries"] = new JsonArray([.. Entries.Select(e => (JsonNode?)e.ToJson())]),
        };
        if (Failed is not null)
        {
            o["failed"] = Failed.ToJson();
        }

        o["failed_tick"] = FailedTick;
        o["failed_offset_ms"] = FailedOffsetMs;
        o["next"] = Next;
        o["source_revision"] = SourceRevision;
        if (FailedOutput.Length > 0)
        {
            o["failed_output"] = FailedOutput;
        }

        File.WriteAllText(System.IO.Path.Combine(dir, Checkpoints.CheckpointRingFile), o.ToJsonString(Indented));
    }

    /// <summary>The entry labelled label, null when absent.</summary>
    public Checkpoint? Entry(string label) => Entries.FirstOrDefault(e => string.Equals(e.Label, label, StringComparison.Ordinal));
}
