#nullable disable
namespace RimGovernor.Runtime
{
    // Why a pawn leaves a bout early (#2709). None means it did not.
    public enum SparringStop { None, Pain, Bleeding, Exchanges }

    // The per-pawn stop rule, a pure function of three simple values. A pawn that
    // hits it ends its own spar job; the rest of the bout continues. The numbers
    // were tuned in the sparring lab (#2711): see facilities.md.
    public static class SparringStopRule
    {
        // Total pain (0 to 1) at which a pawn stops.
        public const float PainLimit = 0.4f;
        // Bleeding above this rate (hediff bleed units per day) stops a pawn: a
        // bruise does not bleed, so only a cut at the sharp tiers can reach it.
        public const float BleedLimit = 1.5f;
        // Swings a pawn makes in one bout.
        public const int MaxExchanges = 12;

        public static SparringStop Check(float pain, float bleedRate, int exchanges)
        {
            if (pain >= PainLimit) return SparringStop.Pain;
            if (bleedRate > BleedLimit) return SparringStop.Bleeding;
            if (exchanges >= MaxExchanges) return SparringStop.Exchanges;
            return SparringStop.None;
        }
    }
}
