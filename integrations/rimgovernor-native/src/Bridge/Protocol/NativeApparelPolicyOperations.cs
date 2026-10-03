#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    internal static class NativeApparelPolicyOperations
    {
        private static string Hash(string s) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", ""); }
        internal static string Signature(ApparelPolicy p) => Hash(p.label + "|" + string.Join(",", p.filter.AllowedThingDefs.Select(d => d.defName).OrderBy(d => d))
            + "|" + string.Join(",", DefDatabase<SpecialThingFilterDef>.AllDefs.Where(d => !p.filter.Allows(d)).Select(d => d.defName).OrderBy(d => d))
            + "|" + p.filter.AllowedHitPointsPercents.min.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            + "|" + p.filter.AllowedHitPointsPercents.max.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "|" + p.filter.AllowedQualityLevels);
        internal static string Token(Pawn p) => Hash(GearUpkeepTools.Identity(p) + "|" + string.Join(";",
            Current.Game.outfitDatabase.AllOutfits.OrderBy(o => o.id).Select(o => o.GetUniqueLoadID() + ":" + Signature(o))));
        internal static Obs.ApparelPolicyState Read(Pawn p)
        {
            var outfit = p.outfits?.CurrentApparelPolicy;
            var row = new Obs.ApparelPolicyState { Token = Token(p), Child = p.DevelopmentalStage.Child(), Slave = p.IsSlaveOfColony,
                IncapableOfViolence = p.WorkTagIsDisabled(WorkTags.Violent), PawnName = ShortName(p), Nude = Nude(p) };
            if (outfit != null) {
                row.Name = outfit.label; row.PolicyId = outfit.GetUniqueLoadID(); row.AllowedDefs.Add(outfit.filter.AllowedThingDefs.Where(d => d.IsApparel).Select(d => d.defName).OrderBy(d => d));
                row.MinHitPoints = outfit.filter.AllowedHitPointsPercents.min; row.MaxHitPoints = outfit.filter.AllowedHitPointsPercents.max;
                row.MinQuality = (int)outfit.filter.AllowedQualityLevels.min; row.MaxQuality = (int)outfit.filter.AllowedQualityLevels.max;
                row.ExcludesTainted = !outfit.filter.Allows(SpecialThingFilterDefOf.AllowDeadmansApparel) && outfit.filter.Allows(SpecialThingFilterDefOf.AllowNonDeadmansApparel);
            }
            var requirements = Requirements(p).ToList();
            var precepts = new HashSet<string>(ModsConfig.IdeologyActive && p.Ideo != null
                ? p.Ideo.PreceptsListForReading.OfType<Precept_Apparel>().Where(v => v.apparelDef != null).Select(v => v.apparelDef.defName) : Enumerable.Empty<string>());
            // What the pawn's title, role and precepts require, by def; Go keeps
            // those it can wear (the apparel rows and its wear inputs).
            foreach (var d in DefDatabase<ThingDef>.AllDefs.Where(d => d.IsApparel).OrderBy(d => d.defName))
                if (precepts.Contains(d.defName) || requirements.Any(r => r.ApparelMeetsRequirement(d, false))) row.RequiredDefs.Add(d.defName);
            row.Drafted = p.Drafted;
            if (p.workSettings != null) foreach (var d in DefDatabase<WorkTypeDef>.AllDefs) row.Work.Add(new Obs.WorkSetting { DefName = d.defName, Priority = p.workSettings.GetPriority(d), Disabled = p.WorkTypeIsDisabled(d) });
            if (p.skills != null) foreach (var s in p.skills.skills) row.Skills.Add(new Obs.Skill { DefName = s.def.defName, Level = s.Level, Passion = NativeEnums.Passion(s.passion), Disabled = s.TotallyDisabled });
            return row;
        }

        internal static string ShortName(Pawn p) => p.Name?.ToStringShort ?? p.LabelShort;

        // A nudist, or a pawn whose ideoligion prefers nudity for its gender
        // (the game's own PreceptDef.prefersNudity rule, no precept named).
        private static bool Nude(Pawn p) => p.story?.traits?.HasTrait(TraitDefOf.Nudist) == true
            || ModsConfig.IdeologyActive && p.Ideo != null && IdeoUtility.IdeoPrefersNudityForGender(p.Ideo, p.gender);

        // The apparel requirements of the pawn's royal title and ideoligion role.
        private static IEnumerable<ApparelRequirement> Requirements(Pawn p)
        {
            if (ModsConfig.RoyaltyActive && p.royalty?.MostSeniorTitle?.def.requiredApparel is List<ApparelRequirement> title)
                foreach (var r in title) yield return r;
            if (ModsConfig.IdeologyActive && p.Ideo?.GetRole(p)?.ApparelRequirements is List<PreceptApparelRequirement> role)
                foreach (var r in role) if (r.requirement != null) yield return r.requirement;
        }

        // The pawn's own outfit, found through its assignment (#1302): its
        // current outfit when no other pawn holds it, else an unheld outfit
        // already carrying its name, else a new one.
        private static ApparelPolicy Own(Pawn p, string name)
        {
            var others = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive.Where(v => v != p).Select(v => v.outfits?.CurrentApparelPolicy).Where(v => v != null).ToHashSet();
            var current = p.outfits.CurrentApparelPolicy;
            if (current != null && !others.Contains(current)) return current;
            return Current.Game.outfitDatabase.AllOutfits.FirstOrDefault(v => v.label == name && !others.Contains(v)) ?? Current.Game.outfitDatabase.MakeNewOutfit();
        }

        // Resolves the intent's pawn and checks the filter; null when it applies.
        internal static Common.Failure? Resolve(Operations.ApparelPolicyIntent? c, Common.ObservationContext context, out Pawn p)
        {
            p = null!;
            if (c == null || !c.HasPawnId || !ProtoBoundary.IsIdentifier(c.PawnId) || !c.HasName || string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 80
                || !c.HasMinHitPoints || !c.HasMaxHitPoints || float.IsNaN(c.MinHitPoints) || float.IsNaN(c.MaxHitPoints)
                || c.MinHitPoints < 0 || c.MaxHitPoints > 1 || c.MinHitPoints > c.MaxHitPoints
                || !c.HasMinQuality || !c.HasMaxQuality || c.MinQuality < 0 || c.MaxQuality > 6 || c.MinQuality > c.MaxQuality
                || c.AllowedDefs.Count == 0 || c.AllowedDefs.Distinct().Count() != c.AllowedDefs.Count
                || c.AllowedDefs.Any(d => DefDatabase<ThingDef>.GetNamedSilentFail(d)?.IsApparel != true))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "An apparel policy needs a pawn, a name, apparel definitions and valid bounds.");
            p = ProtoBoundary.LoadedMap(context)?.mapPawns.FreeColonistsSpawned.ById(c.PawnId)!;
            if (p == null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "The pawn is not a free colonist spawned on this map.");
            var unavailable = GearUpkeepTools.Available(p);
            if (unavailable != null) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The pawn cannot take an apparel policy: " + unavailable);
            return null;
        }

        private static bool Matches(Pawn p, Operations.ApparelPolicyIntent c)
        {
            var v = p.outfits?.CurrentApparelPolicy;
            return v != null && p.outfits != null && !PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive.Any(o => o != p && o.outfits?.CurrentApparelPolicy == v) && p.outfits.forcedHandler.ForcedApparel.Count == 0 && !p.apparel.AnyApparelLocked && v.label == c.Name && v.filter.AllowedThingDefs.Select(d => d.defName).OrderBy(d => d).SequenceEqual(c.AllowedDefs.OrderBy(d => d))
                && v.filter.AllowedHitPointsPercents.min == c.MinHitPoints && v.filter.AllowedHitPointsPercents.max == c.MaxHitPoints
                && (int)v.filter.AllowedQualityLevels.min == c.MinQuality && (int)v.filter.AllowedQualityLevels.max == c.MaxQuality
                && !v.filter.Allows(SpecialThingFilterDefOf.AllowDeadmansApparel) && v.filter.Allows(SpecialThingFilterDefOf.AllowNonDeadmansApparel);
        }

        // Writes the named filter and assignment unless they already match,
        // then reads them back.
        internal static Receipts.EffectEvidence Apply(Operations.ApparelPolicyIntent c, Common.ObservationContext context)
        {
            var failure = Resolve(c, context, out var p);
            if (failure != null) throw new InvalidOperationException("Apparel policy prerequisites changed before apply: " + failure.Detail);
            var before = Token(p);
            if (!Matches(p, c))
            {
                var outfit = Own(p, c.Name);
                outfit.label = c.Name; outfit.filter.SetDisallowAll();
                foreach (var d in c.AllowedDefs) outfit.filter.SetAllow(DefDatabase<ThingDef>.GetNamed(d), true);
                outfit.filter.AllowedHitPointsPercents = new FloatRange(c.MinHitPoints, c.MaxHitPoints);
                outfit.filter.AllowedQualityLevels = new QualityRange((QualityCategory)c.MinQuality, (QualityCategory)c.MaxQuality);
                outfit.filter.SetAllow(SpecialThingFilterDefOf.AllowNonDeadmansApparel, true);
                outfit.filter.SetAllow(SpecialThingFilterDefOf.AllowDeadmansApparel, false);
                p.outfits.CurrentApparelPolicy = outfit;
                p.outfits.forcedHandler.Reset(); p.apparel.UnlockAll();
                foreach (var pawn in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive.Where(v => v.outfits?.CurrentApparelPolicy == outfit)) pawn.mindState?.Notify_OutfitChanged();
                if (!Matches(p, c)) throw new InvalidOperationException("Apparel filter readback differs.");
            }
            return new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect { Snapshot = new Receipts.SnapshotEvidence { EntityId = p.GetUniqueLoadID(), BeforeToken = before, AfterToken = Token(p) } } };
        }
    }

    internal sealed class ApparelPolicyActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) =>
            NativeApparelPolicyOperations.Resolve(action.ApparelPolicy, context, out _);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) =>
            NativeApparelPolicyOperations.Apply(action.ApparelPolicy, context);
    }
}
