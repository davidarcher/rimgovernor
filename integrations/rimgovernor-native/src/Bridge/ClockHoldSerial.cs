#nullable enable

namespace HomeBridge.BridgeTools
{
    // The hold serial (#3414): bumped on every hold entry, the brain's own holds
    // included, and reported on Status. A Run quotes the serial its step read, and
    // resuming a held clock is refused when the serial moved since, so a hold the
    // step did not see is never released. A serial and not a tick: holds pause the
    // game, so the tick freezes and a read before and after a same-tick hold look
    // identical. Touched only on the game thread under the supervisor gate. Pure
    // state with no Verse types, so the contract probe compiles it directly.
    internal sealed class ClockHoldSerial
    {
        internal ulong Current { get; private set; }

        internal void Entered() => Current++;

        /// The refusal of a resume planned at <paramref name="planned"/>, or null
        /// when no hold was entered since that read.
        internal string? Refuses(ulong planned) => planned == Current ? null
            : "A hold was entered after the step's read (hold serial " + planned + ", now " + Current + "); re-plan from a fresh read.";
    }
}
