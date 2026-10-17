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
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The definition catalog the in-process brain plans over (#3116). The catalog reply carries what the game computes
    /// in code (the full rot rate, class chains, Biotech facts) but not the def rows or game constants
    /// (#3100): the brain's <see cref="BrainCatalog"/> is built over the game's own Def objects, so its builders live
    /// here, in the one assembly that may read the Def databases. <c>BrainCompositionOptions.CatalogFactory</c> is
    /// <see cref="Create"/> and <c>BrainCompositionOptions.StartingIdeoligion</c> is <see cref="StartingIdeoligion"/>;
    /// the host start-up (#3115) installs both.
    /// </summary>
    /// <remarks>
    /// Both run on a brain thread, never the game thread. The catalog reads its stats, stuffs and costs from the load's
    /// <see cref="NativeStatTable"/>, which the definition catalog read built on the game thread; the creation-time
    /// ideoligion reads none, so its catalog has the empty table. The Def databases are written once at boot, so reading
    /// them off the game thread after the Defs have loaded is safe.
    /// </remarks>
    internal static class NativeCatalogFactory
    {
        /// <summary>
        /// The catalog of a load: every Def of the game, the derived facts the game computes in code, and the def
        /// stat table and race facts the load's definition catalog read built (<see cref="NativeStatTable"/>,
        /// <see cref="NativeRaceFacts"/>).
        /// </summary>
        internal static BrainCatalog Create(Obs.DefinitionCatalog wire, Common.Identity identity)
        {
            ArgumentNullException.ThrowIfNull(wire);
            ArgumentNullException.ThrowIfNull(identity);
            return new BrainCatalog(AllDefs(), NativeStatTable.ForLoad(identity.LoadToken), NativeDefinitionCatalogTool.BrainDerived(), NativeRaceFacts.ForLoad(identity.LoadToken));
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
                var catalog = new BrainCatalog(AllDefs(), RimGovernor.Brain.Bridge.DefStatTable.Empty, NativeDefinitionCatalogTool.BrainDerived());
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
