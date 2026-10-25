using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Policy;
using Cell = RimGovernor.Brain.Domain.Cell;
namespace RimGovernor.Brain.Runtime.Recordings;

// Planning recordings retain the exact inputs used by a review or planner decision.
// The optional host sink owns encoding and storage; recordings never affect policy.

/// <summary>Where recorded evidence goes. The host implements it with a source-generated codec; none is set by default.</summary>
public interface ISnapshotSink
{
    /// <summary>Appends or writes <paramref name="value"/> into <paramref name="directory"/> as recording <paramref name="kind"/> named <paramref name="name"/>.</summary>
    void Record(string directory, string kind, string name, object value);
}

public static class SnapshotRecorder
{
    /// <summary>
    /// Names the directory a serve records every enabled routine review's snapshot into, one stream per serve; unset
    /// records nothing.
    /// </summary>
    public const string DirEnv = "RIMGOVERNOR_SNAPSHOT_DIR";

    private static ISnapshotSink? _sink;

    /// <summary>The host's recording sink; null records nothing.</summary>
    public static ISnapshotSink? Sink
    {
        get => Volatile.Read(ref _sink);
        set => Volatile.Write(ref _sink, value);
    }

    internal static void Write(string dir, string kind, string name, object value) => Sink?.Record(dir, kind, name, value);

    /// <summary>Writes a rounds review (<see cref="SnapshotReview"/>) into dir.</summary>
    public static void Record(string dir, SnapshotReview rounds) => Write(dir, "routine", $"routine-{rounds.Tick.Value}", rounds);

    /// <summary>The generation snapshot's provenance line every recording carries.</summary>
    internal static string Provenance(GenerationSnapshot current, Tick tick) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"colony {current.Colony} load {current.Load} map {current.Map} tick {tick.Value}");
}
