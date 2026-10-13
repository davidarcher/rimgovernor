using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Go's <c>ReadNote</c>: receives one family read, the family and the native method (or held section) the read came
/// from. The scheduler hangs a note on each planner's context and compares what it saw with the planner's declared
/// families, so a planner that starts reading a family it never declared is caught by a test instead of silently
/// planning on facts its wake step does not track.
/// </summary>
public delegate void ReadNote(FactFamily family, string source);

/// <summary>
/// Go's <c>WithReadNote(ctx, note)</c> and <c>NoteRead(ctx, family, source)</c>: carries a <see cref="ReadNote"/> on
/// the ambient async context. Prefer passing the note explicitly; this exists for call chains that threaded it
/// through <c>context.Context</c> (the facts store calls <see cref="BridgeFunctions.NoteRead"/> for a read of a held
/// store section, which counts as a read although no native call was made).
/// </summary>
public static class ReadNoteScope
{
    private static readonly AsyncLocal<ReadNote?> Current = new();

    /// <summary>Runs until the returned scope is disposed, with <paramref name="note"/> receiving every noted read; a null note changes nothing.</summary>
    public static IDisposable With(ReadNote? note)
    {
        var previous = Current.Value;
        if (note is not null)
        {
            Current.Value = note;
        }
        return new Restore(previous);
    }

    internal static ReadNote? Get() => Current.Value;

    private sealed class Restore(ReadNote? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

public static partial class BridgeFunctions
{
    /// <summary>Reports a read of <paramref name="family"/> from <paramref name="source"/> to the context's note, if it carries one.</summary>
    public static void NoteRead(FactFamily family, string source) => ReadNoteScope.Get()?.Invoke(family, source);
}
