using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.PlayerApi;
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
    /// <see cref="Create"/>, <c>BrainCompositionOptions.StartingIdeoligion</c> is <see cref="StartingIdeoligion"/> and
    /// <c>BrainHostOptions.Presets</c> is <see cref="Presets"/>; the host start-up (#3115) installs them.
    /// </summary>
    /// <remarks>
    /// All run on a brain thread, never the game thread. The catalog reads its stats, stuffs and costs from the load's
    /// <see cref="NativeStatTable"/>, which the definition catalog read built on the game thread; the creation-time
    /// ideoligion reads none, so its catalog has the empty table. The Def databases are written once at boot, so reading
    /// them off the game thread after the Defs have loaded is safe.
    /// </remarks>
    internal static class NativeCatalogFactory
    {
        // The Def databases and the derived facts are fixed after boot, so one creation catalog serves every request; a
        // failed build is retried on the next call.
        private static readonly Lazy<PresetBook> Book = new(
            () => new PresetBook(RimGovernor.Brain.Bridge.ThemePresetCatalog.All, new BrainCatalog(AllDefs(), RimGovernor.Brain.Bridge.DefStatTable.Empty, NativeDefinitionCatalogTool.BrainDerived())),
            LazyThreadSafetyMode.PublicationOnly);

        /// <summary>
        /// The catalog of a load: every Def of the game, the derived facts the game computes in code, and the def
        /// stat table and race facts the load's definition catalog read built (<see cref="NativeStatTable"/>,
        /// <see cref="NativeRaceFacts"/>).
        /// </summary>
        internal static BrainCatalog Create(Common.Identity identity)
        {
            ArgumentNullException.ThrowIfNull(identity);
            return new BrainCatalog(AllDefs(), NativeStatTable.ForLoad(identity.LoadToken), NativeDefinitionCatalogTool.BrainDerived(), NativeRaceFacts.ForLoad(identity.LoadToken));
        }

        /// <summary>
        /// The shipped theme presets over the creation catalog (every Def, the empty stat table): what the goals window
        /// lists, what the host adopts for a themed new colony (#3511) and what <see cref="StartingIdeoligion"/> designs
        /// from. Built on first use; call it off the game thread.
        /// </summary>
        internal static PresetBook Presets() => Book.Value;

        /// <summary>
        /// The ideoligion the governor chooses for a new colony of <paramref name="scenario"/>: the best supported legal
        /// design for the scenario's player faction, over the game's Defs, which need no world and so work on the main menu.
        /// With a <paramref name="theme"/> preset id (#3511) the design holds that preset's memes, precepts and venerated
        /// animals as they resolve over the installed game.
        /// </summary>
        internal static Task<Common.IdeoligionDesign> StartingIdeoligion(string scenario, string? theme, CancellationToken cancellationToken)
            => Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                // ScenPart_PlayerFaction.factionDef is not public.
                var part = DefDatabase<ScenarioDef>.GetNamedSilentFail(scenario)?.scenario?.AllParts.OfType<ScenPart_PlayerFaction>().SingleOrDefault();
                var field = part == null ? null : AccessTools.Field(typeof(ScenPart_PlayerFaction), "factionDef");
                var faction = (field?.GetValue(part) as FactionDef)?.defName;
                if (string.IsNullOrEmpty(faction))
                    throw new InvalidOperationException("The creation scenario lacks its player faction.");
                var book = Presets();
                var catalog = (BrainCatalog)book.Catalog;
                IReadOnlyList<IdeoGoalTarget> targets = [];
                if (theme != null)
                {
                    var preset = book.Find(theme) ?? throw new InvalidOperationException("No theme preset is named " + theme + ".");
                    targets = GoalRules.IdeoTargets(PresetRules.Resolve(preset, catalog, new ThemeRecord { Name = preset.Name, Preset = preset.Id }).Goals());
                }
                var options = catalog.IdeoligionOptions(faction);
                if (IdeoligionDesignRules.ChooseStartingIdeoligion(options, targets) is not { } choice)
                    throw new InvalidOperationException(theme == null
                        ? "No supported legal ideoligion design can be established."
                        : "No supported legal ideoligion design holds the " + theme + " theme's memes, precepts and animals.");
                return Orders.WireIdeoligionDesign(choice.Design);
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
