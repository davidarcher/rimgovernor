namespace RimGovernor.Brain.Policy;

/// <summary>
/// The force-pausing choice dialog the colony census observed open: its native window id,
/// whether it accepts an answer yet, and its options in native order.
/// </summary>
public sealed record OpenDialog
{
    public int Window { get; init; }

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
/// What the people family reads (#3304): whether a choice dialog is open; the review's mood states
/// and the Needs at work that a mood may defer to.
/// </summary>
public sealed record PeopleFacts
{
    /// <summary>Whether the game is force-paused on a choice dialog it opened by itself; read from the colony census.</summary>
    public bool DialogOpen { get; init; }

    /// <summary>The review's tracked mood states, one per pawn; read from the current review.</summary>
    public EquatableArray<MoodState> Mood { get; init; }

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
/// (the review's mood state), met once the pawn is missing or dead. It replaced the per-pawn mood incident
/// Concern and the mood relief planner. Nothing is committed: the step defers to the owner Need of the pawn's
/// dominant thought pressure while that Need is at work (<see cref="MoodRules.MoodNeedProposal"/>), leaves
/// need relief to the shared portfolio, and holds a preventative cast as unsupported. An unread review or pawn
/// raises <see cref="RequiredFactException"/>.</item>
/// </list>
/// </summary>
public sealed class PeopleFamily(PeopleNeeds needs) : IFamily<PeopleFacts>
{
    public const string FamilyId = "people";

    public static readonly NeedId Dialog = NeedId.Of(FamilyId, "dialog");

    private const string MoodKind = "mood";

    /// <summary>The mood Need of one pawn.</summary>
    public static NeedId Mood(PawnId pawn) => NeedId.Of(FamilyId, MoodKind, pawn.Value);

    /// <summary>Whether <paramref name="need"/> is a pawn's mood Need.</summary>
    public static bool IsMood(NeedId need) => need.Family == FamilyId && need.Kind == MoodKind;

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Colony, FactFamily.Emergency];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(PeopleFacts facts) =>
    [
        .. needs.Dialog ? [DialogNeed(facts.DialogOpen)] : Array.Empty<Need>(),
        .. needs.Mood ? MoodNeeds(facts.Mood) : [],
    ];

    public IReadOnlyList<Need> DeriveSeriousHarm(PeopleFacts facts) => [];

    /// <summary>Nothing pure: the answer is the runtime step's, which fences the read it answers.</summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, PeopleFacts facts) => [];

    /// <summary>Unmet while the observed choice dialog is open, met otherwise.</summary>
    public static Need DialogNeed(bool waiting)
    {
        return new Need
        {
            Id = Dialog,
            Tier = NeedTier.Wellbeing,
            Satisfaction = waiting ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = waiting ? 1.0 : 0.0,
        };
    }

    /// <summary>
    /// The mood Needs: one per tracked pawn, unmet while the review holds its mood at risk, met once it recovered or
    /// the pawn is missing or dead (<see cref="MoodState.Need"/>). A present pawn's unread mood,
    /// threshold or availability, raises <see cref="RequiredFactException"/>.
    /// </summary>
    public static IReadOnlyList<Need> MoodNeeds(EquatableArray<MoodState> states)
    {
        return [.. states.Select(s => FoodRules.WholeNeed(Mood(s.Pawn.Id), NeedTier.Wellbeing, MoodMet(s)))];
    }

    /// <summary>Whether the pawn's mood stands met: a missing or dead pawn is; an unclear finding raises <see cref="RequiredFactException"/>.</summary>
    private static bool MoodMet(MoodState s)
    {
        var finding = s.Need();
        if (finding == Finding.Unclear && !s.Missing && !s.Pawn.Dead)
        {
            throw new RequiredFactException("mood of " + s.Pawn.Id.Value);
        }
        return finding != Finding.Unmet;
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
