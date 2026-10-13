using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One bundle's sidecar: what it holds and what it was taken under. Fingerprint fields (Package, Start,
/// Expansions) decide whether a later run may resume from it.
/// </summary>
public sealed class Checkpoint
{
    public string Case { get; set; } = "";

    /// <summary>Names the bundle's directory: "t+7m" for the ring, "failed", or a phase name a case declared.</summary>
    public string Label { get; set; } = "";

    /// <summary>The run-phase offset the bundle was taken at, counted from the case's original start across resumes (the label's t+...).</summary>
    public long OffsetMs { get; set; }

    public ulong Tick { get; set; }

    /// <summary>The .rws name.</summary>
    public string Save { get; set; } = "";

    /// <summary>Whether the bundle carries the service store.</summary>
    public bool Store { get; set; }

    public JsonObject? Identity { get; set; }

    /// <summary>
    /// The fixture op's reply the resumed session reports as its own, since the op does not run again over the
    /// restored world.
    /// </summary>
    public JsonObject? Prepared { get; set; }

    /// <summary>
    /// What the case itself recorded about its progress up to the capture (<see cref="Checkpoints.SetState"/>): the
    /// fixture prep it ran in its Run body, the coordinates it chose. A resumed run reads it back through
    /// Session.Resumed and skips the work the save already carries.
    /// </summary>
    public JsonObject? State { get; set; }

    /// <summary>The case's declared serve spec at capture, for the record.</summary>
    public JsonObject? Serve { get; set; }

    /// <summary>
    /// The service profile directory the store is bound to (its clock inbox binds to the path); a resumed run's
    /// services keep using it, since the store refuses any other.
    /// </summary>
    public string ServiceProfile { get; set; } = "";

    public string SourceRevision { get; set; } = "";

    /// <summary>Hashes the installed native package's files (<see cref="Checkpoints.PackageHash"/>).</summary>
    public string Package { get; set; } = "";

    /// <summary>Hashes the case's Start (its kind, save, fixture op and args).</summary>
    public string Start { get; set; } = "";

    public IReadOnlyList<string> Expansions { get; set; } = [];

    /// <summary>
    /// Mark a staged run-phase bundle: the stage the case declared and the hash of the staging code it was taken
    /// under (a change to the case's area package invalidates it).
    /// </summary>
    public string Stage { get; set; } = "";

    public string StageKey { get; set; } = "";

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
        Put(o, "state", State);
        Put(o, "serve", Serve);
        if (ServiceProfile.Length > 0)
        {
            o["service_profile"] = ServiceProfile;
        }

        o["source_revision"] = SourceRevision;
        o["package"] = Package;
        o["start"] = Start;
        o["expansions"] = new JsonArray([.. Expansions.Select(e => (JsonNode?)JsonValue.Create(e))]);
        if (Stage.Length > 0)
        {
            o["stage"] = Stage;
        }

        if (StageKey.Length > 0)
        {
            o["stage_key"] = StageKey;
        }

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
        State = o["state"].AsMap()?.DeepCloneObject(),
        Serve = o["serve"].AsMap()?.DeepCloneObject(),
        ServiceProfile = o["service_profile"].AsString(),
        SourceRevision = o["source_revision"].AsString(),
        Package = o["package"].AsString(),
        Start = o["start"].AsString(),
        Expansions = [.. o["expansions"].AsSlice().Select(Dyn.AsString)],
        Stage = o["stage"].AsString(),
        StageKey = o["stage_key"].AsString(),
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

/// <summary>
/// What a checkpoint must agree with the current tree on before a run resumes from it: anything that touches world
/// state. A governor-only change keeps the bundle.
/// </summary>
public sealed record Fingerprint(string Package, string Start, IReadOnlyList<string> Expansions)
{
    /// <summary>Says why the sidecar cannot be resumed under this fingerprint, or "".</summary>
    public string Mismatch(Checkpoint c)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (!string.Equals(c.Package, Package, StringComparison.Ordinal))
        {
            return "the installed native package changed";
        }

        if (!string.Equals(c.Start, Start, StringComparison.Ordinal))
        {
            return "the case's Start changed";
        }

        if (!string.Equals(string.Join(',', c.Expansions), string.Join(',', Expansions), StringComparison.Ordinal))
        {
            return "the profile's expansions changed";
        }

        return "";
    }
}

/// <summary>
/// The index a case's checkpoint directory keeps (<see cref="Checkpoints.CheckpointRingFile"/>): the bundles
/// present, the last run's outcome and which entry the next run resumes from.
/// </summary>
public sealed class Ring
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public string Case { get; set; } = "";

    /// <summary>The bundles by label, oldest first by offset.</summary>
    public IList<Checkpoint> Entries { get; } = [];

    /// <summary>The last failed run's final bundle, when one was taken.</summary>
    public Checkpoint? Failed { get; set; }

    /// <summary>Where the last run failed; <see cref="FailedTick"/> is 0 when the failure could not be observed.</summary>
    public ulong FailedTick { get; set; }

    public long FailedOffsetMs { get; set; }

    /// <summary>The label the failed run itself resumed from, "" for a run from scratch.</summary>
    public string ResumedFrom { get; set; } = "";

    /// <summary>The label the next run resumes from; "" means fresh.</summary>
    public string Next { get; set; } = "";

    /// <summary>Printed by the next run before its resume line: the automatic rewind's reason.</summary>
    public string Note { get; set; } = "";

    /// <summary>The failed run's.</summary>
    public string SourceRevision { get; set; } = "";

    /// <summary>The failed run's output directory (its result.json holds the timeline a postmortem-only rerun reads back).</summary>
    public string FailedOutput { get; set; } = "";

    /// <summary>
    /// Set when the last run paused at a breakpoint: Next then names its BreakCheckpoint bundle, which
    /// `acceptance resume` continues from and `acceptance stop` discards.
    /// </summary>
    public BreakRecord? Break { get; set; }

    /// <summary>Reads the sidecar of the bundle at dir, with Path set.</summary>
    public static Checkpoint ReadCheckpoint(string dir)
    {
        var file = System.IO.Path.Combine(dir, Checkpoints.CheckpointSidecar);
        var text = File.ReadAllText(file);
        try
        {
            var c = Checkpoint.FromJson(Dyn.ParseObject(text));
            c.Path = dir;
            return c;
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"{file}: {e.Message}", e);
        }
    }

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
            ResumedFrom = o["resumed_from"].AsString(),
            Next = o["next"].AsString(),
            Note = o["note"].AsString(),
            SourceRevision = o["source_revision"].AsString(),
            FailedOutput = o["failed_output"].AsString(),
            Break = o["break"].AsMap() is { } brk ? BreakRecord.FromJson(brk) : null,
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
        if (ResumedFrom.Length > 0)
        {
            o["resumed_from"] = ResumedFrom;
        }

        o["next"] = Next;
        if (Note.Length > 0)
        {
            o["note"] = Note;
        }

        o["source_revision"] = SourceRevision;
        if (FailedOutput.Length > 0)
        {
            o["failed_output"] = FailedOutput;
        }

        if (Break is not null)
        {
            o["break"] = Break.ToJson();
        }

        File.WriteAllText(System.IO.Path.Combine(dir, Checkpoints.CheckpointRingFile), o.ToJsonString(Indented));
    }

    /// <summary>The entry labelled label, null when absent.</summary>
    public Checkpoint? Entry(string label) => Entries.FirstOrDefault(e => string.Equals(e.Label, label, StringComparison.Ordinal));

    /// <summary>The entry preceding label, null at the ring's start (or when label is absent).</summary>
    public Checkpoint? Before(string label)
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            if (string.Equals(Entries[i].Label, label, StringComparison.Ordinal))
            {
                return i == 0 ? null : Entries[i - 1];
            }
        }

        return null;
    }

    /// <summary>Steps Next back n entries; past the ring's start the result is "" (fresh).</summary>
    public string Rewind(int n)
    {
        var label = Next;
        for (; n > 0 && label.Length > 0; n--)
        {
            if (Before(label) is not { } prev)
            {
                return "";
            }

            label = prev.Label;
        }

        return label;
    }

    /// <summary>
    /// Decides what the run that just ended leaves for the next one: a passing run clears the ring; a failing run
    /// resumes from its last entry, unless it was itself a resumed run that failed at the tick the previous attempt
    /// failed at, in which case the next run steps back one entry ("no progress") and past the ring starts fresh.
    /// <paramref name="previous"/> is the index before this run (null when there was none).
    /// </summary>
    public void Plan(Ring? previous, string resumedFrom, ulong failedTick)
    {
        ResumedFrom = resumedFrom;
        Note = "";
        if (Entries.Count == 0)
        {
            Next = "";
            return;
        }

        Next = Entries[^1].Label;
        if (previous is null || resumedFrom.Length == 0 || failedTick == 0 || previous.FailedTick != failedTick)
        {
            return;
        }

        if (Before(resumedFrom) is not { } prev)
        {
            Next = "";
            Note = $"no progress since {resumedFrom} and nothing earlier in the ring; starting fresh";
            return;
        }

        Next = prev.Label;
        Note = $"no progress since {resumedFrom}; rewinding to {prev.Label}";
    }
}
