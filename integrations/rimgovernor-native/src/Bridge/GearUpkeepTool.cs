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
            if (p.apparel != null)
                parts.AddRange(p.apparel.WornApparel.OrderBy(a => a.thingIDNumber).Select(a =>
                    a.GetUniqueLoadID() + ":" + p.outfits.forcedHandler.AllowedToAutomaticallyDrop(a) + ":" + p.apparel.IsLocked(a)));
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(string.Join("|", parts)))).Replace("-", "");
        }

        internal static object Gear(Thing t) => new {
            thingId = t.GetUniqueLoadID(), defName = t.def.defName, label = t.Label,
            hitPoints = t.HitPoints, maxHitPoints = t.MaxHitPoints,
            quality = t.TryGetQuality(out var quality) ? quality.ToString() : null,
            armorSharp = t.GetStatValue(StatDefOf.ArmorRating_Sharp),
            armorBlunt = t.GetStatValue(StatDefOf.ArmorRating_Blunt),
            insulationCold = t.GetStatValue(StatDefOf.Insulation_Cold),
            insulationHeat = t.GetStatValue(StatDefOf.Insulation_Heat)
        };

        internal static string Available(Pawn p)
        {
            if (!p.IsFreeColonist || p.Dead || p.Downed || p.InMentalState || p.Drafted || p.IsQuestLodger())
                return "Pawn unavailable or player controlled";
            if (p.apparel == null || p.outfits?.CurrentApparelPolicy == null ||
                !p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) return "No apparel capability";
            if (p.IsMutant && p.mutant.Def.disableApparel) return "Mutant cannot use apparel";
            if (p.CurJob?.playerForced == true) return "Preserving player ordered job";
            return null;
        }

        internal static string Eligible(Pawn p, Apparel a)
        {
            if (!a.Spawned || a.Map != p.Map || a.Position.Fogged(p.Map) || a.IsForbidden(p) || a.IsBurning())
                return "Item unavailable or forbidden";
            if (a.Faction != null && !a.Faction.IsPlayer) return "Item belongs to another faction";
            if (!ProductionPolicyGuard.Budgets(p.Map, p).TryGetValue(a.def.defName, out var budget) || budget < 1)
                return "Apparel stock is protected by resource policy or commitments";
            if (!p.outfits.CurrentApparelPolicy.filter.Allows(a)) return "Apparel policy excludes item";
            if (!a.PawnCanWear(p) || !ApparelUtility.HasPartsToWear(p, a.def) ||
                !a.def.apparel.developmentalStageFilter.Has(p.DevelopmentalStage)) return "Body, age or definition incompatible";
            if (CompBiocodable.IsBiocoded(a) && !CompBiocodable.IsBiocodedFor(a, p)) return "Biocoded to another pawn";
            if (!p.CanReserveAndReach(a, PathEndMode.OnCell, p.NormalMaxDanger())) return "Cannot reserve or safely reach item";
            if (p.apparel.WornApparel.Any(w => !ApparelUtility.CanWearTogether(w.def, a.def, p.RaceProps.body) &&
                (!p.outfits.forcedHandler.AllowedToAutomaticallyDrop(w) || p.apparel.IsLocked(w)))) return "Forced or locked apparel would be displaced";
            return null;
        }

        internal static float Gain(Pawn p, Apparel a)
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

        internal static string WeaponEligible(Pawn p, ThingWithComps weapon)
        {
            if (!weapon.def.IsWeapon || weapon.def.equipmentType != EquipmentType.Primary || weapon.GetComp<CompEquippable>() == null)
                return "Definition is not a primary weapon";
            if (p.WorkTagIsDisabled(WorkTags.Violent) || (weapon.def.IsRangedWeapon && p.WorkTagIsDisabled(WorkTags.Shooting)))
                return "Pawn cannot use this weapon";
            if (weapon.IsForbidden(p) || weapon.IsBurning() || weapon.Position.Fogged(p.Map)
                || (weapon.Faction != null && !weapon.Faction.IsPlayer)) return "Weapon unavailable or reserved";
            if (!p.CanReserveAndReach(weapon, PathEndMode.ClosestTouch, p.NormalMaxDanger())) return "Weapon not safely reachable";
            if (!EquipmentUtility.CanEquip(weapon, p, out var reason)) return reason ?? "Native weapon eligibility refused";
            if (!ProductionPolicyGuard.Budgets(p.Map, p).TryGetValue(weapon.def.defName, out var budget) || budget < 1)
                return "Weapon protected by resource policy or commitments";
            var primary = p.equipment?.Primary;
            if (primary != null) {
                if (!GearOwnership.State().Weapons.TryGetValue(p.GetUniqueLoadID(), out var owned) || primary.GetUniqueLoadID() != owned)
                    return "Preserving player weapon assignment";
                if (primary.def != weapon.def) return "Preserving assigned weapon type";
                if (!primary.def.useHitPoints || primary.HitPoints > primary.MaxHitPoints * .5f) return "Current weapon does not need replacement";
                if (primary.TryGetQuality(out var oldQuality) && (!weapon.TryGetQuality(out var quality) || quality < oldQuality))
                    return "Replacement would lower weapon quality";
            }
            if (weapon.def.useHitPoints && weapon.HitPoints < weapon.MaxHitPoints * .8f) return "Replacement weapon is too worn";
            return null;
        }

        internal static float WeaponGain(Pawn p, ThingWithComps weapon)
        {
            var skill = p.skills?.GetSkill(weapon.def.IsRangedWeapon ? SkillDefOf.Shooting : SkillDefOf.Melee)?.Level ?? 0;
            var quality = weapon.TryGetQuality(out var q) ? (int)q : 0;
            return (1f + skill) * (1f + quality) * (weapon.def.useHitPoints ? (float)weapon.HitPoints / weapon.MaxHitPoints : 1f);
        }

        internal static List<object> ProductionNeeds(Pawn p)
        {
            var needs = new List<object>();
            if (Available(p) != null) return needs;
            foreach (var a in p.apparel.WornApparel.Where(a => a.def.useHitPoints && a.HitPoints <= a.MaxHitPoints * .5f
                && p.outfits.forcedHandler.AllowedToAutomaticallyDrop(a) && !p.apparel.IsLocked(a)
                && p.outfits.CurrentApparelPolicy.filter.Allows(a.def)))
                needs.Add(new { defName = a.def.defName, stuff = a.Stuff?.defName, reason = "wear" });
            var primary = p.equipment?.Primary;
            if (primary != null && primary.def.useHitPoints && primary.HitPoints <= primary.MaxHitPoints * .5f
                && GearOwnership.State().Weapons.TryGetValue(p.GetUniqueLoadID(), out var owned) && primary.GetUniqueLoadID() == owned)
                needs.Add(new { defName = primary.def.defName, stuff = primary.Stuff?.defName, reason = "weapon wear" });
            var cold = p.AmbientTemperature < p.GetStatValue(StatDefOf.ComfyTemperatureMin);
            var hot = p.AmbientTemperature > p.GetStatValue(StatDefOf.ComfyTemperatureMax);
            if (cold || hot) {
                var stat = cold ? StatDefOf.Insulation_Cold : StatDefOf.Insulation_Heat;
                var budgets = ProductionPolicyGuard.Budgets(p.Map, p);
                var options = new List<Tuple<ThingDef, ThingDef, float>>();
                foreach (var def in p.outfits.CurrentApparelPolicy.filter.AllowedThingDefs.Where(d => d.IsApparel
                    && d.apparel.CorrectGenderForWearing(p.gender) && d.apparel.developmentalStageFilter.Has(p.DevelopmentalStage)
                    && ApparelUtility.HasPartsToWear(p, d))) {
                    var displaced = p.apparel.WornApparel.Where(a => !ApparelUtility.CanWearTogether(a.def, def, p.RaceProps.body)).ToList();
                    if (displaced.Any(a => !p.outfits.forcedHandler.AllowedToAutomaticallyDrop(a) || p.apparel.IsLocked(a))) continue;
                    var stuffs = def.MadeFromStuff ? GenStuff.AllowedStuffsFor(def).Where(s => budgets.TryGetValue(s.defName, out var n) && n > 0)
                        : new ThingDef[] { null };
                    foreach (var stuff in stuffs) {
                        var gain = def.GetStatValueAbstract(stat, stuff) - displaced.Sum(a => a.GetStatValue(stat));
                        if (gain > 1f) options.Add(Tuple.Create(def, stuff, gain));
                    }
                }
                needs.AddRange(options.OrderByDescending(o => o.Item3).ThenBy(o => o.Item1.defName)
                    .ThenBy(o => o.Item2?.defName).Take(8).Select(o => (object)new {
                        defName = o.Item1.defName, stuff = o.Item2?.defName, reason = cold ? "cold" : "heat" }));
            }
            return needs;
        }

        [Tool("home/gear_upkeep", Title = "Inspect or maintain gear",
            Description = "Inspect worn apparel, primary weapons and eligible replacements. Execution requires exact pawn, item and loadout signature; issues ordinary Wear or Equip work, preserves outfits, forced/locked apparel and player weapon assignments. A receipt is not a completed loadout.")]
        public async Task<object> Upkeep(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Exact current-map pawn Thing ID; omit for inspection of all colonists.")] string pawn = null,
            [ToolParameter(Description = "Exact observed replacement apparel or weapon Thing ID.")] string target = null,
            [ToolParameter(Description = "Loadout signature from inspection, required with target.")] string expectedLoadout = null,
            [ToolParameter(Description = "Inspect only.", DefaultValue = true)] bool dryRun = true)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => Run(pawn, target, expectedLoadout, dryRun), cancellationToken).ConfigureAwait(false);
        }

        internal static object Run(string pawn, string target, string expected, bool dryRun)
        {
            var map = Find.CurrentMap;
            if (map == null) return new { success = false, error = "No current map" };
            var people = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).ToList();
            if (target != null || !dryRun) {
                var p = people.SingleOrDefault(v => v.GetUniqueLoadID() == pawn);
                if (p == null || target == null || string.IsNullOrEmpty(expected)) return new { success = false, error = "Exact pawn, target and signature required" };
                if (!Find.TickManager.Paused) return new { success = false, error = "Pause before preparing apparel work" };
                if (Identity(p) != expected) return new { success = false, error = "Loadout or apparel assignment changed" };
                var weapon = map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon).OfType<ThingWithComps>().SingleOrDefault(v => v.GetUniqueLoadID() == target);
                if (weapon != null) {
                    var blocked = Available(p) ?? WeaponEligible(p, weapon);
                    if (blocked != null) return new { success = false, error = blocked };
                    if (dryRun) return new { success = true, pawn, target, expectedLoadout = expected };
                    var equip = JobMaker.MakeJob(JobDefOf.Equip, weapon);
                    p.jobs.StartJob(equip, JobCondition.InterruptForced);
                    if (p.CurJob == equip) GearOwnership.State().Weapons[pawn] = target;
                    return new { success = p.CurJob == equip, pawn, target, job = p.CurJob?.def.defName, outcome = "ordered" };
                }
                var a = map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel).OfType<Apparel>().SingleOrDefault(v => v.GetUniqueLoadID() == target);
                var refusal = Available(p) ?? (a == null ? "Apparel unavailable" : Eligible(p, a));
                if (refusal != null) return new { success = false, error = refusal };
                if (Gain(p, a) < .05f) return new { success = false, error = "No material native apparel improvement" };
                if (dryRun) return new { success = true, pawn, target, expectedLoadout = expected };
                var job = JobMaker.MakeJob(JobDefOf.Wear, a);
                // Autonomous ordinary work must not create a player forced outfit entry.
                p.jobs.StartJob(job, JobCondition.InterruptForced);
                return new { success = p.CurJob == job, pawn, target, job = p.CurJob?.def.defName, outcome = "ordered" };
            }
            var rows = new List<object>();
            foreach (var p in people.Where(p => pawn == null || p.GetUniqueLoadID() == pawn)) {
                var refusal = Available(p);
                var candidates = new List<object>();
                if (refusal == null)
                    foreach (var a in map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel).OfType<Apparel>().OrderBy(a => a.thingIDNumber)) {
                        if (Eligible(p, a) != null) continue;
                        var gain = Gain(p, a);
                        if (gain >= .05f) candidates.Add(new { target = a.GetUniqueLoadID(), kind = "apparel", gain, gear = Gear(a) });
                    }
                if (refusal == null)
                    foreach (var weapon in map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon).OfType<ThingWithComps>().OrderBy(w => w.thingIDNumber))
                        if (WeaponEligible(p, weapon) == null)
                            candidates.Add(new { target = weapon.GetUniqueLoadID(), kind = "weapon", gain = WeaponGain(p, weapon), gear = Gear(weapon) });
                rows.Add(new { pawn = p.GetUniqueLoadID(), loadout = Identity(p), blocker = refusal,
                    deficit = p.apparel != null && (p.apparel.WornApparel.Any(a => a.def.useHitPoints && a.HitPoints <= a.MaxHitPoints * .5f)
                        || p.AmbientTemperature < p.GetStatValue(StatDefOf.ComfyTemperatureMin)
                        || p.AmbientTemperature > p.GetStatValue(StatDefOf.ComfyTemperatureMax)
                        || (p.equipment?.Primary == null && !p.WorkTagIsDisabled(WorkTags.Violent))
                        || (p.equipment?.Primary != null && p.equipment.Primary.def.useHitPoints
                            && p.equipment.Primary.HitPoints <= p.equipment.Primary.MaxHitPoints * .5f)),
                    worn = p.apparel?.WornApparel.Select(a => new { gear = Gear(a),
                        forced = !p.outfits.forcedHandler.AllowedToAutomaticallyDrop(a), locked = p.apparel.IsLocked(a) }).ToList(),
                    primary = p.equipment?.Primary == null ? null : Gear(p.equipment.Primary),
                    comfortableMin = p.GetStatValue(StatDefOf.ComfyTemperatureMin),
                    comfortableMax = p.GetStatValue(StatDefOf.ComfyTemperatureMax),
                    replacementNeeds = ProductionNeeds(p), candidates });
            }
            return new { success = true, tick = Find.TickManager.TicksGame, mapId = map.uniqueID, pawns = rows };
        }
    }
}
