#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using PawnId = RimGovernor.Brain.Domain.PawnId;
using static HomeBridge.BridgeTools.NativePawnObservationTools;

namespace HomeBridge.BridgeTools
{
    // Policy databases and per-pawn policy inputs. Read-only: the
    // bot's per-pawn policy planners compose their writes from these.
    internal static class NativePolicyFacts
    {
        // Player pawns whose policies the bot owns: its colonists, slaves and
        // tame animals, and its prisoners, wherever they are.
        private static List<Pawn> Owned()
        {
            var player = Faction.OfPlayerSilentFail;
            return PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive
                .Where(p => player != null && (p.Faction == player || p.HostFaction == player))
                .OrderBy(p => p.thingIDNumber).ToList();
        }

        private static RimGovernor.Brain.Domain.EquatableArray<Obs.PolicyEntry> Rows<T>(List<T> policies, List<Pawn> pawns, Func<Pawn, Policy?> current,
            Func<T, IEnumerable<string>>? allowed = null, Func<T, IEnumerable<Operations.DrugPolicyEntry>>? drugs = null) where T : Policy
        {
            var rows = new List<Obs.PolicyEntry>();
            foreach (var policy in policies) {
                rows.Add(new Obs.PolicyEntry {
                    Id = Id(policy.GetUniqueLoadID()),
                    Label = Text(policy.label ?? ""),
                    Pawns = [.. pawns.Where(p => current(p) == policy).Select(p => new PawnId(Id(p.GetUniqueLoadID())))],
                    AllowedDefs = [.. allowed?.Invoke(policy).Select(d => Id(d)) ?? Enumerable.Empty<string>()],
                    DrugEntries = [.. drugs?.Invoke(policy) ?? Enumerable.Empty<Operations.DrugPolicyEntry>()],
                });
            }
            return RimGovernor.Brain.Domain.EquatableArray.From(rows);
        }

        // Every policy database, the colony map's allowed areas and its prisoner and animal food policy holders;
        // throws on anything it cannot read.
        internal static Obs.Policies Read(Map map)
        {
            var game = Current.Game; var pawns = Owned();
            var eaters = pawns.Where(p => p.MapHeld == map && !p.Dead && p.foodRestriction != null && p.needs?.food != null && (p.IsPrisonerOfColony || p.RaceProps.Animal))
                .Select(p => new Obs.FoodEater {
                    Pawn = new PawnId(Id(p.GetUniqueLoadID())),
                    Kind = p.RaceProps.Animal ? Obs.FoodEaterKind.Animal : Obs.FoodEaterKind.Prisoner,
                    // A prisoner's ideoligion precepts and traits; an animal has neither.
                    Precepts = [.. p.RaceProps.Animal || !ModsConfig.IdeologyActive || !(p.Ideo is Ideo ideo) ? Enumerable.Empty<string>()
                        : ideo.PreceptsListForReading.Select(x => Id(x.def.defName)).Distinct().OrderBy(d => d, StringComparer.Ordinal)],
                    Traits = [.. p.RaceProps.Animal ? Enumerable.Empty<Obs.Trait>()
                        : (p.story?.traits?.allTraits ?? new List<Trait>()).OrderBy(t => t.def.defName, StringComparer.Ordinal).ThenBy(t => t.Degree).Select(t => new Obs.Trait { DefName = Id(t.def.defName), Degree = t.Degree })],
                });
            var areas = map.areaManager.AllAreas.OfType<Area_Allowed>().Select(area => new Obs.AllowedArea {
                Id = Id(area.GetUniqueLoadID()),
                Label = Text(area.Label ?? ""),
                Pawns = [.. pawns.Where(p => p.MapHeld == map && p.playerSettings?.AreaRestrictionInPawnCurrentMap == area).Select(p => new PawnId(Id(p.GetUniqueLoadID())))],
                CellCount = area.TrueCount,
            });
            return new Obs.Policies {
                Outfit = Rows(game.outfitDatabase.AllOutfits, pawns, p => p.outfits?.CurrentApparelPolicy),
                Drug = Rows(game.drugPolicyDatabase.AllPolicies, pawns, p => p.drugs?.CurrentPolicy,
                    drugs: d => NativeDrugPolicy.Entries(d).Select(e => e with { DrugDef = Id(e.DrugDef) })),
                Food = Rows(game.foodRestrictionDatabase.AllFoodRestrictions, pawns, p => p.foodRestriction?.CurrentFoodPolicy, allowed: NativeFoodPolicy.Allowed),
                Reading = Rows(game.readingPolicyDatabase.AllReadingPolicies, pawns, p => p.reading?.CurrentPolicy, allowed: NativeReadingPolicy.Allowed),
                AllowedAreas = [.. areas],
                FoodEaters = [.. eaters],
            };
        }
        internal static Obs.PawnPolicyInputs Inputs(Pawn pawn)
        {
            var row = new Obs.PawnPolicyInputs();
            if (pawn.reading?.CurrentPolicy is ReadingPolicy reading) row = row with { ReadingPolicyId = Id(reading.GetUniqueLoadID()) };
            if (pawn.inventoryStock?.stockEntries is Dictionary<InventoryStockGroupDef, InventoryStockEntry> stock)
                foreach (var entry in stock.OrderBy(e => e.Key.defName, StringComparer.Ordinal))
                    row = row with { InventoryStock = [.. row.InventoryStock, new Obs.InventoryStockSetting { Group = Id(entry.Key.defName), Count = entry.Value.count }] };
            if (ModsConfig.IdeologyActive && pawn.Ideo is Ideo ideo) {
                row = row with { IdeoId = Id(ideo.GetUniqueLoadID()) };
                if (pawn.ideo != null) row = row with { IdeoCertainty = Number(pawn.ideo.Certainty) };
                row = row with { Precepts = [.. row.Precepts, .. ideo.PreceptsListForReading.Select(p => Id(p.def.defName)).Distinct().OrderBy(d => d, StringComparer.Ordinal)] };
                if (ideo.GetRole(pawn) is Precept_Role role) row = row with { IdeoRole = Id(role.def.defName) };
            }
            if (pawn.guest is Pawn_GuestTracker guest && pawn.HostFaction != null)
                row = row with { GuestStatus = (RimGovernor.Contracts.Wire.Defs.GuestStatus)(int)guest.GuestStatus };
            if (!pawn.WorkTypeIsDisabled(WorkTypeDefOf.Doctor))
                row = row with { MedicalTendQuality = Number(pawn.GetStatValue(StatDefOf.MedicalTendQuality)) };
            return row;
        }
    }
}
