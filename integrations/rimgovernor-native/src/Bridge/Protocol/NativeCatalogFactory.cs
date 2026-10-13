#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Policy;
using RimWorld;
using Verse;
using BrainCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using BrainDerived = RimGovernor.Brain.Bridge.CatalogDerived;
using BrainRaceFacts = RimGovernor.Brain.Bridge.RaceGameFacts;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The definition catalog the in-process brain plans over (#3116). The catalog reply carries what the game computes
    /// in code (derived facts, race facts, the stat environment, class chains) but not the def rows or game constants
    /// (#3100): the brain's <see cref="BrainCatalog"/> is built over the game's own Def objects, so its builders live
    /// here, in the one assembly that may read the Def databases. <c>BrainCompositionOptions.CatalogFactory</c> is
    /// <see cref="Create"/> and <c>BrainCompositionOptions.StartingIdeoligion</c> is <see cref="StartingIdeoligion"/>;
    /// the host start-up (#3115) installs both.
    /// </summary>
    /// <remarks>
    /// Both run on a brain thread, never the game thread: the catalog's stat reads go through
    /// <see cref="GameThreadStatService"/>, which blocks until the game thread drains it. The Def databases are written
    /// once at boot, so reading them off the game thread after the Defs have loaded is safe.
    /// </remarks>
    internal static class NativeCatalogFactory
    {
        /// <summary>
        /// The catalog of a load: every Def of the game, the derived and race facts of its (validated) wire catalog, and
        /// the game-thread stat service.
        /// </summary>
        internal static BrainCatalog Create(Obs.DefinitionCatalog wire, Common.Identity identity)
        {
            ArgumentNullException.ThrowIfNull(wire);
            ArgumentNullException.ThrowIfNull(identity);
            var derived = wire.Derived ?? throw new InvalidOperationException("The definition catalog carries no derived facts.");
            var races = new Dictionary<string, BrainRaceFacts>(StringComparer.Ordinal);
            foreach (var row in wire.ThingFacts)
            {
                if (row.Race is { } race)
                    races.Add(row.DefName, new BrainRaceFacts([.. race.EdibleDefs], race.TamenessCanDecay, race.TamenessDecayPeriodTicks, race.TameChanceFactor, race.AdultFeedPerDay));
            }
            return new BrainCatalog(AllDefs(), GameThreadStatService.Instance, new BrainDerived(derived.CurrencyDef, derived.WortDef, derived.FullRotRateC), races);
        }

        /// <summary>
        /// The ideoligion the governor chooses for a new colony of <paramref name="scenario"/>: the best supported legal
        /// design for the scenario's player faction, over the game's Defs, which need no world and so work on the main menu.
        /// </summary>
        internal static Task<Common.IdeoligionDesign> StartingIdeoligion(string scenario, CancellationToken cancellationToken)
            => Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                // ScenPart_PlayerFaction.factionDef is not public.
                var part = DefDatabase<ScenarioDef>.GetNamedSilentFail(scenario)?.scenario?.AllParts.OfType<ScenPart_PlayerFaction>().SingleOrDefault();
                var field = part == null ? null : AccessTools.Field(typeof(ScenPart_PlayerFaction), "factionDef");
                var faction = (field?.GetValue(part) as FactionDef)?.defName;
                if (string.IsNullOrEmpty(faction))
                    throw new InvalidOperationException("The creation scenario lacks its player faction.");
                var derived = NativeDefinitionCatalogTool.DerivedFacts();
                var catalog = new BrainCatalog(AllDefs(), GameThreadStatService.Instance, new BrainDerived(derived.CurrencyDef, derived.WortDef, derived.FullRotRateC));
                var options = catalog.IdeoligionOptions(faction!);
                if (!PolicyFunctions.ChooseStartingIdeoligion(options).TryGetValue(out var choice))
                    throw new InvalidOperationException("No supported legal ideoligion design can be established.");
                return BridgeFunctions.WireIdeoligionDesign(choice.Design);
            }, cancellationToken);

        /// <summary>
        /// Every Def of the game: each concrete Def class with a database and the exact classes of the defs in it (a class
        /// below another concrete def class has no database of its own; its defs sit in the root class's database).
        /// </summary>
        internal static IReadOnlyList<Def> AllDefs()
        {
            var defs = new List<Def>();
            var seen = new HashSet<Def>(ReferenceEqualityComparer.Instance);
            foreach (var root in GenDefDatabase.AllDefTypesWithDatabases().Where(type => !type.IsAbstract))
            {
                foreach (var def in GenDefDatabase.GetAllDefsInDatabaseForDef(root))
                {
                    if (seen.Add(def)) defs.Add(def);
                }
            }
            return defs;
        }
    }
}
