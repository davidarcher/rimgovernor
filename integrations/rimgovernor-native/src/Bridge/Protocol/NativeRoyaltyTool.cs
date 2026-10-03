#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI.Group;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // The royalty read (#1599): the title ladder, the permit catalog and each
    // colonist's holdings. It changes on quest completion or a title change,
    // so the controller reads it on its own slow cadence, not with the pawn rows.
    public sealed class NativeRoyaltyTool
    {
        internal const string ToolName = "rimgovernor/observations_read_royalty_facts";

        [Tool(ToolName, Title = "Read royalty facts", Description = "Royal title ladder, permit catalog and each colonist's held titles, favor and taken permits. Not applicable without Royalty. Read-only.")]
        [ToolResponse("payload", "string", "Official RoyaltyFactsReply ProtoJSON.", Always = true)]
        public async Task<object> ReadRoyaltyFacts(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw RoyaltyFactsRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request!, Obs.RoyaltyFactsRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Obs.RoyaltyFactsReply { Failure = failure });
            if (parsed.Scope?.ExpectedIdentity == null)
                return ProtoBoundary.Encode(new Obs.RoyaltyFactsReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Expected identity required.") });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope.ExpectedIdentity, out _, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.RoyaltyFactsReply { Failure = error });
                if (!ModsConfig.RoyaltyActive)
                    return ProtoBoundary.Encode(new Obs.RoyaltyFactsReply { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.NotApplicable, Detail = "Royalty is not active." } });
                try { return ProtoBoundary.Encode(new Obs.RoyaltyFactsReply { Observed = Read(context) }); }
                catch (Exception ex)
                {
                    Log.Error(ObservationWork.Failed("royaltyFacts", ex));
                    return ProtoBoundary.Encode(new Obs.RoyaltyFactsReply { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = "The royalty facts could not be read completely." } });
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        // Rung reads RoyalTitleDef.throneRoomRequirements: the minimum
        // impressiveness and area, and the throne definitions that must stand
        // assigned to the holder.
        private static Obs.RoyalTitleRung Rung(RoyalTitleDef title)
        {
            var rung = new Obs.RoyalTitleRung { DefName = title.defName, Seniority = title.seniority, FavorNeeded = title.favorCost };
            foreach (var req in title.throneRoomRequirements ?? Enumerable.Empty<RoomRequirement>())
            {
                switch (req)
                {
                    case RoomRequirement_Area area: rung.ThroneMinArea = Math.Max(rung.ThroneMinArea, area.area); break;
                    case RoomRequirement_Impressiveness imp: rung.ThroneMinImpressiveness = Math.Max(rung.ThroneMinImpressiveness, imp.impressiveness); break;
                    case RoomRequirement_HasAssignedThroneAnyOf throne:
                        rung.ThroneAssigned = true;
                        rung.ThroneThings.AddRange((throne.things ?? new List<ThingDef>()).Where(d => d != null && ProtoBoundary.IsIdentifier(d.defName)).Select(d => d.defName));
                        break;
                }
            }
            foreach (var req in title.bedroomRequirements ?? Enumerable.Empty<RoomRequirement>())
            {
                switch (req)
                {
                    case RoomRequirement_Area area: rung.BedroomMinArea = Math.Max(rung.BedroomMinArea, area.area); break;
                    case RoomRequirement_Impressiveness imp: rung.BedroomMinImpressiveness = Math.Max(rung.BedroomMinImpressiveness, imp.impressiveness); break;
                    case RoomRequirement_TerrainWithTags _: rung.BedroomFloored = true; break;
                    case RoomRequirement_ThingAnyOfCount anyCount: rung.BedroomThings.Add(BedroomThing(anyCount.things, anyCount.count)); break;
                    case RoomRequirement_ThingAnyOf any: rung.BedroomThings.Add(BedroomThing(any.things, 1)); break;
                    case RoomRequirement_ThingCount count: rung.BedroomThings.Add(BedroomThing(new List<ThingDef> { count.thingDef }, count.count)); break;
                    case RoomRequirement_Thing thing: rung.BedroomThings.Add(BedroomThing(new List<ThingDef> { thing.thingDef }, 1)); break;
                }
            }
            return rung;
        }

        private static Obs.BedroomThingRequirement BedroomThing(List<ThingDef> defs, int count)
        {
            var row = new Obs.BedroomThingRequirement { Count = count };
            row.AnyOf.AddRange(defs.Where(d => d != null && ProtoBoundary.IsIdentifier(d.defName)).Select(d => d.defName));
            return row;
        }

        private static Obs.RoyaltyFacts Read(Common.ObservationContext context)
        {
            var facts = new Obs.RoyaltyFacts { Context = context };
            foreach (var title in DefDatabase<RoyalTitleDef>.AllDefsListForReading.Where(d => ProtoBoundary.IsIdentifier(d.defName))
                .OrderBy(d => d.seniority).ThenBy(d => d.defName, StringComparer.Ordinal))
                facts.Ladder.Add(Rung(title));
            foreach (var permit in DefDatabase<RoyalTitlePermitDef>.AllDefsListForReading.Where(d => ProtoBoundary.IsIdentifier(d.defName))
                .OrderBy(d => d.defName, StringComparer.Ordinal))
            {
                var row = new Obs.RoyalPermitDef { DefName = permit.defName, PermitPoints = permit.permitPointCost, Acts = permit.royalAid != null, CooldownDays = permit.cooldownDays };
                if (permit.minTitle != null && ProtoBoundary.IsIdentifier(permit.minTitle.defName)) row.MinTitle = permit.minTitle.defName;
                if (permit.royalAid != null) row.FavorCost = permit.royalAid.favorCost;
                facts.Permits.Add(row);
            }
            foreach (var pawn in PawnsFinder.AllMaps_FreeColonists.Where(p => p.royalty != null).OrderBy(p => p.thingIDNumber))
            {
                var royalty = pawn.royalty;
                var factions = royalty.AllFactionPermits.Select(p => p.Faction)
                    .Concat(royalty.AllTitlesForReading.Select(t => t.faction))
                    .Where(f => f?.def != null && ProtoBoundary.IsIdentifier(f.def.defName)).Distinct()
                    .OrderBy(f => f.def.defName, StringComparer.Ordinal).ToList();
                var row = new Obs.PawnRoyalty { Pawn = new Common.Ref { Id = pawn.GetUniqueLoadID() } };
                foreach (var faction in factions)
                {
                    var holding = new Obs.PawnRoyalHolding { FactionDef = faction.def.defName, Favor = royalty.GetFavor(faction), PermitPoints = royalty.GetPermitPoints(faction) };
                    var title = royalty.GetCurrentTitle(faction);
                    if (title != null) holding.Title = title.defName;
                    holding.Permits.AddRange(royalty.AllFactionPermits.Where(p => p.Faction == faction && p.Permit != null)
                        .Select(p => p.Permit.defName).OrderBy(n => n, StringComparer.Ordinal));
                    // The native cooldown of each held permit (#1607): FactionPermit.LastUsedTick
                    // (-1 until first used) and the ticks left of the permit's cooldown.
                    var now = Find.TickManager.TicksGame;
                    foreach (var held in royalty.AllFactionPermits.Where(p => p.Faction == faction && p.Permit != null && ProtoBoundary.IsIdentifier(p.Permit.defName))
                        .OrderBy(p => p.Permit.defName, StringComparer.Ordinal))
                    {
                        var cooldown = new Obs.PermitCooldown { Permit = held.Permit.defName, CooldownRemainingTicks = held.OnCooldown ? Math.Max(0, held.LastUsedTick + held.Permit.CooldownTicks - now) : 0 };
                        if (held.LastUsedTick >= 0) cooldown.LastUsedTick = held.LastUsedTick;
                        holding.PermitCooldowns.Add(cooldown);
                    }
                    row.Holdings.Add(holding);
                }
                if (pawn.abilities != null)
                    foreach (var ability in pawn.abilities.abilities.Where(a => a.def != null && a.def.IsPsycast && ProtoBoundary.IsIdentifier(a.def.defName))
                        .OrderBy(a => a.def.level).ThenBy(a => a.def.defName, StringComparer.Ordinal))
                        row.Psycasts.Add(Psycast(ability.def));
                if (row.Holdings.Count > 0 || row.Psycasts.Count > 0) facts.Pawns.Add(row);
            }
            ReadThrones(facts);
            ReadNeuroformers(facts);
            ReadCeremonies(facts);
            return facts;
        }

        // Throne ownership (#1601): every spawned player throne on the home
        // maps with its assigned colonist (Building_Throne.AssignedPawn), so
        // the planner can tell when a throne's assignment is done.
        private static void ReadThrones(Obs.RoyaltyFacts facts)
        {
            foreach (var map in Find.Maps.Where(m => m.IsPlayerHome))
                foreach (var throne in map.listerBuildings.AllBuildingsColonistOfClass<Building_Throne>().OrderBy(t => t.thingIDNumber))
                {
                    var row = new Obs.RoyalThrone { Thing = new Common.Ref { Id = throne.GetUniqueLoadID() } };
                    if (ProtoBoundary.IsIdentifier(throne.def.defName)) row.DefName = throne.def.defName;
                    var owner = throne.AssignedPawn;
                    if (owner != null) row.Owner = new Common.Ref { Id = owner.GetUniqueLoadID() };
                    facts.Thrones.Add(row);
                }
        }

        // Pending bestowing ceremonies (#1602): the bestowing quest of each
        // colonist and royal faction (the offered or ongoing quest, never an
        // ended one). The lord is the bestower's: its Wait toil is the
        // ceremony waiting for the player's command, its job holds the
        // started flag, the ceremony spot and the colonist participants.
        private static void ReadCeremonies(Obs.RoyaltyFacts facts)
        {
            var royalFactions = Find.FactionManager.AllFactionsListForReading
                .Where(f => f?.def != null && f.def.HasRoyalTitles && ProtoBoundary.IsIdentifier(f.def.defName))
                .OrderBy(f => f.def.defName, StringComparer.Ordinal).ToList();
            foreach (var pawn in PawnsFinder.AllMaps_FreeColonists.Where(p => p.royalty != null).OrderBy(p => p.thingIDNumber))
            {
                foreach (var faction in royalFactions)
                {
                    var quest = RoyalTitleUtility.GetCurrentBestowingCeremonyQuest(pawn, faction);
                    if (quest == null || quest.State != QuestState.NotYetAccepted && quest.State != QuestState.Ongoing) continue;
                    var part = quest.PartsListForReading.OfType<QuestPart_BestowingCeremony>().FirstOrDefault();
                    var row = new Obs.BestowingCeremony { Quest = quest.GetUniqueLoadID(), Pawn = new Common.Ref { Id = pawn.GetUniqueLoadID() }, FactionDef = faction.def.defName, Accepted = quest.State == QuestState.Ongoing };
                    var awarded = pawn.royalty.GetTitleAwardedWhenUpdating(faction, pawn.royalty.GetFavor(faction));
                    if (awarded != null && ProtoBoundary.IsIdentifier(awarded.defName)) row.Title = awarded.defName;
                    var bestower = part?.bestower;
                    if (bestower != null) row.Bestower = new Common.Ref { Id = bestower.GetUniqueLoadID() };
                    var lord = bestower?.GetLord();
                    var job = lord?.LordJob as LordJob_BestowingCeremony;
                    row.BestowerWaiting = bestower != null && bestower.Spawned && lord?.CurLordToil is LordToil_BestowingCeremony_Wait;
                    if (job != null)
                    {
                        row.Started = job.ceremonyStarted;
                        var spot = job.Spot;
                        if (spot.IsValid) row.Spot = new Common.Cell { X = spot.x, Z = spot.z };
                        foreach (var attendee in (job.colonistParticipants ?? new List<Pawn>()).Where(p => p != null).OrderBy(p => p.thingIDNumber))
                            row.Attendees.Add(new Common.Ref { Id = attendee.GetUniqueLoadID() });
                    }
                    facts.Ceremonies.Add(row);
                }
            }
        }

        private static Obs.PawnPsycast Psycast(AbilityDef def)
        {
            var cast = new Obs.PawnPsycast { DefName = def.defName, Level = def.level, PsyfocusCost = def.PsyfocusCost, Entropy = def.EntropyGain, CooldownTicks = def.cooldownTicksRange.max };
            var target = def.verbProperties?.targetParams;
            if (target == null) return cast;
            cast.TargetKind = target.canTargetSelf && !target.canTargetPawns && !target.canTargetLocations && !target.canTargetBuildings && !target.canTargetItems ? Obs.PsycastTargetKind.Self
                : target.canTargetPawns ? Obs.PsycastTargetKind.Pawn
                : target.canTargetBuildings || target.canTargetItems ? Obs.PsycastTargetKind.Thing
                : target.canTargetLocations ? Obs.PsycastTargetKind.Cell
                : Obs.PsycastTargetKind.Unspecified;
            return cast;
        }

        // The psylink neuroformer and every psycast neurotrainer. held counts
        // unforbidden spawned stacks on the player's home maps.
        private static void ReadNeuroformers(Obs.RoyaltyFacts facts)
        {
            var defs = DefDatabase<ThingDef>.AllDefsListForReading
                .Where(d => ProtoBoundary.IsIdentifier(d.defName) && (d == ThingDefOf.PsychicAmplifier || d.thingCategories?.Contains(ThingCategoryDefOf.NeurotrainersPsycast) == true))
                .OrderBy(d => d.defName, StringComparer.Ordinal);
            var maps = Find.Maps.Where(m => m.IsPlayerHome).ToList();
            foreach (var def in defs)
            {
                var row = new Obs.NeuroformerStock
                {
                    DefName = def.defName,
                    Held = maps.Sum(m => m.listerThings.ThingsOfDef(def).Where(t => !t.IsForbidden(Faction.OfPlayer)).Sum(t => t.stackCount)),
                    Craftable = DefDatabase<RecipeDef>.AllDefsListForReading.Any(r => r.AvailableNow && r.products.Any(p => p.thingDef == def)),
                    Tradeable = def.tradeability.TraderCanSell()
                };
                var teaches = def.comps?.OfType<CompProperties_UseEffect_GainAbility>().FirstOrDefault()?.ability;
                if (teaches != null && ProtoBoundary.IsIdentifier(teaches.defName)) row.TeachesPsycast = teaches.defName;
                facts.Neuroformers.Add(row);
            }
        }
    }
}
