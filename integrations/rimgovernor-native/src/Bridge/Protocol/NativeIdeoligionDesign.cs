#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;

namespace HomeBridge.BridgeTools
{
    // Build off the live object. Refusal never clears the colony's precepts.
    internal static class NativeIdeoligionDesign
    {
        internal static Common.Failure? Prepare(Ideo source, Common.IdeoligionDesign design, bool reform, out Ideo? candidate)
        {
            candidate = null;
            Common.Failure Invalid(string reason) => ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, reason);
            if (!ModsConfig.IdeologyActive || source == null || design == null || !design.HasFluid)
                return Invalid("Ideoligion design requires Ideology and an explicit mode.");
            if (reform && (!source.Fluid || source.development == null || !source.development.CanReformNow || !design.Fluid))
                return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "The ideoligion cannot reform now.");
            var memes = new List<MemeDef>();
            foreach (var name in design.Memes)
            {
                var meme = DefDatabase<MemeDef>.GetNamedSilentFail(name);
                if (meme == null || memes.Contains(meme) || meme.hiddenInChooseMemes || !IdeoUtility.IsMemeAllowedFor(meme, Faction.OfPlayer.def))
                    return Invalid("Unknown, duplicate or unavailable meme.");
                if (!reform && design.Fluid && !IdeoUtility.IsMemeAllowedForInitialFluidIdeo(meme))
                    return Invalid("Meme unavailable for an initial fluid ideoligion.");
                if (memes.Any(m => m.exclusionTags.Intersect(meme.exclusionTags).Any()))
                    return Invalid("Incompatible memes.");
                memes.Add(meme);
            }
            int normal = memes.Count(m => m.category == MemeCategory.Normal);
            var range = !reform && design.Fluid ? IdeoFoundation.MemeCountRangeFluidAbsolute : IdeoFoundation.MemeCountRangeAbsolute;
            if (memes.Count(m => m.category == MemeCategory.Structure) != 1 || normal < range.min || normal > range.max)
                return Invalid("Invalid structure or normal meme count.");
            if (Faction.OfPlayer.def.requiredMemes?.Any(m => !memes.Contains(m)) == true)
                return Invalid("Missing a required faction meme.");
            if (reform)
            {
                var oldNormal = source.memes.Where(m => m.category == MemeCategory.Normal).ToList();
                int added = memes.Count(m => m.category == MemeCategory.Normal && !oldNormal.Contains(m));
                int removed = oldNormal.Count(m => !memes.Contains(m));
                bool structureChanged = source.StructureMeme != memes.Single(m => m.category == MemeCategory.Structure);
                if (added + removed > 1 || (structureChanged && added + removed != 0))
                    return Invalid("A reform allows one structure change or one normal meme addition/removal.");
                foreach (var faction in Find.FactionManager.AllFactions)
                    if (faction.ideos != null && (faction.ideos.IsPrimary(source) || faction.ideos.IsMinor(source)) &&
                        (memes.Any(m => !IdeoUtility.IsMemeAllowedFor(m, faction.def)) || faction.def.requiredMemes?.Any(m => !memes.Contains(m)) == true))
                        return Invalid("Design conflicts with a faction sharing the ideoligion.");
            }
            var selections = new List<PreceptDef>();
            foreach (var name in design.Precepts)
            {
                var def = DefDatabase<PreceptDef>.GetNamedSilentFail(name);
                if (def == null || def.preceptClass != typeof(Precept) || selections.Contains(def))
                    return Invalid("Design requires distinct plain precepts.");
                selections.Add(def);
            }
            var copy = IdeoGenerator.MakeIdeo(source.foundation.def);
            source.CopyTo(copy);
            copy.Fluid = design.Fluid;
            var previous = copy.memes.ToList();
            copy.memes.Clear(); copy.memes.AddRange(memes); copy.SortMemesInDisplayOrder();
            if (reform)
                copy.foundation.EnsurePreceptsCompatibleWithMemes(previous, memes, new IdeoGenerationParms(Faction.OfPlayer.def));
            else
                copy.foundation.RandomizePrecepts(true, new IdeoGenerationParms(Faction.OfPlayer.def, forNewFluidIdeo: design.Fluid, fixedIdeo: !design.Fluid));
            foreach (var precept in copy.PreceptsListForReading.Where(p => p.def.preceptClass == typeof(Precept)).ToList())
                copy.RemovePrecept(precept, replacing: true);
            foreach (var def in selections.OrderBy(d => d.defName, StringComparer.Ordinal))
            {
                if (!copy.foundation.CanAddForFaction(def, Faction.OfPlayer.def, null, checkDuplicates: true))
                    return Invalid("Precept conflicts with the proposed design.");
                if (reform && Find.FactionManager.AllFactions.Any(f => f.def.humanlikeFaction && f.ideos != null &&
                    (f.ideos.IsPrimary(source) || f.ideos.IsMinor(source)) && !copy.foundation.CanAddForFaction(def, f.def, null, checkDuplicates: false)))
                    return Invalid("Precept conflicts with a faction sharing the ideoligion.");
                copy.AddPrecept(PreceptMaker.MakePrecept(def), init: true, generatingFor: Faction.OfPlayer.def);
            }
            foreach (var meme in memes)
                if (meme.requireOne?.Any(group => !copy.PreceptsListForReading.Any(p => group.Contains(p.def))) == true)
                    return Invalid("Missing a meme's required precept.");
            foreach (var issue in DefDatabase<IssueDef>.AllDefs)
                if (issue.HasDefaultPrecept && !copy.PreceptsListForReading.Any(p => p.def.issue == issue))
                    return Invalid("Missing a default precept issue.");
            if (copy.FirstIncompatiblePreceptPair() != default(Pair<Precept, Precept>) || copy.FirstRitualMissingTarget() != null || copy.FirstConsumableBuildingMissingRitual() != null)
                return Invalid("Incompatible precepts or missing ritual target.");
            candidate = copy;
            return null;
        }

        internal static void Create(Common.IdeoligionDesign design)
        {
            var source = Faction.OfPlayer.ideos.PrimaryIdeo;
            var refusal = Prepare(source, design, false, out var candidate);
            if (refusal != null) throw new InvalidOperationException(refusal.Detail);
            candidate!.CopyTo(source);
        }
    }
}
