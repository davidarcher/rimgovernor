namespace RimGovernor.Brain.Policy;

/// <summary>
/// One option of the force-pausing choice dialog the colony census observed, in native order. Keys are the
/// language-neutral Keyed translation keys native recovered for the label; Selectable already excludes disabled
/// rows, hyperlinks and options that open another window instead of resolving or linking.
/// </summary>
public sealed record DialogOption
{
    public int Index { get; init; }

    public string Label { get; init; } = "";

    public EquatableArray<string> Keys { get; init; }

    public bool Selectable { get; init; }

    public bool Resolves { get; init; }
}

/// <summary>
/// Decides which option answers a choice dialog the game opened by itself. Prefer is an ordered list of
/// patterns; a pattern matches an option when it equals one of the option's translation keys (case-insensitive,
/// so the same policy holds in every game language) or is a case-insensitive substring of its label (a mod's
/// literal text). The first pattern with a selectable match wins. When nothing matches, the default answer is
/// the first selectable option that closes the dialog, then the first that links onward.
/// </summary>
public sealed record DialogAnswerPolicy(EquatableArray<string> Prefer);

public static partial class PolicyFunctions
{
    /// <summary>
    /// The passive, non-escalating preference order, by Core translation key: dismiss an informational dialog,
    /// move on from a met caravan rather than trade or attack, keep watching past a game-over prompt, and pay a
    /// demand rather than fight a caravan the colony did not choose to arm. Close dismisses a monolith level
    /// letter without opening a main tab.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultDialogAnswerPrefer = ["OK", "Close", "Ignore", "CaravanMeeting_MoveOn", "GameOverKeepWatching", "CaravanDemand_Give"];

    // The void node's choice (CompVoidNode.OpenDialog): VoidNodeDisrupt ends the awakening quest by collapsing
    // the monolith, VoidNodeEmbrace is the irreversible alternative the colony never takes, VoidNodePostpone
    // closes the dialog and leaves the node touchable.
    public const string VoidNodeDisrupt = "VoidNodeDisrupt";
    public const string VoidNodeEmbrace = "VoidNodeEmbrace";
    public const string VoidNodePostpone = "VoidNodePostpone";

    /// <summary>Reports whether one Prefer pattern names the option.</summary>
    public static bool DialogPatternMatches(string pattern, DialogOption option)
    {
        var needle = pattern.Trim();
        if (needle == "")
        {
            return false;
        }
        foreach (var key in option.Keys)
        {
            if (string.Equals(key, needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return option.Label.ToLowerInvariant().Contains(needle.ToLowerInvariant(), StringComparison.Ordinal);
    }

    private static DialogOption? VoidNodeOption(IReadOnlyList<DialogOption> options, string key)
    {
        foreach (var option in options)
        {
            if (option.Selectable && DialogPatternMatches(key, new DialogOption { Keys = option.Keys }))
            {
                return option;
            }
        }
        return null;
    }

    /// <summary>Whether the options are the void node's choice.</summary>
    public static bool IsVoidNodeDialog(IReadOnlyList<DialogOption> options)
    {
        foreach (var option in options)
        {
            foreach (var key in new[] { VoidNodeDisrupt, VoidNodeEmbrace, VoidNodePostpone })
            {
                if (DialogPatternMatches(key, new DialogOption { Keys = option.Keys }))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Whether the void node's choice offers no selectable VoidNodeDisrupt: the dialog is then postponed, and
    /// the caller names the absence loudly.
    /// </summary>
    public static bool VoidNodeDisruptAbsent(IReadOnlyList<DialogOption> options) =>
        IsVoidNodeDialog(options) && VoidNodeOption(options, VoidNodeDisrupt) is null;

    /// <summary>
    /// Applies the policy to the observed options. Null when no option can answer the dialog at all (every
    /// option disabled, a hyperlink or a window-opening action), in which case the controller has no method and
    /// the player must intervene. The void node's choice is answered only by its key: VoidNodeDisrupt, else
    /// VoidNodePostpone, never by the default position and never VoidNodeEmbrace.
    /// </summary>
    public static DialogOption? ChooseDialogOption(IReadOnlyList<DialogOption> options, DialogAnswerPolicy p)
    {
        if (IsVoidNodeDialog(options))
        {
            return VoidNodeOption(options, VoidNodeDisrupt) ?? VoidNodeOption(options, VoidNodePostpone);
        }
        foreach (var pattern in p.Prefer)
        {
            foreach (var option in options)
            {
                if (option.Selectable && DialogPatternMatches(pattern, option))
                {
                    return option;
                }
            }
        }
        foreach (var resolves in new[] { true, false })
        {
            foreach (var option in options)
            {
                if (option.Selectable && option.Resolves == resolves)
                {
                    return option;
                }
            }
        }
        return null;
    }
}
