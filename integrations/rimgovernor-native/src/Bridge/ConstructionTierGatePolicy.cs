using System;
using System.Collections.Generic;

namespace HomeBridge.BridgeTools
{
    // Pure comparison for the construction-tier delivery gate (#2523). No Verse types,
    // so the contract probe compiles it directly.
    internal static class ConstructionTierGatePolicy
    {
        internal const int Untiered = -1;

        // A tier-t site may receive a material only if no strictly lower tiered site
        // still needs it and could be delivered to by the same pawn. Equal tiers are
        // not ordered; untiered sites neither gate nor are gated. `neederSites` are
        // the sites that still need this material, each with its tier and a lazy
        // check that the delivering pawn could itself deliver to it.
        internal static bool Allows(int tier, IEnumerable<KeyValuePair<int, Func<bool>>> neederSites)
        {
            if (tier == Untiered) return true;
            foreach (var site in neederSites)
                if (site.Key != Untiered && site.Key < tier && site.Value()) return false;
            return true;
        }
    }
}
