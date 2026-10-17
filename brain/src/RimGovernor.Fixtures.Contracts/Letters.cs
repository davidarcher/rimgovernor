#nullable enable

namespace RimGovernor.Fixtures.Contracts;

// test/dismiss_letter (scripts/fixtures/LetterFixture.cs), which every fixture build carries: a running wait takes an
// acknowledged letter off the stack through it. A record carries only what the harness reads.

/// <summary><c>test/dismiss_letter</c>'s arguments: the letter's unique load id, as the notifications read reports it.</summary>
public sealed record DismissLetterArgs
{
    public string? LetterId { get; init; }
}

/// <summary><c>test/dismiss_letter</c>'s reply: whether the letter was on the stack and removed.</summary>
public sealed record LetterDismissed
{
    public bool Removed { get; init; }
}
