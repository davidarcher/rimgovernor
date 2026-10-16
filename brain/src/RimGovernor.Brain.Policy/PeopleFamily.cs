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

/// <summary>What the people family reads (#3304): whether a choice dialog is open, and the open one.</summary>
public sealed record PeopleFacts
{
    /// <summary>Whether the game is force-paused on a choice dialog it opened by itself; unknown without the colony census.</summary>
    public Fact<bool> DialogOpen { get; init; }

    /// <summary>The open dialog; null when none is or the census was not read.</summary>
    public OpenDialog? Dialog { get; init; }
}

/// <summary>
/// The people family (#3304). Its Need:
/// <list type="bullet">
/// <item><c>people/dialog</c> (Wellbeing): no choice dialog the game opened by itself is waiting on an answer. It
/// replaced the AnswerDialog incident Concern, its inspection and planner; the runtime step answers the open window
/// with the option the answer policy picks, once per window and option (<see cref="DialogMethod"/>), and the
/// answer claims its window (<c>dialog/&lt;window&gt;</c>).</item>
/// </list>
/// </summary>
public sealed class PeopleFamily : IFamily<PeopleFacts>
{
    public const string FamilyId = "people";

    public static readonly NeedId Dialog = NeedId.Of(FamilyId, "dialog");

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Colony, FactFamily.Emergency];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(PeopleFacts facts) => [DialogNeed(facts.DialogOpen)];

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
