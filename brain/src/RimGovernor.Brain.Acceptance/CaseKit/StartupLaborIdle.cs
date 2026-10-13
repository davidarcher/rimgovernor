using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// What one sampled pawn was doing, derived from the job the sample carries. Only the non-work kinds are enumerated;
/// any other named job is work, and a sample without a readable job is <see cref="Unknown"/> (Go
/// <c>startuplabor.Activity</c>).
/// </summary>
public static class LaborActivity
{
    public const string Work = "work";
    public const string Idle = "idle";
    public const string Sleep = "sleep";
    public const string Meal = "meal";
    public const string Recreation = "recreation";
    public const string Medical = "medical_rest";
    public const string Unavailable = "unavailable";
    public const string Unknown = "unknown";
}

/// <summary>
/// One bounded observation of one pawn, as an observations_list_pawns row reports it. <see cref="JobKnown"/> false
/// (the row carried no job field at all) is the only unknown: an absent job on a present row is a pawn with nothing to
/// do.
/// </summary>
public sealed record PawnSample
{
    // The jobs that mean the pawn has nothing to do: RimWorld's own wait and wander drivers.
    private static readonly HashSet<string> IdleJobs = new(StringComparer.Ordinal)
    {
        "Wait", "Wait_Wander", "Wait_MaintainPosture", "GotoWander", "Wait_WithSleeping",
    };

    // A job mapped to the need it is serving. A pawn on one of these is unavailable for work and its interval is
    // excluded from idle time, never counted as starvation.
    private static readonly Dictionary<string, string> NonWorkJobs = new(StringComparer.Ordinal)
    {
        ["LayDown"] = LaborActivity.Sleep,
        ["LayDownAwake"] = LaborActivity.Sleep,
        ["Ingest"] = LaborActivity.Meal,
        ["SocialRelax"] = LaborActivity.Recreation,
        ["ViewArt"] = LaborActivity.Recreation,
        ["Skygaze"] = LaborActivity.Recreation,
        ["StandAndBeSociallyActive"] = LaborActivity.Recreation,
        ["Meditate"] = LaborActivity.Recreation,
        ["VisitSickPawn"] = LaborActivity.Recreation,
        ["Pray"] = LaborActivity.Recreation,
    };

    public string Pawn { get; init; } = "";

    public long Tick { get; init; }

    public string Job { get; init; } = "";

    public bool JobKnown { get; init; }

    public bool Drafted { get; init; }

    public bool Downed { get; init; }

    public string Mental { get; init; } = "";

    public bool InBed { get; init; }

    /// <summary>Marks the first sample after the service restarted: the interval that ends here spans a boundary and is not credited.</summary>
    public bool Restart { get; init; }

    /// <summary>
    /// The sample's activity. Drafted, downed and mental-state pawns are unavailable whatever job they carry; a downed
    /// pawn in bed is medical rest.
    /// </summary>
    public string Classify()
    {
        if (!JobKnown)
        {
            return LaborActivity.Unknown;
        }

        if (Downed)
        {
            return InBed ? LaborActivity.Medical : LaborActivity.Unavailable;
        }

        if (Drafted || Mental.Length > 0)
        {
            return LaborActivity.Unavailable;
        }

        var job = Job.Trim();
        if (job.Length == 0 || IdleJobs.Contains(job))
        {
            return LaborActivity.Idle;
        }

        // A downed sleeper in bed is already medical rest above, so the sleep jobs map straight through.
        return NonWorkJobs.GetValueOrDefault(job, LaborActivity.Work);
    }
}

/// <summary>An observation gap longer than the bound: the interval, what was credited and what was left unattributed.</summary>
public sealed record IdleGap(string Pawn, long From, long To, string Activity, long Credited, long Unbounded);

/// <summary>
/// Derived idle accounting over a run of samples. Every tick of every credited interval lands in exactly one bucket,
/// so Idle+Work+Excluded+Unknown plus Unattributed equals the observed span the samples actually cover (Go
/// <c>startuplabor.Account</c>).
/// </summary>
public sealed record IdleAccount
{
    /// <summary>Available-pawn time with nothing to do: the epic's subject.</summary>
    public long Idle { get; init; }

    /// <summary>Time on a job that is not a need.</summary>
    public long Work { get; init; }

    /// <summary>Sleep, meals, recreation, medical rest and drafted or otherwise unavailable time.</summary>
    public long Excluded { get; init; }

    /// <summary>Time whose sample carried no readable job.</summary>
    public long Unknown { get; init; }

    /// <summary>The part of over-long gaps no sample speaks for.</summary>
    public long Unattributed { get; init; }

    /// <summary>The credited time per activity.</summary>
    public IReadOnlyDictionary<string, long> ByActivity { get; init; } = new Dictionary<string, long>();

    /// <summary>The boundaries the accounting refused to credit; they make the derived totals auditable.</summary>
    public IReadOnlyList<IdleGap> Gaps { get; init; } = [];

    public int Rewinds { get; init; }

    public int Restarts { get; init; }

    /// <summary>The number of samples the account read.</summary>
    public int Samples { get; init; }

    /// <summary>The number of distinct pawns sampled.</summary>
    public int Pawns { get; init; }

    /// <summary>Renders the account for the report artifact.</summary>
    public JsonObject Row()
    {
        var by = new JsonObject();
        foreach (var (activity, ticks) in ByActivity)
        {
            by[activity] = ticks;
        }

        var row = new JsonObject
        {
            ["idle_ticks"] = Idle,
            ["work_ticks"] = Work,
            ["excluded_ticks"] = Excluded,
            ["unknown_ticks"] = Unknown,
            ["unattributed_ticks"] = Unattributed,
            ["by_activity"] = by,
            ["samples"] = Samples,
            ["pawns"] = Pawns,
            ["rewinds"] = Rewinds,
            ["restarts"] = Restarts,
        };
        if (Gaps.Count > 0)
        {
            row["bounded_gaps"] = new JsonArray([.. Gaps.Select(g => (JsonNode?)new JsonObject
            {
                ["pawn"] = g.Pawn,
                ["from"] = g.From,
                ["to"] = g.To,
                ["activity"] = g.Activity,
                ["credited"] = g.Credited,
                ["unbounded"] = g.Unbounded,
            })]);
        }

        return row;
    }
}

public static partial class StartupLabor
{
    /// <summary>How much of an unobserved stretch one sample may speak for: half a game hour. A longer gap credits this much and flags the remainder rather than assuming the sampled job persisted.</summary>
    public const long DefaultMaxGap = 1250;

    /// <summary>
    /// Derives an <see cref="IdleAccount"/> from samples. Samples may arrive in any order and interleaved across pawns;
    /// they are grouped per pawn and ordered by tick within the sequence given, so a tick that goes backwards is a
    /// rewind (a reload or a fresh world), not a sort error.
    /// <para>
    /// A sample credits its own activity to the interval up to the next sample of the same pawn. It credits nothing
    /// across a rewind, a restart boundary or the end of the run, nothing for a paused pair (equal ticks), and at most
    /// <paramref name="maxGap"/> for an over-long gap.
    /// </para>
    /// </summary>
    public static IdleAccount Accumulate(IReadOnlyList<PawnSample> samples, long maxGap)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (maxGap <= 0)
        {
            maxGap = DefaultMaxGap;
        }

        var byActivity = new Dictionary<string, long>(StringComparer.Ordinal);
        var gaps = new List<IdleGap>();
        long idle = 0, work = 0, excluded = 0, unknown = 0, unattributed = 0;
        var rewinds = 0;
        var restarts = 0;
        var byPawn = new Dictionary<string, List<PawnSample>>(StringComparer.Ordinal);
        foreach (var s in samples)
        {
            if (!byPawn.TryGetValue(s.Pawn, out var run))
            {
                run = [];
                byPawn[s.Pawn] = run;
            }

            run.Add(s);
        }

        foreach (var pawn in byPawn.Keys.Order(StringComparer.Ordinal))
        {
            var run = byPawn[pawn];
            for (var i = 0; i + 1 < run.Count; i++)
            {
                PawnSample from = run[i], to = run[i + 1];
                if (to.Restart)
                {
                    restarts++;
                    continue;
                }

                if (to.Tick < from.Tick)
                {
                    rewinds++;
                    continue;
                }

                var span = to.Tick - from.Tick;
                if (span == 0)
                {
                    continue;
                }

                var activity = from.Classify();
                var credited = span;
                if (span > maxGap)
                {
                    credited = maxGap;
                    unattributed += span - maxGap;
                    gaps.Add(new IdleGap(pawn, from.Tick, to.Tick, activity, credited, span - maxGap));
                }

                byActivity[activity] = byActivity.GetValueOrDefault(activity) + credited;
                switch (activity)
                {
                    case LaborActivity.Idle:
                        idle += credited;
                        break;
                    case LaborActivity.Work:
                        work += credited;
                        break;
                    case LaborActivity.Unknown:
                        unknown += credited;
                        break;
                    default:
                        excluded += credited;
                        break;
                }
            }
        }

        return new IdleAccount
        {
            Idle = idle,
            Work = work,
            Excluded = excluded,
            Unknown = unknown,
            Unattributed = unattributed,
            ByActivity = byActivity,
            Gaps = gaps,
            Rewinds = rewinds,
            Restarts = restarts,
            Samples = samples.Count,
            Pawns = byPawn.Count,
        };
    }
}
