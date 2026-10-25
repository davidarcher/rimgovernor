using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using PawnId = RimGovernor.Brain.Domain.PawnId;
using static RimGovernor.Native.NativePawnObservationTools;

namespace RimGovernor.Native
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

        private static RimGovernor.Brain.Domain.EquatableArray<RimGovernor.Brain.GameFacts.Observations.PolicyEntry> Rows<T>(List<T> policies, List<Pawn> pawns, Func<Pawn, Policy?> current,
            Func<T, IEnumerable<string>>? allowed = null, Func<T, IEnumerable<RimGovernor.Brain.GameFacts.Operations.DrugPolicyEntry>>? drugs = null) where T : Policy
        {
            var rows = new List<RimGovernor.Brain.GameFacts.Observations.PolicyEntry>();
            foreach (var policy in policies) {
                rows.Add(new RimGovernor.Brain.GameFacts.Observations.PolicyEntry {
                    Id = Id(policy.GetUniqueLoadID()),
                    Label = Text(policy.label ?? ""),
                    Pawns = [.. pawns.Where(p => current(p) == policy).Select(p => new PawnId(Id(p.GetUniqueLoadID())))],
                    AllowedDefs = [.. allowed?.Invoke(policy).Select(d => Id(d)) ?? Enumerable.Empty<string>()],
                    DrugEntries = [.. drugs?.Invoke(policy) ?? Enumerable.Empty<RimGovernor.Brain.GameFacts.Operations.DrugPolicyEntry>()],
                });
            }
            return RimGovernor.Brain.Domain.EquatableArray.From(rows);
        }

        // Every policy database, the colony map's allowed areas and its prisoner and animal food policy holders;
        // throws on anything it cannot read.
        internal static RimGovernor.Brain.GameFacts.Observations.Policies Read(Map map)
        {
            var game = Current.Game; var pawns = Owned();
            var eaters = pawns.Where(p => p.MapHeld == map && !p.Dead && p.foodRestriction != null && p.needs?.food != null && (p.IsPrisonerOfColony || p.RaceProps.Animal))
                .Select(p => new RimGovernor.Brain.GameFacts.Observations.FoodEater {
                    Pawn = new PawnId(Id(p.GetUniqueLoadID())),
                    Kind = p.RaceProps.Animal ? RimGovernor.Brain.GameFacts.Observations.FoodEaterKind.Animal : RimGovernor.Brain.GameFacts.Observations.FoodEaterKind.Prisoner,
                    // A prisoner's ideoligion precepts and traits; an animal has neither.
                    Precepts = [.. p.RaceProps.Animal || !ModsConfig.IdeologyActive || !(p.Ideo is Ideo ideo) ? Enumerable.Empty<string>()
                        : ideo.PreceptsListForReading.Select(x => Id(x.def.defName)).Distinct().OrderBy(d => d, StringComparer.Ordinal)],
                    Traits = [.. p.RaceProps.Animal ? Enumerable.Empty<RimGovernor.Brain.GameFacts.Observations.Trait>()
                        : (p.story?.traits?.allTraits ?? new List<Trait>()).OrderBy(t => t.def.defName, StringComparer.Ordinal).ThenBy(t => t.Degree).Select(t => new RimGovernor.Brain.GameFacts.Observations.Trait { DefName = Id(t.def.defName), Degree = t.Degree })],
                });
            var areas = map.areaManager.AllAreas.OfType<Area_Allowed>().Select(area => new RimGovernor.Brain.GameFacts.Observations.AllowedArea {
                Id = Id(area.GetUniqueLoadID()),
                Label = Text(area.Label ?? ""),
                Pawns = [.. pawns.Where(p => p.MapHeld == map && p.playerSettings?.AreaRestrictionInPawnCurrentMap == area).Select(p => new PawnId(Id(p.GetUniqueLoadID())))],
                CellCount = area.TrueCount,
            });
            return new RimGovernor.Brain.GameFacts.Observations.Policies {
                Outfit = Rows(game.outfitDatabase.AllOutfits, pawns, p => p.outfits?.CurrentApparelPolicy),
                Drug = Rows(game.drugPolicyDatabase.AllPolicies, pawns, p => p.drugs?.CurrentPolicy,
                    drugs: d => NativeDrugPolicy.Entries(d).Select(e => e with { DrugDef = Id(e.DrugDef) })),
                Food = Rows(game.foodRestrictionDatabase.AllFoodRestrictions, pawns, p => p.foodRestriction?.CurrentFoodPolicy, allowed: NativeFoodPolicy.Allowed),
                Reading = Rows(game.readingPolicyDatabase.AllReadingPolicies, pawns, p => p.reading?.CurrentPolicy, allowed: NativeReadingPolicy.Allowed),
                AllowedAreas = [.. areas],
                FoodEaters = [.. eaters],
            };
        }
        // The per-pawn policy planner inputs, read for every pawn: a pawn
        // without a tracker has none of its inputs.
        internal static RimGovernor.Brain.GameFacts.Observations.PawnPolicyInputs Inputs(Pawn pawn) => new RimGovernor.Brain.GameFacts.Observations.PawnPolicyInputs {
            ReadingPolicyId = pawn.reading?.CurrentPolicy is ReadingPolicy reading ? Id(reading.GetUniqueLoadID()) : null,
            InventoryStock = [.. (pawn.inventoryStock?.stockEntries ?? new Dictionary<InventoryStockGroupDef, InventoryStockEntry>())
                .OrderBy(e => e.Key.defName, StringComparer.Ordinal)
                .Select(e => new RimGovernor.Brain.GameFacts.Observations.InventoryStockSetting { Group = Id(e.Key.defName), Count = e.Value.count })],
            // Pawn.Ideo is the ideo tracker's ideo, so a member has a tracker.
            Ideo = ModsConfig.IdeologyActive && pawn.Ideo is Ideo ideo
                ? new RimGovernor.Brain.GameFacts.Observations.PawnIdeo.Member(Id(ideo.GetUniqueLoadID()),
                    [.. ideo.PreceptsListForReading.Select(p => Id(p.def.defName)).Distinct().OrderBy(d => d, StringComparer.Ordinal)],
                    ideo.GetRole(pawn) is Precept_Role role ? Id(role.def.defName) : null,
                    Number(pawn.ideo.Certainty))
                : new RimGovernor.Brain.GameFacts.Observations.PawnIdeo.None(),
            Guest = pawn.guest is Pawn_GuestTracker guest && pawn.HostFaction != null
                ? new RimGovernor.Brain.GameFacts.Observations.GuestState.Hosted((RimGovernor.Brain.GameFacts.Defs.GuestStatus)(int)guest.GuestStatus)
                : new RimGovernor.Brain.GameFacts.Observations.GuestState.Free(),
        };
    }
}
