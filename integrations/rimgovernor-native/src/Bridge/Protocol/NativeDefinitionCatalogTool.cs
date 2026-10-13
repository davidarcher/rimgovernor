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
using RimGovernor.Brain.Domain;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

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
            if (!ProtoBoundary.TryParse<Obs.DefinitionCatalogRequest>(ctx, ToolName, request!, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Obs.DefinitionCatalogReply { Outcome = new Obs.DefinitionCatalogReplyOutcome.FailureCase(failure) });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => HandleReadDefinitionCatalog(parsed), cancellationToken).ConfigureAwait(false);
        }

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Obs.DefinitionCatalogReply HandleReadDefinitionCatalog(Obs.DefinitionCatalogRequest parsed)
        {
            if (!parsed.Creation.GetValueOrDefault() && parsed.Scope?.ExpectedIdentity == null)
                return new Obs.DefinitionCatalogReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Expected identity required.") };
            if (parsed.Creation.GetValueOrDefault())
            {
                if (parsed.Scope != null || Current.ProgramState != ProgramState.Entry || Current.Game != null || LongEventHandler.AnyEventNowOrWaiting)
                    return new Obs.DefinitionCatalogReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Creation catalog requires a fresh main menu and no map scope.") };
                try
                {
                    // TODO(#3100): defs (the def sets and game constants are no longer carried).
                    var creation = new Obs.CreationDefinitionCatalog { ClassChains = DefMirrorFill.ClassChains(DefClasses()) };
                    return new Obs.DefinitionCatalogReply { Outcome = creation };
                }
                catch (Exception ex)
                {
                    ObservationWork.Failed("definitionCatalog", ex);
                    return new Obs.DefinitionCatalogReply { Outcome = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = PlacementPreviewOperation.Diagnostic("Creation defs could not be read completely: " + ex.GetType().Name + ": " + ex.Message) } };
                }
            }
            if (!ProtoBoundary.ValidateIdentity(parsed.Scope!.ExpectedIdentity, out _, out var context, out var error))
                return new Obs.DefinitionCatalogReply { Outcome = error };
            var player = Faction.OfPlayerSilentFail;
            if (player?.def == null)
                return new Obs.DefinitionCatalogReply { Outcome = new Common.Unavailable { Reason = Common.UnavailableReason.NativeComponentMissing, Detail = "Player faction is unavailable." } };
            try { return new Obs.DefinitionCatalogReply { Outcome = Read(context, (parsed.HeldFingerprint ?? "")) }; }
            catch (Exception ex)
            {
                ObservationWork.Failed("definitionCatalog", ex);
                return new Obs.DefinitionCatalogReply { Outcome = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = PlacementPreviewOperation.Diagnostic("The definition catalog could not be read completely: " + ex.GetType().Name + ": " + ex.Message) } };
            }
        }

        private static Obs.DefinitionCatalog Read(Common.ObservationContext context, string heldFingerprint)
        {
            var fingerprint = NativeCatalogFingerprint.Value;
            var catalog = new Obs.DefinitionCatalog { Context = context, Fingerprint = fingerprint, StatEnv = StatEnv() };
            // The caller holds this static part: only what changes per load is sent.
            if (heldFingerprint == fingerprint) return catalog;
            // TODO(#3100): defs (the ThingDef/TerrainDef rows, def sets and game constants are no longer carried).
            // The base chains of the def classes the game has.
            catalog = catalog with { ClassChains = DefMirrorFill.ClassChains(DefClasses()) };
            catalog = catalog with { Derived = Derived() };
            var facts = new List<Obs.ThingDefFacts>();
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.race != null).OrderBy(d => Named(d.defName, "ThingDef"), StringComparer.Ordinal))
                facts.Add(NativeFoodPolicy.Facts(def));
            catalog = catalog with { ThingFacts = EquatableArray.From(facts), Biotech = NativeBiotechFacts.Catalog() };
            return catalog;
        }

        // Every concrete def class the game has: the ones with a database and the exact
        // classes of the defs in them (a class below another concrete def class has no
        // database of its own; its defs sit in the root class's database).
        private static IEnumerable<Type> DefClasses()
        {
            var classes = new HashSet<Type> { typeof(ThingDef), typeof(TerrainDef) };
            foreach (var root in GenDefDatabase.AllDefTypesWithDatabases().Where(t => !t.IsAbstract))
            {
                classes.Add(root);
                foreach (var def in GenDefDatabase.GetAllDefsInDatabaseForDef(root)) classes.Add(def.GetType());
            }
            return classes;
        }

        // The game state Go's stat evaluator reads that the def rows do not hold
        // (stateval.Env): the active mods, the ideology classic mode, the scenario's
        // stat factors and the storyteller difficulty's yield factors and bool settings.
        private static Obs.StatEnv StatEnv()
        {
            var mods = ModsConfig.ActiveModsInLoadOrder.Select(mod => mod.PackageId.ToLowerInvariant()).ToList();
            var classic = Find.IdeoManager?.classicMode ?? false;
            var scenario = Find.Scenario ?? throw new InvalidOperationException("Find.Scenario is unavailable.");
            var factors = new List<Obs.StatFactor>();
            foreach (var stat in DefDatabase<StatDef>.AllDefsListForReading.OrderBy(s => Named(s.defName, "StatDef"), StringComparer.Ordinal))
            {
                var factor = scenario.GetStatFactor(stat);
                if (factor != 1f) factors.Add(new Obs.StatFactor { Stat = stat.defName, Factor = factor });
            }
            var difficulty = Find.Storyteller?.difficulty ?? throw new InvalidOperationException("Find.Storyteller.difficulty is unavailable.");
            var flags = typeof(Difficulty).GetFields(BindingFlags.Instance | BindingFlags.Public).Where(f => f.FieldType == typeof(bool)).OrderBy(f => f.Name, StringComparer.Ordinal)
                .Select(field => new Obs.DifficultyFlag { Name = field.Name, Value = (bool)field.GetValue(difficulty)! }).ToList();
            return new Obs.StatEnv
            {
                ActiveMods = EquatableArray.From(mods),
                ClassicMode = classic,
                ScenarioFactors = EquatableArray.From(factors),
                ButcherYieldFactor = difficulty.butcherYieldFactor,
                FishingYieldFactor = difficulty.fishingYieldFactor,
                DifficultyFlags = EquatableArray.From(flags),
            };
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
