#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    /// <summary>Native loadout inspection and guarded ordinary apparel jobs.</summary>
    public sealed class GearUpkeepTools
    {
        internal static string Identity(Pawn p)
        {
            var parts = new List<string> { Current.Game.GetComponent<ColonyIdentity>().LoadToken, p.Map.uniqueID.ToString(),
                p.GetUniqueLoadID(), p.outfits?.CurrentApparelPolicy?.GetUniqueLoadID() ?? "none",
                p.equipment?.Primary?.GetUniqueLoadID() ?? "none" };
            var filter = p.outfits?.CurrentApparelPolicy?.filter;
            if (filter != null) {
                parts.Add(string.Join(",", filter.AllowedThingDefs.Select(d => d.defName).OrderBy(d => d)));
                parts.Add(string.Join(",", DefDatabase<SpecialThingFilterDef>.AllDefs.Where(d => !filter.Allows(d)).Select(d => d.defName).OrderBy(d => d)));
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

        internal static string? Eligible(Pawn p, Apparel a)
        {
            if (!a.Spawned || a.Map != p.Map || a.Position.Fogged(p.Map) || a.IsForbidden(p) || a.IsBurning())
                return "Item unavailable or forbidden";
            if (a.Faction != null && !a.Faction.IsPlayer) return "Item belongs to another faction";
            if (!p.outfits.CurrentApparelPolicy.filter.Allows(a)) return "Apparel policy excludes item";
            if (!a.PawnCanWear(p) || !ApparelUtility.HasPartsToWear(p, a.def) ||
                !a.def.apparel.developmentalStageFilter.Has(p.DevelopmentalStage)) return "Body, age or definition incompatible";
            if (CompBiocodable.IsBiocoded(a) && !CompBiocodable.IsBiocodedFor(a, p)) return "Biocoded to another pawn";
            if (!p.CanReserveAndReach(a, PathEndMode.OnCell, p.NormalMaxDanger())) return "Cannot reserve or safely reach item";
            if (p.apparel.WornApparel.Any(w => !ApparelUtility.CanWearTogether(w.def, a.def, p.RaceProps.body) &&
                (!p.outfits.forcedHandler.AllowedToAutomaticallyDrop(w) || p.apparel.IsLocked(w)))) return "Forced or locked apparel would be displaced";
            return null;
        }

        internal static float Gain(Pawn p, Apparel? a)
        {
            // Vanilla's scorer uses a static seasonal context. Restore it even if a mod throws.
            var field = AccessTools.Field(typeof(JobGiver_OptimizeApparel), "neededWarmth");
            if (field == null) throw new InvalidOperationException("Native seasonal apparel scorer unavailable");
            var prior = field.GetValue(null);
            try {
                field.SetValue(null, PawnApparelGenerator.CalculateNeededWarmth(p, p.Map.TileInfo.tile, GenLocalDate.Twelfth(p)));
                return JobGiver_OptimizeApparel.ApparelScoreGain(p, a,
                    p.apparel.WornApparel.Select(w => JobGiver_OptimizeApparel.ApparelScoreRaw(p, w)).ToList());
            } finally { field.SetValue(null, prior); }
        }

        internal sealed class ProductionNeed
        {
            public string? defName { get; set; }
            public string? stuff { get; set; }
            public string? reason { get; set; }
        }

        /// <summary>Body-part groups a dressed colonist covers; a pawn wearing nothing over one is undressed.</summary>
        internal static readonly BodyPartGroupDef[] CoreGroups = { BodyPartGroupDefOf.Torso, BodyPartGroupDefOf.Legs };

        internal static List<BodyPartGroupDef> Uncovered(Pawn p) => p.apparel == null ? new List<BodyPartGroupDef>()
            : CoreGroups.Where(g => !p.apparel.WornApparel.Any(a => a.def.apparel.bodyPartGroups.Contains(g))).ToList();

        internal static bool Deficit(Pawn p) => p.apparel != null &&
            (p.apparel.WornApparel.Any(a => a.def.useHitPoints && a.HitPoints <= a.MaxHitPoints * .5f)
             || Uncovered(p).Count > 0
             || p.AmbientTemperature < p.GetStatValue(StatDefOf.ComfyTemperatureMin)
             || p.AmbientTemperature > p.GetStatValue(StatDefOf.ComfyTemperatureMax)
             || (p.equipment?.Primary == null && !p.WorkTagIsDisabled(WorkTags.Violent))
             || (p.equipment?.Primary != null && p.equipment.Primary.def.useHitPoints
                 && p.equipment.Primary.HitPoints <= p.equipment.Primary.MaxHitPoints * .5f));

        internal static List<ProductionNeed> ProductionNeeds(Pawn p)
        {
            var needs = new List<ProductionNeed>();
            if (Available(p) != null) return needs;
            foreach (var a in p.apparel.WornApparel.Where(a => a.def.useHitPoints && a.HitPoints <= a.MaxHitPoints * .5f
                && p.outfits.forcedHandler.AllowedToAutomaticallyDrop(a) && !p.apparel.IsLocked(a)
                && p.outfits.CurrentApparelPolicy.filter.Allows(a.def)))
                needs.Add(new ProductionNeed { defName = a.def.defName, stuff = a.Stuff?.defName, reason = "wear" });
            var primary = p.equipment?.Primary;
            if (primary != null && primary.def.useHitPoints && primary.HitPoints <= primary.MaxHitPoints * .5f)
                needs.Add(new ProductionNeed { defName = primary.def.defName, stuff = primary.Stuff?.defName, reason = "weapon wear" });
            var cold = p.AmbientTemperature < p.GetStatValue(StatDefOf.ComfyTemperatureMin);
            var hot = p.AmbientTemperature > p.GetStatValue(StatDefOf.ComfyTemperatureMax);
            var uncovered = Uncovered(p);
            if (!cold && !hot && uncovered.Count == 0) return needs;
            // Definition-level candidates: allowed, wearable, displacing no forced or locked garment,
            // one per stocked stuff (Go's material budget funds the bill, #1354), ranked by the insulation stat the deficit names.
            List<Tuple<ThingDef, ThingDef?, float>> Options(StatDef stat, Func<ThingDef, bool> covers)
            {
                var options = new List<Tuple<ThingDef, ThingDef?, float>>();
                foreach (var def in p.outfits.CurrentApparelPolicy.filter.AllowedThingDefs.Where(d => d.IsApparel && covers(d)
                    && d.apparel.CorrectGenderForWearing(p.gender) && d.apparel.developmentalStageFilter.Has(p.DevelopmentalStage)
                    && ApparelUtility.HasPartsToWear(p, d))) {
                    var displaced = p.apparel.WornApparel.Where(a => !ApparelUtility.CanWearTogether(a.def, def, p.RaceProps.body)).ToList();
                    if (displaced.Any(a => !p.outfits.forcedHandler.AllowedToAutomaticallyDrop(a) || p.apparel.IsLocked(a))) continue;
                    var stuffs = def.MadeFromStuff ? GenStuff.AllowedStuffsFor(def).Where(s => p.Map.resourceCounter.GetCount(s) > 0).Cast<ThingDef?>()
                        : new ThingDef?[] { null };
                    foreach (var stuff in stuffs)
                        options.Add(Tuple.Create(def, stuff, def.GetStatValueAbstract(stat, stuff) - displaced.Sum(a => a.GetStatValue(stat))));
                }
                return options.OrderByDescending(o => o.Item3).ThenBy(o => o.Item1.defName).ThenBy(o => o.Item2?.defName).ToList();
            }
            if (cold || hot) {
                var stat = cold ? StatDefOf.Insulation_Cold : StatDefOf.Insulation_Heat;
                needs.AddRange(Options(stat, _ => true).Where(o => o.Item3 > 1f).Take(8).Select(o => new ProductionNeed {
                    defName = o.Item1.defName, stuff = o.Item2?.defName, reason = cold ? "cold" : "heat" }));
            }
            // An uncovered core group is a deficit in any weather; the warmest budgeted garments covering it
            // are the candidates, ahead of the first winter (issue #233).
            foreach (var group in uncovered)
                needs.AddRange(Options(StatDefOf.Insulation_Cold, d => d.apparel.bodyPartGroups.Contains(group)).Take(4)
                    .Select(o => new ProductionNeed { defName = o.Item1.defName, stuff = o.Item2?.defName, reason = "missing" })
                    .Where(n => !needs.Any(e => e.defName == n.defName && e.stuff == n.stuff && e.reason == n.reason)).ToList());
            return needs;
        }
    }
}
