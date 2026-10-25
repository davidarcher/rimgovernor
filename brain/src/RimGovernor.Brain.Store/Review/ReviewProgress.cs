using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    /// <summary>
    /// Carries the Need rows (the family runs' records, RecordPlannerReasons) into this review as they stand, with a
    /// rewound clock clamped to this review's tick; a world change or rewind drops them.
    /// </summary>
    internal static List<ConcernProgress> ReviewProgress(ReviewRequest request, ReviewState previous, bool reset)
    {
        var output = new List<ConcernProgress>();
        if (reset)
        {
            return output;
        }
        foreach (var p in previous.Progress)
        {
            if (NeedKey(p.Concern) && output.Count < MaxReviewProgress)
            {
                output.Add(p.LastProgress > request.Tick ? p with { LastProgress = request.Tick } : p);
            }
        }
        return output;
    }

    /// <summary>Whether a progress key is a NeedId's filing key (a family's row) rather than a Concern id.</summary>
    private static bool NeedKey(FilingKey key) => new NeedId(key.Value).IsValid;

    /// <summary>
    /// A reported Need's progress row: the Method it works (by label) or, with none, the filed note as its blocked
    /// reason. The clock restarts on a new row, a changed Method or a rewound tick. Need rows carry no deadline or
    /// deficit fraction: the family owns rotation (its refusal budget) and its deficit unit.
    /// </summary>
    private static ConcernProgress NeedProgressRow(ConcernProgress? previous, NeedProgress need, Tick tick)
    {
        var method = need.Method.IsEmpty ? "" : MethodLabel(need.Method);
        var fresh = previous is null || previous.Method != method || previous.LastProgress > tick;
        var row = new ConcernProgress
        {
            Concern = need.Need.FilingKey,
            Method = method,
            Expected = method.Length == 0 ? "" : "the need's predicate is met",
            LastProgress = fresh ? tick : previous!.LastProgress,
            Blocked = method.Length == 0 ? need.Note.Blocked() : default,
            Planner = need.Note.Cause,
            PlannerSubject = need.Note.Subject,
            Open = method.Length != 0,
            // The Need's planners key stalled targets out on its row (RecordProgressCooldown); a cooldown outlives
            // the run that rebuilt the row until its own bound.
            Cooldowns = previous is null ? default : EquatableArray.From(previous.Cooldowns.Where(c => c.Until > tick)),
        };
        try
        {
            ConcernProgressRules.ValidateConcernProgress(row, tick);
        }
        catch (DomainException e)
        {
            throw new DomainException($"{need.Need.Value}: {e.Message}");
        }
        return row;
    }

    /// <summary>
    /// A method id without its content hash or admission salt ("acquire-3f9a..." reads "acquire"), so the record
    /// names the method family rather than one plan.
    /// </summary>
    internal static string MethodLabel(MethodId id)
    {
        var s = id.Value;
        var i = s.LastIndexOf('-');
        if (i > 0)
        {
            var suffix = s[(i + 1)..];
            if (suffix.Length >= 8 && suffix.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                s = s[..i];
            }
        }
        return s.Length > 64 ? s[..64] : s;
    }

    /// <summary>
    /// Keys a failed situation out of <paramref name="need"/>'s goal for a bounded cooldown
    /// (<c>ProgressCooldownMax</c>): a planner that cancelled a stalled order calls it with the target it gave up
    /// on so the next selection passes that target over until the cooldown lifts. A review that has moved past
    /// <paramref name="revision"/> is <see cref="ConflictException"/>; a need without a record is a no-op.
    /// </summary>
    public ReviewState RecordProgressCooldown(ulong revision, FilingKey need, string key, Tick until, CancellationToken cancellationToken) =>
        Run(
            tx =>
            {
                var review = LoadReview(tx);
                if (review.Revision != revision)
                {
                    throw new ConflictException($"rounds revision {review.Revision}, cooldown under {revision}");
                }
                var changed = false;
                var progress = new List<ConcernProgress>(review.Progress.Count);
                foreach (var p in review.Progress)
                {
                    if (p.Concern != need)
                    {
                        progress.Add(p);
                        continue;
                    }
                    var updated = ConcernProgressRules.AddProgressCooldown(p, key, until, review.Tick);
                    ConcernProgressRules.ValidateConcernProgress(updated, review.Tick);
                    progress.Add(updated);
                    changed = true;
                }
                if (!changed)
                {
                    return review;
                }
                review = review with { Progress = EquatableArray.From(progress) };
                PutSingleton(tx, "rounds", review, MaxReviewPayload);
                return review;
            },
            cancellationToken
        );

    public Task<ReviewState> RecordProgressCooldownAsync(ulong revision, FilingKey need, string key, Tick until, CancellationToken cancellationToken) =>
        Task.Run(() => RecordProgressCooldown(revision, need, key, until, cancellationToken), cancellationToken);

    /// <summary>
    /// Files each goal's latest planner refusal on its progress record (<c>ConcernProgress.Planner</c>); the zero
    /// note clears it (the planner admitted or found work). A record with no method, or one already naming a
    /// planner refusal or wait, is relabelled at once so the strip names the reason before the next review; held
    /// records keep their hold. Standards without a record are skipped.
    /// <para>
    /// Need rows (#3231 decision 18) come from <paramref name="families"/>, not <paramref name="reasons"/>: each
    /// reported Need that is not met gets a row keyed by its <see cref="NeedId.FilingKey"/> (inserted, or updated
    /// in place), a met Need's row is pruned, and a complete run prunes its family's rows for Needs it no longer
    /// reported. Rows of families that did not run, or ran partially (the immediate pass), stand.
    /// </para>
    /// Reports whether anything changed and each filed goal's and reported Need's current method: a reported Need's
    /// full <see cref="MethodId"/> (empty when it works none), a filed goal's record Method.
    /// </summary>
    public (bool Changed, IReadOnlyDictionary<FilingKey, string> Methods) RecordPlannerReasons(
        IReadOnlyDictionary<FilingKey, PlannerNote> reasons,
        IReadOnlyList<FamilyProgress> families,
        CancellationToken cancellationToken
    )
    {
        if (reasons.Count == 0 && families.Count == 0)
        {
            return (false, new Dictionary<FilingKey, string>());
        }
        var reported = new Dictionary<FilingKey, NeedProgress>();
        var complete = new HashSet<string>(StringComparer.Ordinal);
        foreach (var family in families)
        {
            if (family.Complete)
            {
                complete.Add(family.Family);
            }
            foreach (var need in family.Needs)
            {
                need.Need.Validate();
                DomainException.Require(need.Need.Family == family.Family, $"need {need.Need} reported by family {family.Family}");
                reported[need.Need.FilingKey] = need;
            }
        }
        return Run<(bool Changed, IReadOnlyDictionary<FilingKey, string> Methods)>(
            tx =>
            {
                var review = LoadReview(tx);
                var changed = false;
                // A reported Need names the full MethodId it works, the one need_methods binds (#3593): its row keeps
                // only the label. Met, refused and unmethoded Needs work none.
                var methods = reported.ToDictionary(kv => kv.Key, kv => kv.Value.Method.Value);
                var progress = new List<ConcernProgress>(review.Progress.Count + reported.Count);
                var pending = new Dictionary<FilingKey, NeedProgress>(reported);
                foreach (var record in review.Progress)
                {
                    var p = record;
                    if (NeedKey(p.Concern))
                    {
                        if (pending.Remove(p.Concern, out var need))
                        {
                            if (need.Met)
                            {
                                changed = true;
                                continue;
                            }
                            p = NeedProgressRow(record, need, review.Tick);
                            changed = changed || p != record;
                        }
                        else if (complete.Contains(new NeedId(p.Concern.Value).Family))
                        {
                            changed = true;
                            continue;
                        }
                        progress.Add(p);
                        continue;
                    }
                    if (!reasons.TryGetValue(p.Concern, out var note))
                    {
                        progress.Add(p);
                        continue;
                    }
                    methods[p.Concern] = p.Method;
                    if (p.PlannerNote() != note)
                    {
                        var unmethoded = p.Unmethoded();
                        p = p with { Planner = note.Cause, PlannerSubject = note.Subject };
                        if (unmethoded)
                        {
                            p = p with { Blocked = note.Blocked() };
                        }
                        try
                        {
                            ConcernProgressRules.ValidateConcernProgress(p, review.Tick);
                        }
                        catch (DomainException e)
                        {
                            throw new DomainException($"{p.Concern.Value}: {e.Message}");
                        }
                        changed = true;
                    }
                    progress.Add(p);
                }
                foreach (var need in pending.Values.OrderBy(n => n.Need))
                {
                    if (need.Met || progress.Count >= MaxReviewProgress)
                    {
                        continue;
                    }
                    var row = NeedProgressRow(null, need, review.Tick);
                    progress.Add(row);
                    changed = true;
                }
                if (!changed)
                {
                    return (false, methods);
                }
                PutSingleton(tx, "rounds", review with { Progress = EquatableArray.From(progress) }, MaxReviewPayload);
                return (true, methods);
            },
            cancellationToken
        );
    }

    public Task<(bool Changed, IReadOnlyDictionary<FilingKey, string> Methods)> RecordPlannerReasonsAsync(
        IReadOnlyDictionary<FilingKey, PlannerNote> reasons,
        IReadOnlyList<FamilyProgress> families,
        CancellationToken cancellationToken
    ) => Task.Run(() => RecordPlannerReasons(reasons, families, cancellationToken), cancellationToken);
}

/// <summary>
/// One family run's Need progress (#3231 decision 18): the family, whether the run derived every Need of the family
/// (then a Need it did not report is gone and its row is pruned), and each reported Need.
/// </summary>
public sealed record FamilyProgress(string Family, bool Complete, IReadOnlyList<NeedProgress> Needs);

/// <summary>Where one reported Need stands on its progress row: met (no row), the note its verdict files, and the Method it works (empty when none).</summary>
public readonly record struct NeedProgress(NeedId Need, bool Met, PlannerNote Note, MethodId Method);
