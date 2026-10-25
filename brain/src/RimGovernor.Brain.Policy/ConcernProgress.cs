namespace RimGovernor.Brain.Policy;

/// <summary>
/// The one progress contract every active Standard reports: the method it is working, the native observable
/// that proves the method is advancing, the last tick that observable moved, the tick by which it must move
/// again and why it is not moving now. Progress is measured from native outcomes (a settled effect, a deficit
/// that shrank, construction observed), never from dispatch: an order nobody can take is blocked, not progressing.
/// </summary>
public sealed record ConcernProgress
{
    public FilingKey Concern { get; init; }

    /// <summary>The goal's current method or ladder rung ("hunt", "cook").</summary>
    public string Method { get; init; } = "";

    /// <summary>The observable the method is expected to move.</summary>
    public string Expected { get; init; } = "";

    /// <summary>The last review tick native evidence advanced Expected.</summary>
    public Tick LastProgress { get; init; }

    /// <summary>The tick the method is judged stalled unless Expected advances first; zero when the contract sets no deadline.</summary>
    public Tick NextReview { get; init; }

    /// <summary>Why Expected is not advancing; empty while the method is unblocked.</summary>
    public BlockedReason Blocked { get; init; }

    /// <summary>The failed situations a rotation keyed, each bounded by its Until tick; none is a permanent ban.</summary>
    public EquatableArray<ProgressCooldown> Cooldowns { get; init; }

    /// <summary>
    /// The goal's planner's latest refusal or wait, empty while it last admitted, found work or has not run.
    /// A record with no method reads it as its Blocked, so "no_method" only means no planner has said why.
    /// </summary>
    public Cause Planner { get; init; }

    /// <summary>The thing Planner concerns, bounded by MaxSubjectLen; empty when Planner is.</summary>
    public string PlannerSubject { get; init; } = "";

    /// <summary>The goal has a method with unsettled work in flight, the evidence a prerequisite is being worked rather than merely owed.</summary>
    public bool Open { get; init; }

    /// <summary>The record's filed planner note.</summary>
    public PlannerNote PlannerNote() => new(Planner, PlannerSubject);

    /// <summary>Reports a record whose blocked reason only the planner's note can improve: no method, or the filed note's own reason.</summary>
    public bool Unmethoded() => Blocked == BlockedReason.NoMethod || (Planner != Cause.None && Blocked == BlockedReason.FromCause(Planner));

    /// <summary>The subject of a blocked reason that is the filed planner note; empty for any other reason.</summary>
    public string BlockedSubject() =>
        Planner != Cause.None && Blocked == BlockedReason.FromCause(Planner) ? PlannerSubject : "";

    /// <summary>Reports whether the situation key is under a cooldown at <paramref name="now"/>. A changed condition is a different key and lifts the cooldown by itself.</summary>
    public bool Cooled(string key, Tick now) => Cooldowns.Any(cd => cd.Key == key && cd.Until > now);
}

/// <summary>
/// Says why a goal's expected observable is not advancing: a Cause, or a prerequisite naming the goal it
/// waits on after a colon.
/// </summary>
public readonly record struct BlockedReason(string Value)
{
    private const string PrerequisitePrefix = "prerequisite:";
    private const string HeldPrefix = "held:";

    /// <summary>The method's work is issued but no available pawn is capable of it, so the designation can never be taken.</summary>
    public static readonly BlockedReason NoWorker = FromCause(Cause.NoWorker);

    /// <summary>Native refuses the order (no storage accepts the thing, no route reaches it).</summary>
    public static readonly BlockedReason NativeIneligible = FromCause(Cause.NativeIneligible);

    /// <summary>A write's outcome is unknown; the action identity is reconciled before anything is retried.</summary>
    public static readonly BlockedReason Reconciling = FromCause(Cause.ReconcileWrite);

    /// <summary>Every alternative method is under a cooldown.</summary>
    public static readonly BlockedReason Cooldown = FromCause(Cause.Cooldown);

    /// <summary>The goal has no method open and its planner proposed none this review.</summary>
    public static readonly BlockedReason NoMethod = FromCause(Cause.NoMethod);

    // Held reasons: the goal is intentionally not worked (HoldProgress).
    public static readonly BlockedReason HeldUnavailable = FromCause(Cause.HeldUnavailable);
    public static readonly BlockedReason HeldOptIn = FromCause(Cause.HeldOptIn);
    public static readonly BlockedReason HeldNoSource = FromCause(Cause.HeldNoSource);

    public string Text => Value ?? "";

    public bool IsEmpty => Text.Length == 0;

    public static BlockedReason FromCause(Cause cause) => new(cause.Value);

    /// <summary>Blocks on another goal that must be served first.</summary>
    public static BlockedReason OnPrerequisite(FilingKey goal) => new(PrerequisitePrefix + goal.Value);

    /// <summary>The Cause this reason spells, when it spells one.</summary>
    public bool TryGetCause(out Cause cause)
    {
        cause = new Cause(Text);
        return cause.IsValid();
    }

    /// <summary>Reports a planner wait.</summary>
    public bool Waiting() => TryGetCause(out var cause) && cause.Waiting();

    /// <summary>The goal a prerequisite reason waits on, else empty.</summary>
    public FilingKey Prerequisite() =>
        Text.StartsWith(PrerequisitePrefix, StringComparison.Ordinal) ? new FilingKey(Text[PrerequisitePrefix.Length..]) : default;

    /// <summary>Reports a goal intentionally not worked: a held: reason or a prerequisite another goal serves first.</summary>
    public bool Held() => Text.StartsWith(HeldPrefix, StringComparison.Ordinal) || !Prerequisite().IsEmpty;

    /// <summary>Reports a blocked goal someone should look at: blocked and not held and not merely waiting.</summary>
    public bool Actionable() => !IsEmpty && !Held() && !Waiting();

    internal bool IsValid()
    {
        if (IsEmpty)
        {
            return true;
        }
        if (!Prerequisite().IsEmpty)
        {
            return AdmissionRules.ValidResource(new Resource(Prerequisite().Value));
        }
        return TryGetCause(out var cause) && cause.IsValid();
    }

    public override string ToString() => Text;
}

/// <summary>
/// A planner's latest standing on a goal as it files on the goal's progress record: the zero note clears it
/// (the planner admitted or found no deficit), otherwise Cause is the refusal, wait or opt-out
/// (Cause.HeldOptIn) and Subject what it concerns.
/// </summary>
public readonly record struct PlannerNote(Cause Cause, string Subject)
{
    /// <summary>What the note concerns; "" for the zero note (a default struct has no string to hold).</summary>
    public string Subject { get => field ?? ""; init; } = Subject;

    /// <summary>Compares through <see cref="Subject"/> so the default struct equals the zero note built with "".</summary>
    public bool Equals(PlannerNote other) => Cause == other.Cause && Subject == other.Subject;

    public override int GetHashCode() => HashCode.Combine(Cause, Subject);

    /// <summary>The reason a record with no method reads the note as: nothing to do while cleared, else the note's cause.</summary>
    public BlockedReason Blocked() => Cause == Cause.None ? BlockedReason.NoMethod : BlockedReason.FromCause(Cause);
}

/// <summary>Keeps one failed situation (a prey, a haul target, a route, a season) off the table until Until.</summary>
public readonly record struct ProgressCooldown(string Key, Tick Until);

/// <summary>
/// What one method promises: the observable it moves, how long it may go without moving it (Deadline, zero for
/// no bound) and how long a situation it failed in stays keyed out (Cooldown, bounded by
/// ProgressCooldownMax; zero uses the deadline).
/// </summary>
public sealed record ProgressContract
{
    public string Method { get; init; } = "";

    public Tick Deadline { get; init; }

    public Tick Cooldown { get; init; }

    /// <summary>Reports a method whose observable last moved at <paramref name="since"/> and has exhausted its deadline at <paramref name="now"/>; a contract without a deadline never expires.</summary>
    public bool Expired(Tick since, Tick now) => Deadline.Value > 0 && now.Value - since.Value >= Deadline.Value;

    internal Tick EffectiveCooldown()
    {
        var d = Cooldown.Value;
        if (d <= 0)
        {
            d = Deadline.Value;
        }
        return new Tick(Math.Min(Math.Max(d, 0), ConcernProgressRules.ProgressCooldownMax.Value));
    }

    /// <summary>The retry deadline for a failed situation.</summary>
    public Tick CooldownUntil(Tick now) => new(now.Value + EffectiveCooldown().Value);
}

public static class ConcernProgressRules
{
    /// <summary>Bounds every cooldown to three game days: a failed situation is retried, never banned.</summary>
    public static readonly Tick ProgressCooldownMax = new(180000);

    /// <summary>
    /// The hunt stall policy as a contract: a hunt designation the census reports untaken for HuntStallTicks
    /// since native first saw it is withdrawn, so the planner tries other prey or a non-hunt source.
    /// </summary>
    public static ProgressContract HuntProgress(this PlannerSettings p) =>
        new() { Method = "hunt", Deadline = new Tick(p.HuntStallTicks) };

    /// <summary>
    /// The designation stall policy: a plant harvest the census reports designated and untaken for
    /// AcquisitionStallTicks since native first saw it is cancelled so the goal re-plans from another source.
    /// </summary>
    public static ProgressContract AcquisitionProgress(this PlannerSettings p) =>
        new() { Method = "harvest", Deadline = new Tick(p.AcquisitionStallTicks) };

    internal static List<ProgressCooldown> LiveCooldowns(IEnumerable<ProgressCooldown> cooldowns, Tick now) =>
        cooldowns.Where(cd => cd.Until > now).ToList();

    /// <summary>Joins the parts of a failed situation into one cooldown key.</summary>
    public static string CooldownKey(params string[] parts) => string.Join('/', parts);

    /// <summary>Keys a situation out until the given tick, bounded by ProgressCooldownMax from now; a key already cooled takes the later bound.</summary>
    public static ConcernProgress AddProgressCooldown(ConcernProgress p, string key, Tick until, Tick now)
    {
        until = new Tick(Math.Min(until.Value, now.Value + ProgressCooldownMax.Value));
        var cooldowns = new List<ProgressCooldown>();
        var replaced = false;
        foreach (var cd in LiveCooldowns(p.Cooldowns, now))
        {
            var item = cd;
            if (item.Key == key)
            {
                item = item with { Until = new Tick(Math.Max(item.Until.Value, until.Value)) };
                replaced = true;
            }
            cooldowns.Add(item);
        }
        if (!replaced && until > now && cooldowns.Count < 64)
        {
            cooldowns.Add(new ProgressCooldown(key, until));
        }
        return p with { Cooldowns = EquatableArray.From(cooldowns.OrderBy(cd => cd.Key, StringComparer.Ordinal)) };
    }

    /// <summary>Checks a persisted record against the review tick; throws <see cref="DomainException"/> when it is invalid.</summary>
    public static void ValidateConcernProgress(ConcernProgress p, Tick tick)
    {
        DomainException.Require(
            !(!AdmissionRules.ValidResource(new Resource(p.Concern.Value)) || p.Method.Length > 64 || p.LastProgress.Value < 0 || p.LastProgress > tick || p.NextReview.Value < 0 || !p.Blocked.IsValid() || !ValidPlannerNote(p.PlannerNote())),
            "invalid standard progress");
        foreach (var cd in p.Cooldowns)
        {
            DomainException.Require(!(cd.Key.Length == 0 || cd.Until.Value <= 0 || cd.Until.Value > tick.Value + ProgressCooldownMax.Value), "invalid standard progress cooldown");
        }
    }

    /// <summary>Reports a filed note whose cause is in the closed set and whose subject is within the bound Wording folds in; a cleared note has neither.</summary>
    internal static bool ValidPlannerNote(PlannerNote n)
    {
        if (n.Cause == Cause.None)
        {
            return (n.Subject ?? "").Length == 0;
        }
        return n.Cause.IsValid() && n.Subject == CauseRules.BoundSubject(n.Subject);
    }

    /// <summary>Reports unavailable methods and planner opt-outs.</summary>
    public static List<ConcernProgress> HoldProgress(IReadOnlyList<ConcernProgress> progress)
    {
        var result = new List<ConcernProgress>(progress.Count);
        foreach (var p in progress)
        {
            if (!p.Unmethoded())
            {
                result.Add(p);
            }
            else if (p.Planner == Cause.HeldOptIn)
            {
                result.Add(p with { Blocked = BlockedReason.HeldOptIn });
            }
            else
            {
                result.Add(p);
            }
        }
        return result;
    }
}
