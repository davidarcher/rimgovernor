using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    /// <summary>
    /// The load's <see cref="DefStatTable"/>: the game's own <c>StatWorker</c> evaluation of every declared catalog stat
    /// (<see cref="CatalogStats.All"/>), plus <c>GenStuff.AllowedStuffsFor</c> and <c>CostListAdjusted</c>, for every
    /// ThingDef with no stuff and with each allowed stuff and for every TerrainDef, computed in one game-thread pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When. The definition catalog read (<see cref="NativeDefinitionCatalogTool.HandleReadDefinitionCatalog"/>, a
    /// game-thread hop the brain makes before it builds its catalog) runs <see cref="EnsureForLoad"/>: the first read of
    /// a load builds the table, later reads of the same load reuse it. <see cref="NativeCatalogFactory.Create"/> then
    /// takes the published table on the brain thread with <see cref="ForLoad"/>; the hop's completion orders the
    /// publish before that read. The table is immutable, so the brain reads it with no further game hop.
    /// </para>
    /// <para>
    /// Why per load. The values are def-level (<c>GetStatValueAbstract</c>, no thing), but stat parts read the
    /// scenario and difficulty of the loaded game, so a new load rebuilds the table. Within a load they are taken as
    /// fixed. A stat value is computed when the game shows the stat for the subject, and for every subject when the
    /// stat is <see cref="CatalogStat.ReadHidden"/>. A value the game cannot compute (an exception or a non-finite
    /// number) is kept as that failure, which the brain reads as a <see cref="StatException"/>.
    /// </para>
    /// </remarks>
    internal static class NativeStatTable
    {
        private sealed class Published
        {
            internal readonly Game? Game;
            internal readonly string LoadToken;
            internal readonly DefStatTable Table;
            internal Published(Game? game, string loadToken, DefStatTable table) { Game = game; LoadToken = loadToken; Table = table; }
        }

        private static volatile Published? latest;

        /// <summary>On the game thread: builds the table of the loaded game unless it is already published for it.</summary>
        internal static void EnsureForLoad(string loadToken)
        {
            var game = Current.Game;
            var held = latest;
            if (held != null && ReferenceEquals(held.Game, game) && held.LoadToken == loadToken) return;
            latest = new Published(game, loadToken, Build());
        }

        /// <summary>On any thread: the table the definition catalog read of <paramref name="loadToken"/> published.</summary>
        internal static DefStatTable ForLoad(string loadToken)
        {
            var held = latest;
            return held != null && held.LoadToken == loadToken
                ? held.Table
                : throw new InvalidOperationException($"No def stat table is published for load {loadToken}: the load's definition catalog read builds it.");
        }

        /// <summary>On the game thread: one pass over every ThingDef and TerrainDef; logs its size and cost.</summary>
        internal static DefStatTable Build()
        {
            var watch = Stopwatch.StartNew();
            // The catalog's stats are declared by StatDef name in the brain (CatalogStats); one lookup per stat per pass.
            var stats = new List<(CatalogStat Stat, StatDef Def)>();
            foreach (var stat in CatalogStats.All)
            {
                var def = DefDatabase<StatDef>.GetNamedSilentFail(stat.Name);
                if (def != null) stats.Add((stat, def));
            }
            var subjects = new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost>();
            var values = new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value>();
            var stuffs = new Dictionary<BuildableDef, IReadOnlyList<string>>();
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading) Def(def, stats, subjects, values, stuffs);
            foreach (var def in DefDatabase<TerrainDef>.AllDefsListForReading) Def(def, stats, subjects, values, stuffs);
            var table = new DefStatTable(subjects, values, stuffs, new HashSet<CatalogStat>(stats.Select(s => s.Stat)));
            Log.Message($"[RimGovernor] Def stat table: {subjects.Count} subjects, {values.Count} values over {stats.Count} stats in {watch.ElapsedMilliseconds} ms.");
            return table;
        }

        private static void Def(BuildableDef def, List<(CatalogStat Stat, StatDef Def)> stats,
            Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost> subjects,
            Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value> values,
            Dictionary<BuildableDef, IReadOnlyList<string>> stuffs)
        {
            List<ThingDef> allowed;
            try { allowed = GenStuff.AllowedStuffsFor(def).ToList(); }
            catch (Exception) { allowed = new List<ThingDef>(); }
            stuffs[def] = allowed.Select(s => s.defName).ToList();
            Row(def, null, stats, subjects, values);
            foreach (var stuff in allowed) Row(def, stuff, stats, subjects, values);
        }

        private static void Row(BuildableDef def, ThingDef? stuff, List<(CatalogStat Stat, StatDef Def)> stats,
            Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost> subjects,
            Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value> values)
        {
            subjects[(def, stuff)] = CostOf(def, stuff);
            var request = StatRequest.For(def, stuff, QualityCategory.Normal);
            foreach (var (stat, statDef) in stats)
            {
                bool shown;
                try { shown = statDef.Worker.ShouldShowFor(request); }
                catch (Exception error)
                {
                    values[(def, stuff, stat)] = new DefStatTable.Value(0, false, Failed(stat, def, stuff, error));
                    continue;
                }
                if (!shown && !stat.ReadHidden) continue;
                try
                {
                    var value = def.GetStatValueAbstract(statDef, stuff);
                    values[(def, stuff, stat)] = float.IsFinite(value)
                        ? new DefStatTable.Value(value, shown, null)
                        : new DefStatTable.Value(0, shown, $"stat {stat.Name} of {def.defName} is {value}");
                }
                catch (Exception error)
                {
                    values[(def, stuff, stat)] = new DefStatTable.Value(0, shown, Failed(stat, def, stuff, error));
                }
            }
        }

        private static DefStatTable.Cost CostOf(BuildableDef def, ThingDef? stuff)
        {
            if (stuff == null && def.MadeFromStuff) return new DefStatTable.Cost(null, $"{def.defName} is made from stuff; its cost needs one");
            try
            {
                var costs = def.CostListAdjusted(stuff, false);
                return new DefStatTable.Cost(costs == null ? Array.Empty<StatCost>() : costs.Select(c => new StatCost(c.thingDef.defName, c.count)).ToList(), null);
            }
            catch (Exception error)
            {
                return new DefStatTable.Cost(null, $"the cost of {def.defName} with stuff \"{stuff?.defName}\" failed in the game: {error.GetType().Name}: {error.Message}");
            }
        }

        private static string Failed(CatalogStat stat, BuildableDef def, ThingDef? stuff, Exception error)
            => $"stat {stat.Name} of {def.defName} with stuff \"{stuff?.defName}\" failed in the game: {error.GetType().Name}: {error.Message}";
    }
}
