namespace RimGovernor.Brain.Domain;

/// <summary>
/// Explicit intent to activate one exact observed option of the single force-pausing choice dialog the game
/// opened by itself (a caravan demand or meeting, a quest dialog, a finished research project's completion
/// dialog). WindowId plus the option's native list position and label stand in for an entity precondition: the
/// native AnswerDialog operation refuses on any drift from these exact observed values. A nonempty LetterToken
/// explicitly targets a WandererJoins letter with WindowId carrying its letter id instead.
/// </summary>
public sealed record DialogAnswer : IActionPayload
{
    /// <summary>Go <c>NewDialogAnswer</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public DialogAnswer(int windowId, int optionIndex, string optionLabel)
        : this(windowId, optionIndex, optionLabel, "") { }

    private DialogAnswer(int windowId, int optionIndex, string optionLabel, string letterToken)
    {
        DomainException.Require(windowId >= 0 && optionIndex >= 0 && Identity.IsValidText(optionLabel), "dialog answer requires a valid window, option index and label");
        WindowId = windowId;
        OptionIndex = optionIndex;
        OptionLabel = optionLabel;
        LetterToken = letterToken;
    }

    public int WindowId { get; }

    public int OptionIndex { get; }

    public string OptionLabel { get; }

    /// <summary>Empty for a window answer.</summary>
    public string LetterToken { get; }

    /// <summary>Go <c>NewJoinerLetterAnswer</c>: targets a pending native letter rather than a window.</summary>
    public static DialogAnswer NewJoinerLetterAnswer(int letterId, string label, string token)
    {
        DomainException.Require(letterId >= 0 && Identity.IsValidText(label) && Identity.IsValidText(token), "invalid joiner letter target");
        return new DialogAnswer(letterId, 0, label, token);
    }
}

public sealed partial record DomainAction
{
    public static DomainAction NewDialogAnswerAction(ActionId id, DialogAnswer answer) => Create(id, ActionKind.DialogAnswer, answer);

    public DialogAnswer? DialogAnswer => PayloadOf<DialogAnswer>(ActionKind.DialogAnswer);
}
