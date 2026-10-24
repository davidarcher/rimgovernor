#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;

namespace HomeBridge.BridgeTools
{
    // Build off the live object. Refusal never clears the colony's precepts.
    internal static class NativeIdeoligionDesign
    {
        internal static Common.Failure? Prepare(Ideo source, Common.IdeoligionDesign design, bool reform, out Ideo? candidate)
        {
            candidate = null;
            Common.Failure Invalid(string reason) => ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, reason);
            if (!ModsConfig.IdeologyActive || source == null || design == null)
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
            // IdeoStyleTracker builds its hair, beard and tattoo maps on first read, and its CopyTo reads them unguarded. A
            // new colony's ideo has had no read yet (no starting pawn is rolled before the design, #2723); this count read
            // is vanilla's own lazy build, and keeps maps a played ideo already has.
            _ = source.style.NumHairAndBeardStylesAvailable;
            var copy = IdeoGenerator.MakeIdeo(source.foundation.def);
            source.CopyTo(copy);
            copy.Fluid = design.Fluid;
            var previous = copy.memes.ToList();
            copy.memes.Clear(); copy.memes.AddRange(memes); copy.SortMemesInDisplayOrder();
            if (reform)
                copy.foundation.EnsurePreceptsCompatibleWithMemes(previous, memes, new IdeoGenerationParms(Faction.OfPlayer.def));
            else
            {
                copy.foundation.RandomizePrecepts(true, new IdeoGenerationParms(Faction.OfPlayer.def, forNewFluidIdeo: design.Fluid, fixedIdeo: !design.Fluid));
                // A new ideo's style items follow its designed memes, not the memes it was generated with.
                copy.style.RecalculateAvailableStyleItems();
            }
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
            // Venerated animals are the design's own list: those it omits go, and each added one passes the precept worker's
            // CanUse, the precept's maxCount and the faction rules. Style categories are design identity only; a reform keeps them.
            var animalDefs = new List<ThingDef>();
            foreach (var name in design.VeneratedAnimals)
            {
                var animal = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                if (animal == null || animalDefs.Contains(animal))
                    return Invalid("Design requires distinct known animals to venerate.");
                animalDefs.Add(animal);
            }
            if (animalDefs.Count > PreceptDefOf.AnimalVenerated.maxCount)
                return Invalid("Too many venerated animals.");
            foreach (var precept in copy.PreceptsListForReading.OfType<Precept_Animal>().Where(p => !animalDefs.Contains(p.ThingDef)).ToList())
                copy.RemovePrecept(precept, replacing: true);
            foreach (var animal in animalDefs.OrderBy(d => d.defName, StringComparer.Ordinal))
            {
                if (copy.VeneratedAnimals.Contains(animal))
                    continue;
                if (!PreceptDefOf.AnimalVenerated.Worker.CanUse(animal, copy, Faction.OfPlayer.def).Accepted)
                    return Invalid("The game does not offer this animal for veneration.");
                if (!copy.foundation.CanAddForFaction(PreceptDefOf.AnimalVenerated, Faction.OfPlayer.def, null, checkDuplicates: false) ||
                    (reform && Find.FactionManager.AllFactions.Any(f => f.def.humanlikeFaction && f.ideos != null &&
                        (f.ideos.IsPrimary(source) || f.ideos.IsMinor(source)) && !copy.foundation.CanAddForFaction(PreceptDefOf.AnimalVenerated, f.def, null, checkDuplicates: false))))
                    return Invalid("Animal veneration conflicts with a faction.");
                // Setting ThingDef notifies the precept's ideo style tracker, so the precept joins the ideo first (vanilla's
                // precept editor sets the animal on a held precept); Init's own pick is replaced.
                var venerated = (Precept_Animal)PreceptMaker.MakePrecept(PreceptDefOf.AnimalVenerated);
                copy.AddPrecept(venerated, init: true, generatingFor: Faction.OfPlayer.def);
                venerated.ThingDef = animal;
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
            var player = Faction.OfPlayer;
            var source = player.ideos.PrimaryIdeo;
            // FactionIdeosTracker.ChooseOrGenerateIdeo can hand the player an ideo a world faction generated first (its
            // 20% reuse branch, #3591); the design would rewrite that faction's ideo too, so the player gets one of its own.
            if (Find.FactionManager.AllFactions.Any(f => f != player && f.ideos != null && f.ideos.Has(source)))
            {
                source = IdeoGenerator.GenerateIdeo(new IdeoGenerationParms(player.def));
                source.primaryFactionColor = player.Color;
                Find.IdeoManager.Add(source);
                player.ideos.SetPrimary(source);
            }
            var refusal = Prepare(source, design, false, out var candidate);
            if (refusal != null) throw new InvalidOperationException(refusal.Detail);
            candidate!.CopyTo(source);
        }
    }
}
