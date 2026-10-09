#nullable enable
using RimWorld;
using Verse;

namespace RimGovernor.Runtime
{
    // The one definition of "burnable", shared by the two
    // special filters below so a dump zone routes waste through native hauling:
    // a rottable past Fresh, except a colonist's or slave's corpse (those are
    // buried or entombed), or an apparel or weapon worth less than SilverCutoff.
    // Gear only: resource stacks are never judged by value.
    public static class BurnableRule
    {
        // Absolute silver, per item. MarketValue already folds in hit points,
        // quality and the tainted (worn by a corpse) factor, so a tattered or
        // tainted garment falls under it while sound gear stays above.
        public const float SilverCutoff = 5f;

        public static bool CanEverMatch(ThingDef def) =>
            def.IsCorpse || def.IsApparel || def.IsWeapon || def.GetCompProperties<CompProperties_Rottable>() != null;

        public static bool IsBurnable(Thing thing)
        {
            if (thing is Corpse corpse && IsOwnPerson(corpse.InnerPawn)) return false;
            var rottable = thing.TryGetComp<CompRottable>();
            if (rottable != null) return rottable.Stage != RotStage.Fresh;
            if (thing.def.IsApparel || thing.def.IsWeapon) return thing.GetStatValue(StatDefOf.MarketValue) < SilverCutoff;
            return false;
        }

        private static bool IsOwnPerson(Pawn? pawn) =>
            pawn != null && pawn.Faction != null && pawn.Faction.IsPlayer && pawn.RaceProps.Humanlike;
    }

    // Not-burnable is the complement within what the filter can ever judge:
    // a Steel stack matches neither filter, so disallowing either never
    // rejects it.
    public sealed class SpecialThingFilterWorker_RimGovernorBurnable : SpecialThingFilterWorker
    {
        public override bool Matches(Thing t) => BurnableRule.CanEverMatch(t.def) && BurnableRule.IsBurnable(t);
        public override bool CanEverMatch(ThingDef def) => BurnableRule.CanEverMatch(def);
    }

    public sealed class SpecialThingFilterWorker_RimGovernorNotBurnable : SpecialThingFilterWorker
    {
        public override bool Matches(Thing t) => BurnableRule.CanEverMatch(t.def) && !BurnableRule.IsBurnable(t);
        public override bool CanEverMatch(ThingDef def) => BurnableRule.CanEverMatch(def);
    }
}
