#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
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

        private static Obs.RoyaltyFacts Read(Common.ObservationContext context)
        {
            var facts = new Obs.RoyaltyFacts { Context = context };
            foreach (var title in DefDatabase<RoyalTitleDef>.AllDefsListForReading.Where(d => ProtoBoundary.IsIdentifier(d.defName))
                .OrderBy(d => d.seniority).ThenBy(d => d.defName, StringComparer.Ordinal))
                facts.Ladder.Add(new Obs.RoyalTitleRung { DefName = title.defName, Seniority = title.seniority, FavorNeeded = title.favorCost });
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
                    row.Holdings.Add(holding);
                }
                if (row.Holdings.Count > 0) facts.Pawns.Add(row);
            }
            return facts;
        }
    }
}
