#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
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
                    ObservationWork.Failed("definitionCatalog", ex);
                    return ProtoBoundary.Encode(new Obs.DefinitionCatalogReply { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = "The definition catalog could not be read completely." } });
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        private static Obs.DefinitionCatalog Read(Common.ObservationContext context, Faction player)
        {
            var catalog = new Obs.DefinitionCatalog { Context = context };
            foreach (var def in DefDatabase<ResearchProjectDef>.AllDefsListForReading.Where(d => ProtoBoundary.IsIdentifier(d.defName)).OrderBy(d => d.defName, StringComparer.Ordinal))
                catalog.Research.Add(NativeResearchObservationTools.Static(def, player));
            // Every def with all its fields (#1730), by protobuf reflection over the
            // generated messages. An unmapped field throws and fails the read.
            var mirror = new DefMirrorFill();
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading.OrderBy(d => Named(d.defName, "ThingDef"), StringComparer.Ordinal))
                catalog.ThingDefs.Add(mirror.Build<Defs.ThingDef>(def));
            foreach (var def in DefDatabase<TerrainDef>.AllDefsListForReading.OrderBy(d => Named(d.defName, "TerrainDef"), StringComparer.Ordinal))
                catalog.TerrainDefs.Add(mirror.Build<Defs.TerrainDef>(def));
            catalog.Defs = DefSets(mirror);
            // After every def is filled: the base chains of the classes the fill touched.
            foreach (var chain in mirror.ClassChains())
            {
                var row = new Obs.ClassChain { Name = chain.Key };
                row.Bases.AddRange(chain.Value);
                catalog.ClassChains.Add(row);
            }
            catalog.Constants = Constants();
            catalog.StatValues = StatValues();
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading.OrderBy(d => Named(d.defName, "ThingDef"), StringComparer.Ordinal))
                catalog.ThingFacts.Add(NativeFoodPolicy.Facts(def));
            catalog.Biotech = NativeBiotechFacts.Catalog();
            catalog.Odyssey = NativeOdysseyFacts.Catalog();
            catalog.Anomaly = NativeAnomalyFacts.Catalog();
            return catalog;
        }

        // Every def of each class DefSets mirrors (#1761), by exact class and in
        // defName order. A concrete def class the mirror has no field for (a mod's)
        // fails the read naming it.
        private static Defs.DefSets DefSets(DefMirrorFill mirror)
        {
            var types = GenDefDatabase.AllDefTypesWithDatabases().Where(t => !t.IsAbstract).ToDictionary(t => t.FullName ?? t.Name, StringComparer.Ordinal);
            // A def class below another concrete def class has no database of
            // its own (AllDefTypesWithDatabases): its defs sit in the root
            // class's database, so every database is read once and the defs are
            // grouped by their exact class.
            var byClass = new Dictionary<Type, List<Def>>();
            foreach (var root in types.Values)
                foreach (var def in GenDefDatabase.GetAllDefsInDatabaseForDef(root))
                {
                    if (!byClass.TryGetValue(def.GetType(), out var group)) byClass[def.GetType()] = group = new List<Def>();
                    group.Add(def);
                }
            var mirrored = new HashSet<Type> { typeof(ThingDef), typeof(TerrainDef) };
            var sets = new Defs.DefSets();
            foreach (var field in Defs.DefSets.Descriptor.Fields.InFieldNumberOrder())
            {
                var clr = mirror.ClrName(field.MessageType) ?? throw new InvalidOperationException($"DefSets.{field.Name} has no clr_type option.");
                // A class the game does not have at all is a stale mapping and
                // fails the read; a def class of an expansion that is not loaded
                // has no defs: its set is empty.
                var type = GenTypes.GetTypeInAnyAssembly(clr);
                if (type == null || !typeof(Def).IsAssignableFrom(type)) throw new InvalidOperationException($"DefSets.{field.Name}: the game has no def class {clr}.");
                mirrored.Add(type);
                if (!byClass.TryGetValue(type, out var defs)) continue;
                var list = (IList)field.Accessor.GetValue(sets);
                foreach (var def in defs.OrderBy(d => Named(d.defName, type.Name), StringComparer.Ordinal))
                    list.Add(mirror.Build(field.MessageType, def));
            }
            var missing = types.Values.Concat(byClass.Keys).Distinct().Where(t => !mirrored.Contains(t)).Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (missing.Count > 0) throw new InvalidOperationException("Def classes the mirror has no field for: " + string.Join(", ", missing));
            return sets;
        }

        // The game's own GetStatValueAbstract(stat, stuff) (#1759) for every
        // ThingDef: once per allowed stuff when the def is made from stuff, once
        // with no stuff otherwise. A stat the game does not show for the def
        // (StatWorker.ShouldShowFor) is left out of its row, except the planner
        // stats of a ThingDef (PlannerStats), emitted whether shown or not. A stat that fails to
        // compute, or computes a non-finite value, fails the read naming def,
        // stuff and stat; nothing is skipped or defaulted.
        private static Obs.DefStatTable StatValues()
        {
            var table = new Obs.DefStatTable();
            var forcedStuffs = ScenarioStartingStuffs();
            var stats = DefDatabase<StatDef>.AllDefsListForReading.OrderBy(s => Named(s.defName, "StatDef"), StringComparer.Ordinal).ToList();
            foreach (var stat in stats) table.Stats.Add(stat.defName);
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading.OrderBy(d => Named(d.defName, "ThingDef"), StringComparer.Ordinal))
            {
                if (!def.MadeFromStuff) { table.Rows.Add(StatRow(def, null, stats)); continue; }
                var allowed = GenStuff.AllowedStuffsFor(def).ToList();
                // A scenario can start the colony with a stuff the game would not
                // offer for the def (the classic one's jade knife); the item
                // exists, so its stats are read from a row of its own. Go keeps
                // such a pair out of the allowed stuffs by category.
                if (forcedStuffs.TryGetValue(def, out var forced))
                    allowed.AddRange(forced.Where(s => !allowed.Contains(s)));
                foreach (var stuff in allowed.OrderBy(s => Named(s.defName, "ThingDef"), StringComparer.Ordinal))
                    table.Rows.Add(StatRow(def, stuff, stats));
            }
            // Every TerrainDef the same way, with no stuff, and its adjusted cost list.
            foreach (var def in DefDatabase<TerrainDef>.AllDefsListForReading.OrderBy(d => Named(d.defName, "TerrainDef"), StringComparer.Ordinal))
                table.TerrainRows.Add(StatRow(def, null, stats));
            return table;
        }

        // The (def, stuff) pairs the current scenario's parts start the colony
        // with (ScenPart_ThingCount.thingDef and .stuff, both protected).
        private static Dictionary<ThingDef, List<ThingDef>> ScenarioStartingStuffs()
        {
            var result = new Dictionary<ThingDef, List<ThingDef>>();
            var scenario = Current.Game?.Scenario;
            if (scenario == null) return result;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var defField = typeof(ScenPart_ThingCount).GetField("thingDef", flags) ?? throw new InvalidOperationException("ScenPart_ThingCount.thingDef is unavailable.");
            var stuffField = typeof(ScenPart_ThingCount).GetField("stuff", flags) ?? throw new InvalidOperationException("ScenPart_ThingCount.stuff is unavailable.");
            foreach (var part in scenario.AllParts.OfType<ScenPart_ThingCount>())
            {
                if (defField.GetValue(part) is ThingDef def && def.MadeFromStuff && stuffField.GetValue(part) is ThingDef stuff)
                {
                    if (!result.TryGetValue(def, out var list)) result[def] = list = new List<ThingDef>();
                    if (!list.Contains(stuff)) list.Add(stuff);
                }
            }
            return result;
        }

        // Stats a planner reads of every ThingDef whether or not the game shows
        // them: DeteriorationRate is showIfUndefined false, so the game hides it
        // for any def that does not set it, where its value is the default base
        // value (0). Go reads it as bridge.StatDeteriorationRate.
        private static readonly HashSet<string> PlannerStats = new HashSet<string>(StringComparer.Ordinal) { "DeteriorationRate" };

        private static Obs.DefStatRow StatRow(BuildableDef def, ThingDef? stuff, System.Collections.Generic.List<StatDef> stats)
        {
            var row = new Obs.DefStatRow { DefName = def.defName, StuffName = stuff?.defName ?? "" };
            for (var i = 0; i < stats.Count; i++)
            {
                var stat = stats[i];
                try
                {
                    if (!stat.Worker.ShouldShowFor(StatRequest.For(def, stuff)) && !(def is ThingDef && PlannerStats.Contains(stat.defName))) continue;
                    var value = def.GetStatValueAbstract(stat, stuff);
                    if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidOperationException($"the value is {value}");
                    row.Stat.Add(i);
                    row.Value.Add(value);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Stat {stat.defName} of def {def.defName} with stuff {stuff?.defName ?? "(none)"} failed: {ex.Message}", ex);
                }
            }
            try
            {
                foreach (var cost in def.CostListAdjusted(stuff, false))
                    row.Costs.Add(new Obs.Quantity { DefName = cost.thingDef.defName, Units = cost.count });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Adjusted cost list of def {def.defName} with stuff {stuff?.defName ?? "(none)"} failed: {ex.Message}", ex);
            }
            return row;
        }

        // A def whose defName is not a protocol identifier cannot be a catalog key:
        // fail the read naming it rather than leave it out.
        private static string Named(string defName, string kind) =>
            ProtoBoundary.IsIdentifier(defName) ? defName : throw new InvalidOperationException($"{kind} defName '{defName}' is not an identifier.");

        private static Obs.CatalogConstants Constants() => new Obs.CatalogConstants
        {
            TicksPerHour = GenDate.TicksPerHour,
            TicksPerDay = GenDate.TicksPerDay,
            DaysPerYear = GenDate.DaysPerYear,
            BillStackMax = BillStack.MaxCount,
            SkillMaxLevel = SkillRecord.MaxLevel,
            LitGlowThreshold = LitGlowThreshold(),
            // Tradeable.IsCurrency is "def == ThingDefOf.Silver".
            CurrencyDef = ThingDefOf.Silver?.defName ?? throw new InvalidOperationException("ThingDefOf.Silver is not loaded."),
            // Building_FermentingBarrel adds ThingDefOf.Wort and takes out ThingDefOf.Beer.
            WortDef = ThingDefOf.Wort?.defName ?? throw new InvalidOperationException("ThingDefOf.Wort is not loaded."),
            FullRotRateC = FullRotRateC(),
            RoofMaxSupportDistance = RoofMaxSupportDistance(),
        };

        // RoofCollapseUtility.RoofMaxSupportDistance is a public const, which the
        // compiler would inline from the reference assembly: read the loaded
        // assembly's value by name instead, and fail naming it when the game no
        // longer has it or its type changed.
        private static float RoofMaxSupportDistance()
        {
            const string member = "RoofCollapseUtility.RoofMaxSupportDistance";
            var field = typeof(RoofCollapseUtility).GetField("RoofMaxSupportDistance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null || !field.IsLiteral)
                throw new InvalidOperationException($"{member} is not a constant of this game version.");
            if (!(field.GetRawConstantValue() is float value))
                throw new InvalidOperationException($"{member} is a {field.FieldType.FullName} constant, not a float.");
            return value;
        }

        // The game keeps its rot curve as literals inside
        // GenTemperature.RotRateAtTemperature, so evaluate that function: bisect the
        // ordered bit patterns of the non-negative floats for the first temperature
        // whose rate reaches 1. It is exact for any non-decreasing curve, and fails
        // naming the function when the rate never reaches 1 or is not a number.
        private static float FullRotRateC()
        {
            const string member = "GenTemperature.RotRateAtTemperature";
            float Rate(float temperature)
            {
                var rate = GenTemperature.RotRateAtTemperature(temperature);
                if (float.IsNaN(rate) || float.IsInfinity(rate))
                    throw new InvalidOperationException($"{member}({temperature}) is {rate}.");
                return rate;
            }
            const float top = 1000f;
            if (Rate(0f) >= 1f)
                throw new InvalidOperationException($"{member} is already at its full rate at 0 C.");
            if (Rate(top) < 1f)
                throw new InvalidOperationException($"{member} never reaches a rate of 1 up to {top} C.");
            var below = BitConverter.ToInt32(BitConverter.GetBytes(0f), 0);
            var at = BitConverter.ToInt32(BitConverter.GetBytes(top), 0);
            while (at - below > 1)
            {
                var middle = below + (at - below) / 2;
                if (Rate(BitConverter.ToSingle(BitConverter.GetBytes(middle), 0)) >= 1f) at = middle; else below = middle;
            }
            return BitConverter.ToSingle(BitConverter.GetBytes(at), 0);
        }

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
