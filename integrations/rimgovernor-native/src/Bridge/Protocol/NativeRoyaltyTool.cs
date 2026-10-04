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

        [Tool(ToolName, Title = "Read royalty facts", Description = "Royal title ladder, permit catalog, thrones, ceremonies and neuroformers (each colonist's own royalty rides its pawn row). Not applicable without Royalty. Read-only.")]
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
                // What the permit does for the colony (#1606): its worker class.
                if (permit.workerClass != null && ProtoBoundary.IsIdentifier(permit.workerClass.Name)) row.WorkerClass = permit.workerClass.Name;
                facts.Permits.Add(row);
            }
            return facts;
        }

        // A free colonist's royalty facts on its pawn row (#1876): holdings per
        // faction with each held permit's cooldown, the known psycasts and the
        // caster's psyfocus and neural heat (combat casts hold on them, #1611).
        // Absent without Royalty or a royalty tracker; a failed read leaves the
        // block absent next to a ReadIssue named "royalty".
        internal static void Apply(Pawn pawn, Obs.PawnState row)
        {
            if (!ModsConfig.RoyaltyActive || !pawn.IsFreeColonist || pawn.royalty == null) return;
            try { row.Royalty = Pawn(pawn); }
            catch (Exception ex)
            {
                row.Issues.Add(new Obs.ReadIssue { Field = "royalty", Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = PlacementPreviewOperation.Diagnostic(ex.Message) } });
            }
        }

        private static Obs.PawnRoyalty Pawn(Pawn pawn)
        {
            var royalty = pawn.royalty;
            var factions = royalty.AllFactionPermits.Select(p => p.Faction)
                .Concat(royalty.AllTitlesForReading.Select(t => t.faction))
                .Where(f => f?.def != null && ProtoBoundary.IsIdentifier(f.def.defName)).Distinct()
                .OrderBy(f => f.def.defName, StringComparer.Ordinal).ToList();
            var row = new Obs.PawnRoyalty();
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
                    row.Psycasts.Add(Psycast(ability.def, ability.CooldownTicksRemaining));
            var tracker = pawn.psychicEntropy;
            if (tracker != null && tracker.Psylink != null)
            {
                if (tracker.NeedsPsyfocus) row.Psyfocus = tracker.CurrentPsyfocus;
                row.Entropy = tracker.EntropyValue;
                row.EntropyMax = tracker.MaxEntropy;
            }
            return row;
        }

        private static Obs.PawnPsycast Psycast(AbilityDef def, int cooldownRemaining)
        {
            var cast = new Obs.PawnPsycast { DefName = def.defName, Level = def.level, PsyfocusCost = def.PsyfocusCost, Entropy = def.EntropyGain, CooldownTicks = def.cooldownTicksRange.max, CooldownRemainingTicks = Math.Max(0, cooldownRemaining) };
            var target = def.verbProperties?.targetParams;
            if (target == null) return cast;
            cast.TargetKind = target.canTargetSelf && !target.canTargetPawns && !target.canTargetLocations && !target.canTargetBuildings && !target.canTargetItems ? Obs.PsycastTargetKind.Self
                : target.canTargetPawns ? Obs.PsycastTargetKind.Pawn
                : target.canTargetBuildings || target.canTargetItems ? Obs.PsycastTargetKind.Thing
                : target.canTargetLocations ? Obs.PsycastTargetKind.Cell
                : Obs.PsycastTargetKind.Unspecified;
            return cast;
        }
    }
}
