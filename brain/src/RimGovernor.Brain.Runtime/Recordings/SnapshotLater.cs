using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Policy;
using Cell = RimGovernor.Brain.Domain.Cell;
using System.Threading.Channels;

namespace RimGovernor.Brain.Runtime.Recordings;

public static class SnapshotLaterReplay
{
    // Recording is evidence for replay, never part of a decision, and encoding a 250x250 planning window costs
    // seconds: Later runs a recording job on one background consumer, in submission order, so the facts store's lock,
    // the player gate and the planner steps never wait on it. Jobs keep what they were handed (a mirror table's rows are
    // never mutated after publishing; a colony reading is read-only once taken), so a job reads it later.
    private static readonly object LaterLock = new();
    private static Channel<(Action Job, string Target, Tick Tick)>? _jobs;
    private static Task _drain = Task.CompletedTask;

    /// <summary>Queues job behind the recordings already queued.</summary>
    public static void Later(Action job, string target, Tick tick)
    {
        lock (LaterLock)
        {
            if (_jobs is null)
            {
                _jobs = Channel.CreateUnbounded<(Action Job, string Target, Tick Tick)>(new UnboundedChannelOptions { SingleReader = true });
                var reader = _jobs.Reader;
                _drain = Task.Run(async () =>
                {
                    await foreach (var j in reader.ReadAllAsync().ConfigureAwait(false))
                    {
                        try
                        {
                            j.Job();
                        }
                        catch (Exception ex)
                        {
                            Tel.Decide(SnapshotSkip.SnapshotSkipDecision("recording", j.Target, ex, j.Tick));
                        }
                    }
                });
            }
            _jobs.Writer.TryWrite((job, target, tick));
        }
    }

    /// <summary>Returns once every recording queued before it is written: a serve calls it on its way out, a test before it reads the stream.</summary>
    public static void Flush()
    {
        Task drain;
        lock (LaterLock)
        {
            if (_jobs is null)
            {
                return;
            }
            // Complete the current consumer after the queued jobs; the next Later starts a new one.
            _jobs.Writer.TryComplete();
            _jobs = null;
            drain = _drain;
        }
        drain.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Records every table a mirror publishes into the serve's stream in dir. A failed write drops the section, so
    /// its next table is a keyframe and no line refers to it meanwhile; the sink owns that bookkeeping.
    /// </summary>
    public static Recorder MirrorRecorder(string dir) =>
        published => SnapshotRecorder.Write(dir, "section", published.Section, published);
}
