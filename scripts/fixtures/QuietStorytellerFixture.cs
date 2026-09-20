using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable test setup only (issue #92). Acceptance scenarios rerun for
    // reasons unrelated to their assertion: a raid, a manhunter pack, a cold
    // snap or a mental break that holds the clock for as long as it lasts.
    // Apply quiets the storyteller at the source so only the fixture's own
    // events happen; interruption harnesses simply do not call it.
    public static class QuietStoryteller
    {
        private static bool patched;
        private static DifficultyDef Custom => DefDatabase<DifficultyDef>.AllDefsListForReading.Single(d => d.isCustom);

        // RimWorld saves a preset difficulty by def name only and rebuilds the
        // storyteller comps on load, so the quiet state has to be carried by
        // the one thing a save does persist in full: a Custom difficulty. A
        // Custom difficulty at zero threat scale is the quiet marker; the
        // StorytellerTick prefix below honours it after every reload while a
        // fixture build is installed.
        public static bool IsQuiet(Storyteller storyteller)
        {
            return storyteller?.difficultyDef?.isCustom == true && storyteller.difficulty.threatScale == 0f;
        }

        // Difficulty only scales incidents: even at zero threat a manhunter
        // pack of one still fires. Clearing the storyteller comps means no
        // incidents at all; the queue drops anything already scheduled.
        public static void ApplyDifficulty(Storyteller storyteller)
        {
            var difficulty = storyteller.difficulty;
            difficulty.colonistMoodOffset = 30f;
            difficulty.threatScale = 0f;
            difficulty.allowBigThreats = false;
            difficulty.allowIntroThreats = false;
            difficulty.allowViolentQuests = false;
            difficulty.predatorsHuntHumanlikes = false;
            difficulty.manhunterChanceOnDamageFactor = 0f;
            storyteller.difficultyDef = Custom;
            storyteller.storytellerComps.Clear();
            storyteller.incidentQueue.Clear();
            EnsurePatched();
        }

        // A predator or hostile already on the random map would hold the
        // window from tick one, so every non-colony pawn leaves the map.
        public static int RemoveStrangers(Map map)
        {
            var strangers = map.mapPawns.AllPawnsSpawned.Where(p => p.Faction != Faction.OfPlayer).ToList();
            foreach (var stranger in strangers) stranger.Destroy(DestroyMode.Vanish);
            return strangers.Count;
        }

        // A map-gen insect hive is a hostile building the census lists as a
        // combat target (#246) and a spawner of the very insects RemoveStrangers
        // just removed, so every hive leaves the map too (#340). A case that
        // wants a hive spawns its own after this (defense/hive).
        public static int RemoveHives(Map map)
        {
            var hives = map.listerThings.AllThings.OfType<Hive>().ToList();
            foreach (var hive in hives) hive.Destroy(DestroyMode.Vanish);
            return hives.Count;
        }

        public static object Apply(Map map)
        {
            ApplyDifficulty(Find.Storyteller);
            var removed = map == null ? 0 : RemoveStrangers(map);
            var hives = map == null ? 0 : RemoveHives(map);
            return new { success = true, storyteller = Find.Storyteller.def.defName, difficulty = Custom.defName,
                threatScale = 0f, comps = 0, strangersRemoved = removed, hivesRemoved = hives };
        }

        public static void EnsurePatched()
        {
            if (patched) return;
            var harmony = new Harmony("rimgovernor.test.quiet-storyteller");
            harmony.Patch(
                AccessTools.Method(typeof(Storyteller), nameof(Storyteller.StorytellerTick)),
                prefix: new HarmonyMethod(typeof(QuietStoryteller), nameof(SkipTick)));
            harmony.Patch(
                AccessTools.Method(typeof(InspirationHandler), nameof(InspirationHandler.InspirationHandlerTickInterval)),
                prefix: new HarmonyMethod(typeof(QuietStoryteller), nameof(SkipInspiration)));
            harmony.Patch(
                AccessTools.Method(typeof(Pawn_InteractionsTracker), nameof(Pawn_InteractionsTracker.SocialFightChance)),
                postfix: new HarmonyMethod(typeof(QuietStoryteller), nameof(QuietSocialFightChance)));
            patched = true;
        }

        // No storyteller tick at all while quiet: the comps RimWorld rebuilt
        // on load never get to fire, and nothing queued is delivered. Fixture
        // ops that execute an incident directly are unaffected.
        private static bool SkipTick(Storyteller __instance) => !IsQuiet(__instance);

        // No inspiration rolls either: an inspiration comes from the pawn's
        // own handler, not the storyteller, and its PositiveEvent letter was
        // the one event a quiet colony still delivered mid-raid (#228).
        private static bool SkipInspiration() => !IsQuiet(Find.Storyteller);

        // Social fights roll during pawn interactions, independently of mood
        // and storyteller incidents. Keep ordinary interactions, but suppress
        // their random fights in quiet fixtures. Explicitly staged fights still
        // use StartSocialFight and are unaffected.
        private static void QuietSocialFightChance(ref float __result)
        {
            if (IsQuiet(Find.Storyteller)) __result = 0f;
        }
    }

    // Disposable test setup only (#272). Sets the loaded game's QuietWorld
    // marker (AcceptanceWorld, persisted with the save): under a game
    // launched with -rimgovernor-test-acceleration, wild plants and wild
    // animals outside the home area (and outside any growing zone) stop
    // ticking and the wild spawners stop. A case whose assertion watches
    // the wild map (farming, husbandry, hunting) must not call it.
    public sealed class QuietWorldFixture
    {
        [Tool("test/quiet_world", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup: mark the loaded game quiet-world so, under -rimgovernor-test-acceleration, wild plants and animals outside the home area stop ticking and the wild spawners stop. Persists across save and reload. Farm, husbandry and hunting harnesses must not call it.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "apply (default), release or inspect.")] string action = "apply")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (Current.Game == null) throw new InvalidOperationException("A loaded game is required.");
                if (action == "apply") AcceptanceWorld.SetQuiet(true);
                else if (action == "release") AcceptanceWorld.SetQuiet(false);
                else if (action != "inspect") throw new ArgumentException("Unknown action.");
                var marker = Current.Game.GetComponent<AcceptanceWorld>()?.QuietWorld ?? false;
                return new { success = true, quietWorld = marker, active = AcceptanceWorld.Quiet, launched = AcceptanceWorld.Launched };
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public sealed class QuietStorytellerFixture
    {
        // The tick prefix must be live before a quiet save is reloaded, not
        // only after Apply, so it installs as soon as the bridge discovers
        // this tool.
        static QuietStorytellerFixture() { QuietStoryteller.EnsurePatched(); }
        public QuietStorytellerFixture() { QuietStoryteller.EnsurePatched(); }

        [Tool("test/quiet_storyteller", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup: Custom difficulty at zero threat scale with no big/intro threats, violent quests or humanlike-hunting predators, no storyteller comps, queued incidents or storyteller ticks, and every non-colony pawn and insect hive removed from the current map. Persists across save and reload. Interruption harnesses must not call it.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "apply (default) or inspect, which only reads the current storyteller state.")] string action = "apply")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (Current.Game == null || Find.Storyteller == null) throw new InvalidOperationException("A loaded game is required.");
                if (action == "inspect")
                {
                    var teller = Find.Storyteller;
                    return new { success = true, quiet = QuietStoryteller.IsQuiet(teller), storyteller = teller.def.defName,
                        difficulty = teller.difficultyDef?.defName, threatScale = teller.difficulty.threatScale,
                        allowBigThreats = teller.difficulty.allowBigThreats, comps = teller.storytellerComps.Count,
                        strangers = Find.CurrentMap?.mapPawns.AllPawnsSpawned.Count(p => p.Faction != Faction.OfPlayer) ?? 0,
                        hives = Find.CurrentMap?.listerThings.AllThings.OfType<Hive>().Count() ?? 0 };
                }
                if (action != "apply") throw new ArgumentException("Unknown action.");
                return QuietStoryteller.Apply(Find.CurrentMap);
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
