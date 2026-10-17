#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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
    internal static class NativeDefinitionCatalogTool
    {

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Obs.DefinitionCatalogReply HandleReadDefinitionCatalog(Obs.DefinitionCatalogRequest parsed)
        {
            if (parsed.Scope?.ExpectedIdentity == null)
                return new Obs.DefinitionCatalogReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Expected identity required.") };
            if (!ProtoBoundary.ValidateIdentity(parsed.Scope!.ExpectedIdentity, out _, out var context, out var error))
                return new Obs.DefinitionCatalogReply { Outcome = error };
            var player = Faction.OfPlayerSilentFail;
            if (player?.def == null)
                return new Obs.DefinitionCatalogReply { Outcome = new Common.Unavailable { Reason = Common.UnavailableReason.NativeComponentMissing, Detail = "Player faction is unavailable." } };
            try
            {
                // The brain builds its catalog over the load's def stat table right after this read, off the game thread.
                NativeStatTable.EnsureForLoad(context.Identity.LoadToken);
                return new Obs.DefinitionCatalogReply { Outcome = Read(context) };
            }
            catch (Exception ex)
            {
                ObservationWork.Failed("definitionCatalog", ex);
                return new Obs.DefinitionCatalogReply { Outcome = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = PlacementPreviewOperation.Diagnostic("The definition catalog could not be read completely: " + ex.GetType().Name + ": " + ex.Message) } };
            }
        }

        private static Obs.DefinitionCatalog Read(Common.ObservationContext context)
        {
            var catalog = new Obs.DefinitionCatalog { Context = context, StatEnv = StatEnv() };
            // The reply carries no def rows or game constants (#3100): the in-process brain builds its catalog over the
            // game's Def objects (NativeCatalogFactory), so only what the game computes in code is sent.
            // The base chains of the def classes the game has.
            catalog = catalog with { ClassChains = DefMirrorFill.ClassChains(DefClasses()) };
            catalog = catalog with { Derived = DerivedFacts() };
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
                ScenarioFactors = EquatableArray.From(factors),
                DifficultyFlags = EquatableArray.From(flags),
            };
        }

        // A def whose defName is not a protocol identifier cannot be a catalog key:
        // fail the read naming it rather than leave it out.
        private static string Named(string defName, string kind) =>
            ProtoBoundary.IsIdentifier(defName) ? defName : throw new InvalidOperationException($"{kind} defName '{defName}' is not an identifier.");

        internal static Obs.CatalogDerived DerivedFacts() => new Obs.CatalogDerived
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
