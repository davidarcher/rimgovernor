#nullable enable

using System.Collections.Generic;

namespace RimGovernor.Fixtures.Contracts;

// The replies of the presentation fixtures (#3404): a force-pausing choice dialog the game would open by itself, and a
// plain letter of a known def. A record carries only what a case reads.

/// <summary>
/// <c>test/open_choice_dialog</c>: the fixture's last dialog and the labels of every option activated since the latest
/// open, in order; <see cref="DueTick"/> is a scheduled open's tick.
/// </summary>
public sealed record ChoiceDialogRead
{
    public bool Success { get; init; }

    public bool Scheduled { get; init; }

    public int DueTick { get; init; }

    public int WindowId { get; init; }

    public bool WindowOpen { get; init; }

    public bool ForcePaused { get; init; }

    public IReadOnlyList<string> Chosen { get; init; } = [];

    public IReadOnlyList<string> ForcePausingWindows { get; init; } = [];

    public int Tick { get; init; }
}

/// <summary><c>test/deliver_letter</c>: the delivered letter's id and the game tick it was delivered (or scheduled) on.</summary>
public sealed record LetterDelivered
{
    public bool Success { get; init; }

    public string LetterId { get; init; } = "";

    public int Tick { get; init; }
}
