namespace RimGovernor.Brain.Policy;

/// <summary>
/// Names one Need (#3250): <c>family/kind[/subject]</c>, stable across reviews and loads and reproducible from facts
/// alone. <see cref="Family"/> is the owning family's id and <see cref="Kind"/> the kind of Need it derives, both
/// lowercase ASCII letters, digits and hyphens; the optional <see cref="Subject"/> names one occurrence or target
/// (a pawn, an event, a building) in printable ASCII without '/' or spaces. Never hash a load token or tick into it.
/// </summary>
public readonly record struct NeedId(string Value) : IComparable<NeedId>
{
    public string Value { get => field ?? string.Empty; } = Value;

    /// <summary>The owning family's id: the first segment.</summary>
    public string Family => Segment(0);

    /// <summary>The kind of Need: the second segment.</summary>
    public string Kind => Segment(1);

    /// <summary>Whether the id is <c>family/kind[/subject]</c> in the allowed alphabets and within the id bound.</summary>
    public bool IsValid
    {
        get
        {
            if (!Identity.IsValidText(Value))
            {
                return false;
            }
            var parts = Value.Split('/');
            return parts.Length is 2 or 3 && Slug(parts[0]) && Slug(parts[1]) && (parts.Length == 2 || SubjectText(parts[2]));
        }
    }

    /// <summary>
    /// The key a ConcernId-keyed reader files this Need under (planner reasons, need_transitions, explain): its
    /// value. A NeedId always holds a '/', which no Concern id does, so the two key spaces never collide.
    /// </summary>
    public ConcernId FilingKey => new(Value);

    /// <summary>The id of <paramref name="family"/>'s <paramref name="kind"/> Need, about <paramref name="subject"/> when given; throws for an invalid result.</summary>
    public static NeedId Of(string family, string kind, string subject = "")
    {
        var id = new NeedId(subject.Length == 0 ? family + "/" + kind : family + "/" + kind + "/" + subject);
        DomainException.Require(id.IsValid, $"invalid need id \"{id.Value}\"");
        return id;
    }

    public void Validate() => DomainException.Require(IsValid, $"invalid need id \"{Value}\"");

    public bool Equals(NeedId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public int CompareTo(NeedId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    public static bool operator <(NeedId left, NeedId right) => left.CompareTo(right) < 0;

    public static bool operator >(NeedId left, NeedId right) => left.CompareTo(right) > 0;

    public static bool operator <=(NeedId left, NeedId right) => left.CompareTo(right) <= 0;

    public static bool operator >=(NeedId left, NeedId right) => left.CompareTo(right) >= 0;

    private string Segment(int index)
    {
        var parts = Value.Split('/');
        return index < parts.Length ? parts[index] : "";
    }

    private static bool Slug(string s) => s.Length > 0 && s.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static bool SubjectText(string s) => s.Length > 0 && s.All(c => c is > ' ' and <= '~' and not '/');
}

/// <summary>A Need's tier: selection orders by it first, and <see cref="SeriousHarm"/> Needs also run in the immediate pass.</summary>
public enum NeedTier
{
    [WireName("serious_harm")]
    SeriousHarm,

    [WireName("wellbeing")]
    Wellbeing,

    [WireName("development")]
    Development,
}

/// <summary>
/// A Need's satisfaction predicate over what stands now. <see cref="Unknown"/> is the default and never counts as
/// met: not for proposing, and not as a precondition.
/// </summary>
public enum Satisfaction
{
    [WireName("unknown")]
    Unknown,

    [WireName("unmet")]
    Unmet,

    [WireName("met")]
    Met,
}

/// <summary>One fact a Need's finding rests on, for explain: its name and value, or unknown.</summary>
public readonly record struct NeedEvidence(string Fact, Fact<string> Value);

/// <summary>
/// A Need (#3250): derived fresh from facts on every review, never stored. It replaces the Concern registry row,
/// the Rounds assessment and the Standard episode. A Standard's target, an Incident's occurrence and a Once outcome
/// are all a predicate over what stands.
/// </summary>
public sealed record Need
{
    public required NeedId Id { get; init; }

    public required NeedTier Tier { get; init; }

    /// <summary>The predicate's result; unknown unless the facts say.</summary>
    public Satisfaction Satisfaction { get; init; }

    /// <summary>How far from met, in the family's own unit; unknown when the facts do not say.</summary>
    public Fact<double> Deficit { get; init; }

    public EquatableArray<NeedEvidence> Evidence { get; init; }

    /// <summary>Other Needs whose predicate must be met before this one is proposed for; ordering between Needs.</summary>
    public EquatableArray<NeedId> Preconditions { get; init; }

    /// <summary>Throws for a Need its family may not derive: an invalid id or one outside the family, a self or invalid precondition, a negative deficit.</summary>
    public void Validate(string family)
    {
        Id.Validate();
        DomainException.Require(Id.Family == family, $"need {Id} does not belong to family {family}");
        DomainException.Require(Enum.IsDefined(Tier) && Enum.IsDefined(Satisfaction), $"need {Id} has an invalid tier or satisfaction");
        DomainException.Require(!Deficit.TryGetValue(out var deficit) || (deficit >= 0 && double.IsFinite(deficit)), $"need {Id} has an invalid deficit");
        foreach (var precondition in Preconditions)
        {
            precondition.Validate();
            DomainException.Require(precondition != Id, $"need {Id} waits on itself");
        }
    }
}

/// <summary>
/// A candidate Method a family proposes for one ready Need: the MethodId the journal keys it by (the same unmet
/// Need yields the same MethodId, so a repeat continues the open Method), the planner's short why, and the plan
/// built from the plan id the runtime mints (actions are conventionally <c>{plan}-{n}</c>).
/// </summary>
public sealed record NeedMethod(NeedId Need, MethodId Method, string Reason, Func<PlanId, PlanSpec> Plan)
{
    /// <summary>
    /// Set when the family holds the ready Need instead of proposing work: nothing is committed and the runtime
    /// files the hold as the Need's awaiting-plan reason. Build one with <see cref="Held"/>.
    /// </summary>
    public NeedHold? Hold { get; init; }

    /// <summary>A hold for <paramref name="need"/>: a stable subject (for example <c>unsupported_forecast_repair</c>) and its detail.</summary>
    public static NeedMethod Held(NeedId need, string subject, string detail) =>
        new(need, default, detail, _ => throw new DomainException($"need {need} is held: {subject}")) { Hold = new NeedHold(subject, detail) };
}

/// <summary>Why a family holds a ready Need without a Method: a stable refusal subject and its detail.</summary>
public sealed record NeedHold(string Subject, string Detail);

/// <summary>Where a derived Need stands before proposing: met, unknown, waiting on a precondition, or ready for a Method.</summary>
public enum NeedReadiness
{
    [WireName("met")]
    Met,

    [WireName("unknown")]
    Unknown,

    [WireName("waiting")]
    Waiting,

    [WireName("ready")]
    Ready,
}

/// <summary>A derived Need and where it stands; <see cref="WaitingOn"/> names the first precondition that does not hold.</summary>
public sealed record NeedStanding(Need Need, NeedReadiness Readiness, NeedId WaitingOn = default);

/// <summary>
/// The family module (#3250): one per domain. It replaces the Concern registry rows, Inspect bodies,
/// <c>Workable</c> and phase checks and per-planner whole-owner guards of the Concerns it absorbs. The runtime
/// registers it as one scheduler entry; see <see cref="IFamily{TFacts}"/> for the policy.
/// </summary>
public interface IFamily
{
    /// <summary>The family segment of its NeedIds, for example <c>food-stores</c>.</summary>
    string Id { get; }

    /// <summary>The fact families its full read covers; a read outside them (Definitions and Identity are ambient) is an audit finding.</summary>
    IReadOnlyList<FactFamily> Reads { get; }

    /// <summary>The narrow fact families its <see cref="NeedTier.SeriousHarm"/> Needs read in the immediate pass; empty for a family without such Needs.</summary>
    IReadOnlyList<FactFamily> SeriousHarmReads { get; }
}

/// <summary>
/// A family's pure policy over its own fact type. The runtime reads <typeparamref name="TFacts"/> (the full read,
/// or in the immediate pass the narrow one, whose other facts stay unknown), derives, evaluates preconditions with
/// <see cref="PolicyFunctions.NeedStandings"/>, and asks for Methods for the ready Needs only.
/// </summary>
public interface IFamily<in TFacts> : IFamily
{
    /// <summary>Pure. Derives every Need of the family from the current facts, in the family's own order.</summary>
    IReadOnlyList<Need> Derive(TFacts facts);

    /// <summary>Pure. Candidate Methods for <paramref name="ready"/> (unmet, preconditions met), in the family's preferred order within a tier.</summary>
    IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, TFacts facts);
}

public static partial class NeedsRules
{
    /// <summary>
    /// Where each derived Need stands, ordered by tier and then the family's derivation order. A Need is met or
    /// unknown by its own predicate; an unmet one waits on its first precondition that is not met among
    /// <paramref name="needs"/> (a precondition that is unknown or was not derived does not hold) and is otherwise
    /// ready. Throws for an invalid Need or a repeated id.
    /// </summary>
    public static IReadOnlyList<NeedStanding> NeedStandings(string family, IReadOnlyList<Need> needs)
    {
        var byId = new Dictionary<NeedId, Need>();
        foreach (var need in needs)
        {
            need.Validate(family);
            DomainException.Require(byId.TryAdd(need.Id, need), $"need {need.Id} derived twice");
        }
        var standings = new List<NeedStanding>(needs.Count);
        foreach (var need in needs.Select((n, i) => (Need: n, Index: i)).OrderBy(n => n.Need.Tier).ThenBy(n => n.Index).Select(n => n.Need))
        {
            standings.Add(need.Satisfaction switch
            {
                Satisfaction.Met => new NeedStanding(need, NeedReadiness.Met),
                Satisfaction.Unknown => new NeedStanding(need, NeedReadiness.Unknown),
                _ => need.Preconditions.FirstOrDefault(p => !byId.TryGetValue(p, out var held) || held.Satisfaction != Satisfaction.Met) is { IsValid: true } waiting
                    ? new NeedStanding(need, NeedReadiness.Waiting, waiting)
                    : new NeedStanding(need, NeedReadiness.Ready),
            });
        }
        return standings;
    }
}
