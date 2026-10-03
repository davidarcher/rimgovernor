#nullable enable
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Defs = RimGovernor.Protocol.Defs;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // The definition catalog (#1340): the static planning facts of every
    // buildable or sowable definition and every research project. They hold
    // for the whole load, so the controller reads them once per load token
    // and frames carry only per-map, per-tick values.
    public sealed class NativeDefinitionCatalogTool
    {
        internal const string ToolName = "rimgovernor/observations_read_definition_catalog";

        [Tool(ToolName, Title = "Read the definition catalog", Description = "Static planning facts of every player-buildable or sowable ThingDef and buildable TerrainDef, and every research project with its costs and prerequisites, plus every ThingDef and TerrainDef with all its fields and the game constants. Fixed for a load. Read-only.")]
        [ToolResponse("payload", "string", "Official DefinitionCatalogReply ProtoJSON.", Always = true)]
        public async Task<object> ReadDefinitionCatalog(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw DefinitionCatalogRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request!, Obs.DefinitionCatalogRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Obs.DefinitionCatalogReply { Failure = failure });
            if (parsed.Scope?.ExpectedIdentity == null)
                return ProtoBoundary.Encode(new Obs.DefinitionCatalogReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Expected identity required.") });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope.ExpectedIdentity, out _, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.DefinitionCatalogReply { Failure = error });
                var player = Faction.OfPlayerSilentFail;
                if (player?.def == null)
                    return ProtoBoundary.Encode(new Obs.DefinitionCatalogReply { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.NativeComponentMissing, Detail = "Player faction is unavailable." } });
                try { return ProtoBoundary.Encode(new Obs.DefinitionCatalogReply { Observed = Read(context, player) }); }
                catch (Exception ex)
                {
                    Log.Error(ObservationWork.Failed("definitionCatalog", ex));
                    return ProtoBoundary.Encode(new Obs.DefinitionCatalogReply { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = "The definition catalog could not be read completely." } });
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        private static Obs.DefinitionCatalog Read(Common.ObservationContext context, Faction player)
        {
            var catalog = new Obs.DefinitionCatalog { Context = context };
            var rows = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => ProtoBoundary.IsIdentifier(d.defName) && NativeColonyObservationTools.Cataloged(d))
                .Select(NativeColonyObservationTools.Definition)
                .Concat(DefDatabase<TerrainDef>.AllDefsListForReading.Where(d => ProtoBoundary.IsIdentifier(d.defName) && d.BuildableByPlayer)
                    .Select(NativeColonyObservationTools.Terrain));
            // A name both a ThingDef and a TerrainDef carry keeps the thing.
            foreach (var row in rows.GroupBy(r => r.Definition.DefName, StringComparer.Ordinal).Select(g => g.First()).OrderBy(r => r.Definition.DefName, StringComparer.Ordinal))
                catalog.Definitions.Add(row);
            foreach (var def in DefDatabase<ResearchProjectDef>.AllDefsListForReading.Where(d => ProtoBoundary.IsIdentifier(d.defName)).OrderBy(d => d.defName, StringComparer.Ordinal))
                catalog.Research.Add(NativeResearchObservationTools.Static(def, player));
            // Every def with all its fields (#1730), by protobuf reflection over the
            // generated messages. An unmapped field throws and fails the read.
            var mirror = new DefMirrorFill();
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading.Where(d => ProtoBoundary.IsIdentifier(d.defName)).OrderBy(d => d.defName, StringComparer.Ordinal))
                catalog.ThingDefs.Add(mirror.Build<Defs.ThingDef>(def));
            foreach (var def in DefDatabase<TerrainDef>.AllDefsListForReading.Where(d => ProtoBoundary.IsIdentifier(d.defName)).OrderBy(d => d.defName, StringComparer.Ordinal))
                catalog.TerrainDefs.Add(mirror.Build<Defs.TerrainDef>(def));
            catalog.Constants = Constants();
            catalog.Biotech = NativeBiotechFacts.Catalog();
            catalog.Ideology = NativeIdeologyObservation.Catalog();
            catalog.Odyssey = NativeOdysseyFacts.Catalog();
            catalog.Anomaly = NativeAnomalyFacts.Catalog();
            return catalog;
        }

        private static Obs.CatalogConstants Constants() => new Obs.CatalogConstants
        {
            TicksPerHour = GenDate.TicksPerHour,
            TicksPerDay = GenDate.TicksPerDay,
            DaysPerYear = GenDate.DaysPerYear,
            BillStackMax = BillStack.MaxCount,
            SkillMaxLevel = SkillRecord.MaxLevel,
            LitGlowThreshold = LitGlowThreshold(),
        };

        // GlowGrid.GameGlowLitThreshold is a non-public const: read it by name, and
        // fail naming it when the game no longer has it or its type changed.
        private static float LitGlowThreshold()
        {
            const string member = "GlowGrid.GameGlowLitThreshold";
            var field = typeof(GlowGrid).GetField("GameGlowLitThreshold", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null || !field.IsLiteral)
                throw new InvalidOperationException($"{member} is not a constant of this game version.");
            if (!(field.GetRawConstantValue() is float value))
                throw new InvalidOperationException($"{member} is a {field.FieldType.FullName} constant, not a float.");
            return value;
        }
    }
}
