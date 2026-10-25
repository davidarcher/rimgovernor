using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using PawnId = RimGovernor.Brain.Domain.PawnId;
using static RimGovernor.Native.NativePawnObservationTools;

namespace RimGovernor.Native
{
    internal static class NativeGearFacts
    {
        internal static RimGovernor.Brain.GameFacts.Observations.GearSnapshot Read(Map map)
        {
            long mark = System.Diagnostics.Stopwatch.GetTimestamp(), screenTicks = 0, scoreTicks = 0, reachTicks = 0, availTicks = 0, identTicks = 0, policyTicks = 0, modelTicks = 0, rowTicks = 0;
            long Lap() { var now = System.Diagnostics.Stopwatch.GetTimestamp(); var spent = now - mark; mark = now; return spent; }
            using var memo = new CensusMemo();
            var people = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).ToList();
            var climate = ReadClimate(map);
            var catalog = Catalog();
            var apparelOnMap = map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel).OfType<Apparel>().ToList();
            var byId = new Dictionary<string, Apparel>();
            foreach (var apparel in apparelOnMap) byId[apparel.GetUniqueLoadID()] = apparel;
            var billOptions = new Dictionary<RecipeDef, IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.GearLoadoutOption>>();
            // A qualityless garment groups under -1, apart from every QualityCategory.
            var stored = map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel).OfType<Apparel>()
                .Where(a => a.IsInValidStorage() && !a.IsForbidden(Faction.OfPlayer) && !a.WornByCorpse
                    && (!a.def.useHitPoints || (float)a.HitPoints / a.MaxHitPoints > .5f))
                .GroupBy(a => new { Def = a.def.defName, Stuff = a.Stuff?.defName ?? "",
                    Quality = a.TryGetQuality(out var q) ? (int)q : -1,
                    Band = a.def.useHitPoints ? Math.Min(9, (int)(10f * a.HitPoints / a.MaxHitPoints)) : 9 })
                .OrderBy(g => g.Key.Def).ThenBy(g => g.Key.Stuff).ThenBy(g => g.Key.Quality).ThenBy(g => g.Key.Band).ToList();
            var storedRows = new List<RimGovernor.Brain.GameFacts.Observations.GearStock>();
            foreach (var group in stored)
                storedRows.Add(new RimGovernor.Brain.GameFacts.Observations.GearStock { DefName = group.Key.Def, Stuff = group.Key.Stuff.Length == 0 ? null : group.Key.Stuff,
                    Quality = group.Key.Quality < 0 ? new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Qualityless() : new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Graded((RimGovernor.Brain.GameFacts.Defs.QualityCategory)group.Key.Quality),
                    HpBand = group.Key.Band, Count = group.Sum(a => a.stackCount) });
            ObservationWork.Detail("cf.gear.stored", Lap());
            var rows = new List<RimGovernor.Brain.GameFacts.Observations.GearLoadout>();
            // The census carries each candidate's supply token the way the
            // supply census does; the WEAR pawn order
            // does not check it.
            foreach (var pawn in people) {
                Lap();
                var refusal = GearUpkeepTools.Available(pawn); availTicks += Lap();
                var token = GearUpkeepTools.Identity(pawn); identTicks += Lap();
                var policy = pawn.outfits?.CurrentApparelPolicy == null ? null : NativeApparelPolicyOperations.Read(pawn); policyTicks += Lap();
                var groups = PresentGroups(pawn);
                rowTicks += Lap();
                var candidates = new List<RimGovernor.Brain.GameFacts.Observations.GearCandidate>();
                if (refusal == null) {
                    // Every eligible loose item is offered, best gain first
                    // (truncation biases every pawn toward the same few shirts).
                    // Candidates are apparel only: they feed the wear order
                    // (NativeGearOperations, JobDefOf.Wear), which looks its
                    // target up among loose apparel. Loose weapons are the
                    // equip family's (PAWN_ORDER_KIND_EQUIP); listing them
                    // here had a WoodLog admitted as apparel wear and refused
                    // on every attempt.
                    // Pathing is the costly check, so it runs only for an
                    // item whose gain already qualifies.
                    var scored = GearUpkeepTools.WithGainScorer(pawn, score => {
                        var found = new List<KeyValuePair<Thing, float>>();
                        foreach (var apparel in apparelOnMap) {
                            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                            var screened = GearUpkeepTools.Screened(pawn, apparel) != null;
                            var t1 = System.Diagnostics.Stopwatch.GetTimestamp(); screenTicks += t1 - t0;
                            if (screened) continue;
                            var gain = score(apparel);
                            var t2 = System.Diagnostics.Stopwatch.GetTimestamp(); scoreTicks += t2 - t1;
                            if (gain >= .05f && GearUpkeepTools.Reachable(pawn, apparel)) found.Add(new KeyValuePair<Thing, float>(apparel, gain));
                            reachTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t2;
                        }
                        return found;
                    });
                    foreach (var candidate in scored.OrderByDescending(c => c.Value).ThenBy(c => c.Key.thingIDNumber))
                        candidates.Add(new RimGovernor.Brain.GameFacts.Observations.GearCandidate { Item = Gear(candidate.Key), Gain = Number(candidate.Value) });
                }
                Lap();
                var model = Model(pawn, candidates, catalog, byId, billOptions, people);
                modelTicks += Lap();
                rows.Add(new RimGovernor.Brain.GameFacts.Observations.GearLoadout {
                    Token = token,
                    Pawn = new PawnId(Id(pawn.GetUniqueLoadID())),
                    Candidates = [.. candidates],
                    Blocked = refusal != null,
                    ApparelPolicy = policy,
                    LoadoutModel = model,
                    // The wear inputs the apparel rows cannot say: the brain applies the
                    // wear filter (gender, stage, body part groups) from the rows.
                    Gender = (RimGovernor.Brain.GameFacts.Defs.Gender)(int)pawn.gender,
                    DevelopmentalStage = (RimGovernor.Brain.GameFacts.Defs.DevelopmentalStage)(int)pawn.DevelopmentalStage,
                    BodyPartGroups = [.. groups]
                });
            }
            var result = new RimGovernor.Brain.GameFacts.Observations.GearSnapshot { Pawns = [.. rows], Climate = climate, StoredApparel = [.. storedRows],
                LockedBills = [.. LockedBills(catalog, people)] };
            ObservationWork.Detail("cf.gear.rows", rowTicks);
            ObservationWork.Detail("cf.gear.available", availTicks);
            ObservationWork.Detail("cf.gear.identity", identTicks);
            ObservationWork.Detail("cf.gear.policy", policyTicks);
            ObservationWork.Detail("cf.gear.screen", screenTicks);
            ObservationWork.Detail("cf.gear.score", scoreTicks);
            ObservationWork.Detail("cf.gear.reach", reachTicks);
            ObservationWork.Detail("cf.gear.model", modelTicks);
            return result;
        }

        // The body part groups the pawn still has a part in (a part that is not missing).
        private static List<string> PresentGroups(Pawn pawn)
        {
            var parts = pawn.health.hediffSet.GetNotMissingParts().ToList();
            return DefDatabase<BodyPartGroupDef>.AllDefsListForReading.Where(g => parts.Any(part => part.IsInGroup(g)))
                .Select(g => Id(g.defName)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        }

        // Producible apparel: one recipe per definition whose research is
        // finished, in stable definition order.
        private static List<RecipeDef> Catalog() => ApparelRecipes(true);

        private static List<RecipeDef> ApparelRecipes(bool available) => DefDatabase<RecipeDef>.AllDefsListForReading
            .Where(r => r.ProducedThingDef != null && r.ProducedThingDef.IsApparel && r.AvailableNow == available)
            .GroupBy(r => r.ProducedThingDef).Select(g => g.OrderBy(r => r.defName, StringComparer.Ordinal).First())
            .OrderBy(r => r.ProducedThingDef.defName, StringComparer.Ordinal).ToList();

        // The apparel defs no available recipe makes: who could craft each once its recipe is available (#3850), for the bills
        // the brain offers ahead of their research.
        private static IEnumerable<RimGovernor.Brain.GameFacts.Observations.GearLockedBill> LockedBills(List<RecipeDef> available, IReadOnlyList<Pawn> people)
        {
            var made = new HashSet<ThingDef>(available.Select(r => r.ProducedThingDef));
            foreach (var recipe in ApparelRecipes(false).Where(r => !made.Contains(r.ProducedThingDef))) {
                var (craftable, crafters) = Crafters(recipe, people);
                yield return new RimGovernor.Brain.GameFacts.Observations.GearLockedBill { DefName = Id(recipe.ProducedThingDef.defName), Craftable = craftable, Crafters = [.. crafters] };
            }
        }

        // Whether a free colonist can work the recipe, and the able colonists' quality facts for a def with a quality.
        // GenRecipe.PostProcessProduct sets a quality product's quality from the recipe's workSkill: without one the game cannot make it.
        private static (bool Craftable, RimGovernor.Brain.GameFacts.Observations.GearCrafter[] Crafters) Crafters(RecipeDef recipe, IReadOnlyList<Pawn> people)
        {
            var benches = recipe.AllRecipeUsers.ToList();
            var givers = DefDatabase<WorkGiverDef>.AllDefsListForReading.Where(g => g.Worker is WorkGiver_DoBill && g.fixedBillGiverDefs != null
                && g.fixedBillGiverDefs.Any(benches.Contains)).ToList();
            var able = people.Where(p => CanCraft(recipe, givers, p)).ToList();
            var quality = recipe.ProducedThingDef.HasComp<CompQuality>();
            if (able.Count == 0 || quality && recipe.workSkill == null) return (false, Array.Empty<RimGovernor.Brain.GameFacts.Observations.GearCrafter>());
            return (true, quality ? able.Select(p => Crafter(recipe, p)).Distinct().ToArray() : Array.Empty<RimGovernor.Brain.GameFacts.Observations.GearCrafter>());
        }

        private static RimGovernor.Brain.GameFacts.Observations.GearLoadoutModel Model(Pawn pawn, List<RimGovernor.Brain.GameFacts.Observations.GearCandidate> candidates, List<RecipeDef> catalog,
            Dictionary<string, Apparel> things, Dictionary<RecipeDef, IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.GearLoadoutOption>> billOptions, IReadOnlyList<Pawn> people)
        {
            var worn = new List<RimGovernor.Brain.GameFacts.Observations.GearLoadoutOption>();
            if (pawn.apparel != null)
                foreach (var garment in pawn.apparel.WornApparel)
                    worn.Add(Physical(garment, RimGovernor.Brain.GameFacts.Observations.GearSource.Worn,
                        pawn.apparel.IsLocked(garment) || pawn.outfits?.forcedHandler.AllowedToAutomaticallyDrop(garment) == false));
            var options = new List<RimGovernor.Brain.GameFacts.Observations.GearLoadoutOption>();
            // Every candidate is apparel on the map (the census screens only those).
            foreach (var candidate in candidates) {
                var apparel = things[candidate.Item.Thing.Id];
                options.Add(Physical(apparel, apparel.IsInValidStorage() ? RimGovernor.Brain.GameFacts.Observations.GearSource.Stored : RimGovernor.Brain.GameFacts.Observations.GearSource.Loose, false));
            }
            foreach (var recipe in catalog) {
                var def = recipe.ProducedThingDef;
                if (!def.apparel.CorrectGenderForWearing(pawn.gender) || !def.apparel.developmentalStageFilter.Has(pawn.DevelopmentalStage)
                    || !ApparelUtility.HasPartsToWear(pawn, def)) continue;
                // A bill option does not depend on the pawn: built once per census.
                if (!billOptions.TryGetValue(recipe, out var bills)) billOptions[recipe] = bills = BillOptions(recipe, people);
                options.AddRange(bills);
            }
            return new RimGovernor.Brain.GameFacts.Observations.GearLoadoutModel { Worn = [.. worn], Options = [.. options] };
        }

        // Expose legal material candidates; the brain owns selection and its search bound. A recipe no free colonist can work
        // offers no bill, and a crafted garment with a quality carries its crafters' quality facts (#3850).
        private static IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.GearLoadoutOption> BillOptions(RecipeDef recipe, IReadOnlyList<Pawn> people)
        {
            var def = recipe.ProducedThingDef;
            var (craftable, crafters) = Crafters(recipe, people);
            if (!craftable) return Array.Empty<RimGovernor.Brain.GameFacts.Observations.GearLoadoutOption>();
            // A bill is at Normal quality, or without one for a def with no CompQuality (#3841).
            RimGovernor.Brain.GameFacts.Observations.GearLoadoutOption Bill(ThingDef? stuff) => new RimGovernor.Brain.GameFacts.Observations.GearLoadoutOption {
                Id = Id("bill:" + def.defName + "/" + (stuff?.defName ?? "")), DefName = Id(def.defName), Stuff = stuff == null ? null : Id(stuff.defName),
                Quality = def.HasComp<CompQuality>() ? new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Graded(RimGovernor.Brain.GameFacts.Defs.QualityCategory.Normal) : new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Qualityless(),
                Source = RimGovernor.Brain.GameFacts.Observations.GearSource.Bill, Condition = 1, Tainted = false, Locked = false, Crafters = [.. crafters] };
            if (!def.MadeFromStuff) return new[] { Bill(null) };
            return GenStuff.AllowedStuffsFor(def).Where(s => recipe.fixedIngredientFilter.Allows(s))
                .OrderBy(s => s.defName, StringComparer.Ordinal).Select(Bill).ToArray();
        }

        // A free colonist who can work the recipe: one of the WorkGiver_DoBill givers of its benches whose work type and tags the
        // pawn can do and whose capacities it has (as the tailoring read's giver), and the recipe's skill requirements.
        private static bool CanCraft(RecipeDef recipe, List<WorkGiverDef> givers, Pawn pawn) =>
            recipe.FirstSkillRequirementPawnDoesntSatisfy(pawn) == null
            && givers.Any(g => !pawn.WorkTypeIsDisabled(g.workType) && !pawn.WorkTagIsDisabled(g.workTags)
                && ((WorkGiver_DoBill)g.Worker).MissingRequiredCapacity(pawn) == null);

        // What QualityUtility.GenerateQualityCreatedByPawn reads of the crafter: the recipe's work skill level, Inspired_Creativity,
        // and the ideoligion role's first RoleEffect_ProductionQualityOffset under Ideology.
        private static RimGovernor.Brain.GameFacts.Observations.GearCrafter Crafter(RecipeDef recipe, Pawn pawn)
        {
            var offset = 0;
            if (ModsConfig.IdeologyActive && pawn.Ideo?.GetRole(pawn)?.def.roleEffects?.FirstOrDefault(e => e is RoleEffect_ProductionQualityOffset)
                is RoleEffect_ProductionQualityOffset effect) offset = effect.offset;
            return new(pawn.skills.GetSkill(recipe.workSkill).Level, pawn.InspirationDef == InspirationDefOf.Inspired_Creativity, offset);
        }

        // The instance facts of one item option (#3841): its quality, or none for a def with no CompQuality, and its hit points
        // (1 for a def without them); layers, groups and the def x stuff stats are the catalog's.
        private static RimGovernor.Brain.GameFacts.Observations.GearLoadoutOption Physical(Apparel apparel, RimGovernor.Brain.GameFacts.Observations.GearSource source, bool locked) => new RimGovernor.Brain.GameFacts.Observations.GearLoadoutOption {
            Id = Id(apparel.GetUniqueLoadID()), DefName = Id(apparel.def.defName), Stuff = apparel.Stuff == null ? null : Id(apparel.Stuff.defName),
            Quality = Quality(apparel), Source = source,
            Condition = apparel.def.useHitPoints ? Number(Math.Min(1.0, (double)apparel.HitPoints / apparel.MaxHitPoints)) : 1,
            Tainted = apparel.WornByCorpse, Locked = locked, Crafters = [] };

        private static RimGovernor.Brain.GameFacts.Observations.ItemQuality Quality(Thing thing) => thing.TryGetQuality(out var quality)
            ? new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Graded((RimGovernor.Brain.GameFacts.Defs.QualityCategory)(int)quality) : new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Qualityless();

        /// <summary>
        /// The map's local-calendar twelfth now, the ticks to the next one and the local offset from longitude: the year the
        /// climate read and the outdoor recreation shares (<see cref="NativeRecreation.OutdoorClimate"/>) are indexed by.
        /// </summary>
        internal static (int Current, int TicksToNext, long Offset) TwelfthNow(Map map)
        {
            var longitude = Find.WorldGrid.LongLatOf(map.Tile).x;
            var offset = GenDate.LocalTicksOffsetFromLongitude(longitude);
            var localTicks = (long)GenTicks.TicksAbs + offset;
            return ((int)GenDate.Twelfth(GenTicks.TicksAbs, longitude), GenDate.TicksPerTwelfth
                - (int)((localTicks % GenDate.TicksPerTwelfth + GenDate.TicksPerTwelfth) % GenDate.TicksPerTwelfth), offset);
        }

        private static RimGovernor.Brain.GameFacts.Observations.GearClimate ReadClimate(Map map)
        {
            var (current, toNext, offset) = TwelfthNow(map);
            var temperatures = new List<double>();
            var daylight = new List<double>();
            // Sample the middle of each local-calendar twelfth. Seasonal
            // temperature excludes weather and indoor heating.
            for (var twelfth = 0; twelfth < GenDate.TwelfthsPerYear; twelfth++) {
                var sampleTick = (int)(twelfth * GenDate.TicksPerTwelfth + GenDate.TicksPerTwelfth / 2 - offset);
                temperatures.Add(Number(GenTemperature.GetTemperatureFromSeasonAtTile(sampleTick, map.Tile)));
                // Daylight is the same sampled day's hours the game counts as daytime at the tile.
                daylight.Add(Number(DaylightHours.Of(offsetTicks => GenCelestial.CelestialSunGlow(map.Tile, sampleTick + offsetTicks), GenCelestial.IsDaytime)));
            }
            return new RimGovernor.Brain.GameFacts.Observations.GearClimate {
                Temperatures = [.. temperatures],
                DaylightHours = [.. daylight],
                CurrentTwelfth = current,
                TicksToNextTwelfth = toNext
            };
        }

        // The pawn's equipment and worn apparel, shared by the loadout upkeep and the pawn detail reads. A pawn
        // without an equipment or apparel tracker (an animal) carries neither and is unarmed, with no ranged damage. The
        // damage per second is the game's own (NativeDefenseStats); the hit factor is the pawn detail read's
        // (NativePawnDetails.Equipment): the loadout census leaves it 0.
        internal static RimGovernor.Brain.GameFacts.Observations.PawnEquipment Equipment(Pawn pawn)
        {
            var equipped = new List<RimGovernor.Brain.GameFacts.Observations.GearItem>();
            var worn = new List<RimGovernor.Brain.GameFacts.Observations.GearItem>();
            var primary = pawn.equipment?.Primary;
            if (pawn.equipment != null)
                foreach (var thing in pawn.equipment.AllEquipmentListForReading) equipped.Add(Gear(thing));
            if (pawn.apparel != null) {
                var forced = pawn.outfits?.forcedHandler;
                foreach (var apparel in pawn.apparel.WornApparel)
                    worn.Add(Gear(apparel) with { Forced = forced != null && !forced.AllowedToAutomaticallyDrop(apparel), Locked = pawn.apparel.IsLocked(apparel) });
            }
            return new RimGovernor.Brain.GameFacts.Observations.PawnEquipment { PrimaryId = primary == null ? null : Id(primary.GetUniqueLoadID()), Equipped = [.. equipped],
                Apparel = [.. worn], Armed = primary != null, RangedHitFactor = 0,
                RangedDps = NativeDefenseStats.PawnRangedDps(pawn), MeleeDps = pawn.GetStatValue(StatDefOf.MeleeDPS) };
        }

        // Planning census, not simulated damage: natural armor or strongest worn layer.
        internal static RimGovernor.Brain.GameFacts.Observations.RaidArmor RaidArmor(Map map)
        {
            var hostiles = map.mapPawns.AllPawnsSpawned.Where(p => !p.Dead && !p.Downed && p.HostileTo(Faction.OfPlayer));
            var total = 0.0;
            var count = 0;
            foreach (var hostile in hostiles) {
                total += PawnArmor(hostile);
                count++;
            }
            return count == 0 ? new RimGovernor.Brain.GameFacts.Observations.RaidArmor.NoHostiles() : new RimGovernor.Brain.GameFacts.Observations.RaidArmor.Rated(Number(total / count));
        }

        // One pawn's sharp armor: natural armor or its strongest worn layer.
        internal static double PawnArmor(Pawn pawn)
        {
            var armor = Number(pawn.GetStatValue(StatDefOf.ArmorRating_Sharp));
            if (pawn.apparel != null)
                foreach (var apparel in pawn.apparel.WornApparel)
                    armor = Math.Max(armor, Number(apparel.GetStatValue(StatDefOf.ArmorRating_Sharp)));
            return armor;
        }

        // One gear item: its biocode, quality and hit points (1 for a def without them). It is neither forced nor locked:
        // worn apparel's are the equipment read's.
        internal static RimGovernor.Brain.GameFacts.Observations.GearItem Gear(Thing thing)
        {
            if (thing.def.useHitPoints && (thing.MaxHitPoints <= 0 || thing.HitPoints < 0)) throw new InvalidOperationException("Invalid native hit points.");
            var comp = thing.TryGetComp<CompBiocodable>();
            var biocoded = comp?.Biocoded == true;
            return new RimGovernor.Brain.GameFacts.Observations.GearItem {
                Thing = NativeRef.Thing(thing),
                Quality = Quality(thing),
                ConditionFraction = thing.def.useHitPoints ? Number((double)thing.HitPoints / thing.MaxHitPoints) : 1,
                Forced = false,
                Locked = false,
                Biocoded = biocoded,
                BiocodedTo = biocoded && comp?.CodedPawn != null ? Id(comp.CodedPawn.GetUniqueLoadID()) : null
            };
        }
    }
}
