namespace RimGovernor.Brain.Policy;

/// <summary>
/// The force-pausing choice dialog the colony census observed open: its native window id (unknown when the census
/// did not report one), whether it accepts an answer yet, and its options in native order.
/// </summary>
public sealed record OpenDialog
{
    public Fact<int> Window { get; init; }

    public bool Interactive { get; init; }

    public EquatableArray<DialogOption> Options { get; init; }
}

/// <summary>Which of the people family's Needs a composition serves.</summary>
public sealed record PeopleNeeds
{
    public bool Dialog { get; init; }

    public bool Mood { get; init; }
}

/// <summary>
/// What the people family reads (#3304): whether a choice dialog is open, and the open one; the review's mood states
/// and the Needs at work that a mood may defer to.
/// </summary>
public sealed record PeopleFacts
{
    /// <summary>Whether the game is force-paused on a choice dialog it opened by itself; unknown without the colony census.</summary>
    public Fact<bool> DialogOpen { get; init; }

    /// <summary>The open dialog; null when none is or the census was not read.</summary>
    public OpenDialog? Dialog { get; init; }

    /// <summary>The review's tracked mood states, one per pawn; unknown while the review was not read.</summary>
    public Fact<EquatableArray<MoodState>> Mood { get; init; }

    /// <summary>
    /// The Needs whose latest progress row is unmet with a Method (decision 18): a mood whose pressure such a Need
    /// owns defers to it. Read from the review's rows, so it lags the provider's run by one review.
    /// </summary>
    public EquatableArray<NeedId> Working { get; init; }
}

/// <summary>
/// The people family (#3304). Its Needs:
/// <list type="bullet">
/// <item><c>people/dialog</c> (Wellbeing): no choice dialog the game opened by itself is waiting on an answer. It
/// replaced the AnswerDialog incident Concern, its inspection and planner; the runtime step answers the open window
/// with the option the answer policy picks, once per window and option (<see cref="DialogMethod"/>), and the
/// answer claims its window (<c>dialog/&lt;window&gt;</c>).</item>
/// <item><c>people/mood/&lt;pawn&gt;</c> (Wellbeing): one per pawn the review tracks, unmet while its mood is at risk
/// (the review's mood state), unknown while the pawn is missing or unread. It replaced the per-pawn mood incident
/// Concern and the mood relief planner. Nothing is committed: the step defers to the owner Need of the pawn's
/// dominant thought pressure while that Need is at work (<see cref="PolicyFunctions.MoodNeedProposal"/>), leaves
/// need relief to the shared portfolio, and holds a preventative cast as unsupported. An unread review is one
/// unknown <c>people/mood</c>.</item>
/// </list>
/// </summary>
public sealed class PeopleFamily(PeopleNeeds needs) : IFamily<PeopleFacts>
{
    public const string FamilyId = "people";

    public static readonly NeedId Dialog = NeedId.Of(FamilyId, "dialog");

    /// <summary>The mood Need of an unread review: one Unknown Need stands for the pawns nobody can list.</summary>
    public static readonly NeedId MoodCensus = NeedId.Of(FamilyId, MoodKind);

    private const string MoodKind = "mood";

    /// <summary>The mood Need of one pawn.</summary>
    public static NeedId Mood(PawnId pawn) => NeedId.Of(FamilyId, MoodKind, pawn.Value);

    /// <summary>Whether <paramref name="need"/> is a mood Need (a pawn's or the census).</summary>
    public static bool IsMood(NeedId need) => need.Family == FamilyId && need.Kind == MoodKind;

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Colony, FactFamily.Emergency];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(PeopleFacts facts) =>
    [
        .. needs.Dialog ? [DialogNeed(facts.DialogOpen)] : Array.Empty<Need>(),
        .. needs.Mood ? MoodNeeds(facts.Mood) : [],
    ];

    /// <summary>Nothing pure: the answer is the runtime step's, which fences the read it answers.</summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, PeopleFacts facts) => [];

    /// <summary>Unmet while a choice dialog is open, unknown while the census cannot say, met otherwise.</summary>
    public static Need DialogNeed(Fact<bool> open)
    {
        if (!open.TryGetValue(out var waiting))
        {
            return new Need { Id = Dialog, Tier = NeedTier.Wellbeing, Evidence = [new("choice_dialog", default)] };
        }
        return new Need
        {
            Id = Dialog,
            Tier = NeedTier.Wellbeing,
            Satisfaction = waiting ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = Fact.Known(waiting ? 1.0 : 0.0),
            Evidence = [new("choice_dialog", Fact.Known(waiting ? "open" : "none"))],
        };
    }

    /// <summary>
    /// The mood Needs: one per tracked pawn, unmet while the review holds its mood at risk, met once it recovered,
    /// unknown while the pawn is missing or its mood, threshold or availability is unread (<see cref="MoodState.Need"/>).
    /// An unread review is one Unknown census Need.
    /// </summary>
    public static IReadOnlyList<Need> MoodNeeds(Fact<EquatableArray<MoodState>> mood)
    {
        if (!mood.TryGetValue(out var states))
        {
            return [new Need { Id = MoodCensus, Tier = NeedTier.Wellbeing, Deficit = Fact.Unknown<double>(), Evidence = [new("mood_states", default)] }];
        }
        return [.. states.Select(s =>
        {
            var satisfaction = s.Need() switch
            {
                Finding.Unmet => Satisfaction.Unmet,
                Finding.Met => Satisfaction.Met,
                _ => Satisfaction.Unknown,
            };
            return new Need
            {
                Id = Mood(s.Pawn.Id),
                Tier = NeedTier.Wellbeing,
                Satisfaction = satisfaction,
                Deficit = PolicyFunctions.WholeDeficit(satisfaction),
                Evidence = [new("mood_risk", satisfaction == Satisfaction.Unknown ? default : Fact.Known(satisfaction == Satisfaction.Unmet ? "active" : "recovered"))],
            };
        })];
    }

    /// <summary>
    /// The answer's Method: <c>dialog-</c> and the first 16 bytes of the SHA-256 of window, option index and label,
    /// in hex, so one window's chosen option is answered once.
    /// </summary>
    public static MethodId DialogMethod(int window, DialogOption chosen)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{window}/{chosen.Index}/{chosen.Label}")));
        return new MethodId("dialog-" + Convert.ToHexStringLower(digest.AsSpan(0, 16)));
    }
}
