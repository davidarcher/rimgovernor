#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    /// <summary>Native loadout inspection and guarded ordinary apparel jobs.</summary>
    public sealed class GearUpkeepTools
    {
        internal static string Identity(Pawn p) => CensusMemo.Of(("identity", (object?)p), () => ComputeIdentity(p));

        private static string ComputeIdentity(Pawn p)
        {
            var parts = new List<string> { Current.Game.GetComponent<ColonyIdentity>().LoadToken, p.Map.uniqueID.ToString(),
                p.GetUniqueLoadID(), p.outfits?.CurrentApparelPolicy?.GetUniqueLoadID() ?? "none",
                p.equipment?.Primary?.GetUniqueLoadID() ?? "none" };
            var filter = p.outfits?.CurrentApparelPolicy?.filter;
            if (filter != null) {
                var policy = p.outfits!.CurrentApparelPolicy;
                parts.Add(CensusMemo.Of(("allowed", (object?)policy), () => string.Join(",", filter.AllowedThingDefs.Select(d => d.defName).OrderBy(d => d))));
                parts.Add(CensusMemo.Of(("special", (object?)policy), () => string.Join(",", DefDatabase<SpecialThingFilterDef>.AllDefs.Where(d => !filter.Allows(d)).Select(d => d.defName).OrderBy(d => d))));
                parts.Add(filter.AllowedHitPointsPercents.min.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                parts.Add(filter.AllowedHitPointsPercents.max.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                parts.Add(filter.AllowedQualityLevels.ToString());
            }
            if (p.apparel != null && p.outfits != null)
                parts.AddRange(p.apparel.WornApparel.OrderBy(a => a.thingIDNumber).Select(a =>
                    a.GetUniqueLoadID() + ":" + p.outfits.forcedHandler.AllowedToAutomaticallyDrop(a) + ":" + p.apparel.IsLocked(a)));
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(string.Join("|", parts)))).Replace("-", "");
        }

        internal static object Gear(Thing t) => new {
            thingId = t.GetUniqueLoadID(), defName = t.def.defName, label = t.Label, stuff = t.Stuff?.defName,
            hitPoints = t.HitPoints, maxHitPoints = t.MaxHitPoints,
            quality = t.TryGetQuality(out var quality) ? quality.ToString() : null,
            armorSharp = t.GetStatValue(StatDefOf.ArmorRating_Sharp),
            armorBlunt = t.GetStatValue(StatDefOf.ArmorRating_Blunt),
            insulationCold = t.GetStatValue(StatDefOf.Insulation_Cold),
            insulationHeat = t.GetStatValue(StatDefOf.Insulation_Heat)
        };

        internal static string? Available(Pawn p)
        {
            if (!p.IsFreeColonist || p.Dead || p.Downed || p.InMentalState || p.Drafted || p.IsQuestLodger())
                return "Pawn unavailable or player controlled";
            if (p.apparel == null || p.outfits?.CurrentApparelPolicy == null ||
                !p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) return "No apparel capability";
            if (p.IsMutant && p.mutant.Def.disableApparel) return "Mutant cannot use apparel";
            return null;
        }

        // Eligible is Screened (every check but pathing) then the pathing
        // check; the census scores between the two, so an item it discards
        // for low gain is never pathed to.
        internal static string? Eligible(Pawn p, Apparel a) => Screened(p, a) ?? (Reachable(p, a) ? null : "Cannot reserve or safely reach item");

        internal static bool Reachable(Pawn p, Apparel a) => p.CanReserveAndReach(a, PathEndMode.OnCell, p.NormalMaxDanger());

        internal static string? Screened(Pawn p, Apparel a)
        {
            if (!a.Spawned || a.Map != p.Map || a.Position.Fogged(p.Map) || a.IsForbidden(p) || a.IsBurning())
                return "Item unavailable or forbidden";
            if (a.Faction != null && !a.Faction.IsPlayer) return "Item belongs to another faction";
            if (!p.outfits.CurrentApparelPolicy.filter.Allows(a)) return "Apparel policy excludes item";
            if (!a.PawnCanWear(p) || !ApparelUtility.HasPartsToWear(p, a.def) ||
                !a.def.apparel.developmentalStageFilter.Has(p.DevelopmentalStage)) return "Body, age or definition incompatible";
            if (CompBiocodable.IsBiocoded(a) && !CompBiocodable.IsBiocodedFor(a, p)) return "Biocoded to another pawn";
            if (p.apparel.WornApparel.Any(w => !ApparelUtility.CanWearTogether(w.def, a.def, p.RaceProps.body) &&
                (!p.outfits.forcedHandler.AllowedToAutomaticallyDrop(w) || p.apparel.IsLocked(w)))) return "Forced or locked apparel would be displaced";
            return null;
        }

        private static readonly System.Reflection.FieldInfo? NeededWarmth = AccessTools.Field(typeof(JobGiver_OptimizeApparel), "neededWarmth");

        internal static float Gain(Pawn p, Apparel? a) => WithGainScorer(p, score => score(a));

        // Vanilla's scorer uses a static seasonal context: set once for the
        // pawn and restored even if a mod throws. The pawn's worn-apparel
        // scores are read once however many items are scored.
        internal static T WithGainScorer<T>(Pawn p, Func<Func<Apparel?, float>, T> use)
        {
            if (NeededWarmth == null) throw new InvalidOperationException("Native seasonal apparel scorer unavailable");
            var prior = NeededWarmth.GetValue(null);
            try {
                NeededWarmth.SetValue(null, PawnApparelGenerator.CalculateNeededWarmth(p, p.Map.TileInfo.tile, GenLocalDate.Twelfth(p)));
                var worn = p.apparel.WornApparel.Select(w => JobGiver_OptimizeApparel.ApparelScoreRaw(p, w)).ToList();
                return use(a => JobGiver_OptimizeApparel.ApparelScoreGain(p, a, worn));
            } finally { NeededWarmth.SetValue(null, prior); }
        }
    }
}
