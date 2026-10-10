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
    // The definition catalog: the static planning facts of every
    // buildable or sowable definition and every research project. They hold
    // for the whole load, so the controller reads them once per load token
    // and frames carry only per-map, per-tick values.
    public sealed class NativeDefinitionCatalogTool
    {
        internal const string ToolName = "rimgovernor/observations_read_definition_catalog";

        [Tool(ToolName, Title = "Read the definition catalog", Description = "Static planning facts of every player-buildable or sowable ThingDef and buildable TerrainDef, plus every ThingDef and TerrainDef with all its fields and the game constants. Fixed for a load. Read-only.")]
        [ToolResponse("payload", "string", "Official DefinitionCatalogReply ProtoJSON.", Always = true)]
        public async Task<object> ReadDefinitionCatalog(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw DefinitionCatalogRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request!, Obs.DefinitionCatalogRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Obs.DefinitionCatalogReply { Failure = failure });
            if (!parsed.Creation && parsed.Scope?.ExpectedIdentity == null)
                return ProtoBoundary.Encode(new Obs.DefinitionCatalogReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Expected identity required.") });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (parsed.Creation)
                {
                    if (parsed.Scope != null || Current.ProgramState != ProgramState.Entry || Current.Game != null || LongEventHandler.AnyEventNowOrWaiting)
                        return ProtoBoundary.Encode(new Obs.DefinitionCatalogReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Creation catalog requires a fresh main menu and no map scope.") });
                    try
                    {
                        var mirror = new DefMirrorFill();
                        var creation = new Obs.CreationDefinitionCatalog { Defs = DefSets(mirror) };
                        foreach (var chain in mirror.ClassChains()) { var row = new Obs.ClassChain { Name = chain.Key }; row.Bases.AddRange(chain.Value); creation.ClassChains.Add(row); }
                        creation.GameConstants = mirror.BuildStatics<Defs.GameConstants>();
                        return ProtoBoundary.Encode(new Obs.DefinitionCatalogReply { Creation = creation });
                    }
                    catch (Exception ex)
                    {
                        ObservationWork.Failed("definitionCatalog", ex);
                        return ProtoBoundary.Encode(new Obs.DefinitionCatalogReply { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = PlacementPreviewOperation.Diagnostic("Creation defs could not be read completely: " + ex.GetType().Name + ": " + ex.Message) } });
                    }
                }
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope!.ExpectedIdentity, out _, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.DefinitionCatalogReply { Failure = error });
                var player = Faction.OfPlayerSilentFail;
                if (player?.def == null)
                    return ProtoBoundary.Encode(new Obs.DefinitionCatalogReply { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.NativeComponentMissing, Detail = "Player faction is unavailable." } });
                try { return ProtoBoundary.Encode(new Obs.DefinitionCatalogReply { Observed = Read(context) }); }
                catch (Exception ex)
                {
                    ObservationWork.Failed("definitionCatalog", ex);
                    return ProtoBoundary.Encode(new Obs.DefinitionCatalogReply { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = PlacementPreviewOperation.Diagnostic("The definition catalog could not be read completely: " + ex.GetType().Name + ": " + ex.Message) } });
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        private static Obs.DefinitionCatalog Read(Common.ObservationContext context)
        {
            var catalog = new Obs.DefinitionCatalog { Context = context };
            // Every def with all its fields, by protobuf reflection over the
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
            catalog.GameConstants = mirror.BuildStatics<Defs.GameConstants>();
            catalog.Derived = Derived();
            catalog.StatEnv = StatEnv();
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading.OrderBy(d => Named(d.defName, "ThingDef"), StringComparer.Ordinal))
                catalog.ThingFacts.Add(NativeFoodPolicy.Facts(def));
            catalog.Biotech = NativeBiotechFacts.Catalog();
            return catalog;
        }

        // Every def of each class DefSets mirrors, by exact class and in
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

        // The game state Go's stat evaluator reads that the def rows do not hold
        // (stateval.Env): the active mods, the ideology classic mode, the scenario's
        // stat factors and the storyteller difficulty's yield factors and bool settings.
        private static Obs.StatEnv StatEnv()
        {
            var env = new Obs.StatEnv();
            foreach (var mod in ModsConfig.ActiveModsInLoadOrder)
                env.ActiveMods.Add(mod.PackageId.ToLowerInvariant());
            env.ClassicMode = Find.IdeoManager?.classicMode ?? false;
            var scenario = Find.Scenario ?? throw new InvalidOperationException("Find.Scenario is unavailable.");
            foreach (var stat in DefDatabase<StatDef>.AllDefsListForReading.OrderBy(s => Named(s.defName, "StatDef"), StringComparer.Ordinal))
            {
                var factor = scenario.GetStatFactor(stat);
                if (factor != 1f) env.ScenarioFactors.Add(new Obs.StatFactor { Stat = stat.defName, Factor = factor });
            }
            var difficulty = Find.Storyteller?.difficulty ?? throw new InvalidOperationException("Find.Storyteller.difficulty is unavailable.");
            env.ButcherYieldFactor = difficulty.butcherYieldFactor;
            env.FishingYieldFactor = difficulty.fishingYieldFactor;
            foreach (var field in typeof(Difficulty).GetFields(BindingFlags.Instance | BindingFlags.Public).Where(f => f.FieldType == typeof(bool)).OrderBy(f => f.Name, StringComparer.Ordinal))
                env.DifficultyFlags.Add(new Obs.DifficultyFlag { Name = field.Name, Value = (bool)field.GetValue(difficulty)! });
            return env;
        }

        // A def whose defName is not a protocol identifier cannot be a catalog key:
        // fail the read naming it rather than leave it out.
        private static string Named(string defName, string kind) =>
            ProtoBoundary.IsIdentifier(defName) ? defName : throw new InvalidOperationException($"{kind} defName '{defName}' is not an identifier.");

        private static Obs.CatalogDerived Derived() => new Obs.CatalogDerived
        {
            // Tradeable.IsCurrency is "def == ThingDefOf.Silver".
            CurrencyDef = ThingDefOf.Silver?.defName ?? throw new InvalidOperationException("ThingDefOf.Silver is not loaded."),
            // Building_FermentingBarrel adds ThingDefOf.Wort and takes out ThingDefOf.Beer.
            WortDef = ThingDefOf.Wort?.defName ?? throw new InvalidOperationException("ThingDefOf.Wort is not loaded."),
            FullRotRateC = FullRotRateC(),
        };

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
    }
}
