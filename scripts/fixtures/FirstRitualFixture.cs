using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class IdeoligionDesignFixture
    {
        [Tool("test/ideoligion_design_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Stage an eligible fluid ideoligion and return a legal plain-precept reform without applying it.")]
        public async Task<object> PrepareDesign(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var ideo = Faction.OfPlayerSilentFail?.ideos?.PrimaryIdeo;
                if (!ModsConfig.IdeologyActive || ideo == null || Find.CurrentMap == null || !Find.TickManager.Paused)
                    return new { success = false, reason = "A paused Ideology lab is required." };
                ideo.Fluid = true;
                ideo.development.points = ideo.development.NextReformationDevelopmentPoints;
                var expected = IdeoligionReformActionHandler.ReadDesign(ideo);
                foreach (var old in ideo.PreceptsListForReading.Where(p => p.def.preceptClass == typeof(Precept)).OrderBy(p => p.def.defName, StringComparer.Ordinal))
                    foreach (var def in DefDatabase<PreceptDef>.AllDefs.Where(d => d.preceptClass == typeof(Precept) && d.issue == old.def.issue && d != old.def).OrderBy(d => d.defName, StringComparer.Ordinal))
                    {
                        var design = expected.Clone();
                        design.Precepts.Remove(old.def.defName); design.Precepts.Add(def.defName);
                        if (NativeIdeoligionDesign.Prepare(ideo, design, true, out _) != null) continue;
                        return new { success = true, ideoId = ideo.GetUniqueLoadID(), expected = Google.Protobuf.JsonFormatter.Default.Format(expected), design = Google.Protobuf.JsonFormatter.Default.Format(design), count = ideo.development.reformCount };
                    }
                return new { success = false, reason = "No legal plain-precept change is available in this fixture." };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/ideoligion_design_inspect", Description = "Read the disposable primary ideoligion's exact design and reform progression.")]
        public async Task<object> InspectDesign(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var ideo = Faction.OfPlayer.ideos.PrimaryIdeo;
                return new { design = Google.Protobuf.JsonFormatter.Default.Format(IdeoligionReformActionHandler.ReadDesign(ideo)), count = ideo.development?.reformCount ?? 0, points = ideo.development?.Points ?? 0 };
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    // Private disposable acceptance only: a colony with an
    // ideoligion holds its first ritual. test/first_ritual_prepare keeps the
    // lab colony's own primary ideoligion and picks the ritual it stages from
    // the game's defs: a ritual precept the ideoligion holds (or, when it holds
    // none that qualifies, one the game's precept defs offer and the fixture
    // adds) whose pattern the player may start at any time, whose obligation
    // target filter names a building the player can build, and whose required
    // roles the ideoligion holds precepts for. It finishes that building's
    // research, places the finished building, and ages the ritual's last
    // finished tick past its own cooldown, so the planner owes the ritual
    // from the first review. A candidate is accepted only when the game itself
    // offers an enabled begin command at the building (the same Command_Ritual
    // NativeRitualBegin takes). No ritual, building or role name is listed
    // here. Everything after the staging (the plan, the begin, the lord job and
    // its outcome) is the controller's and the game's.
    public sealed class FirstRitualFixture
    {
        [Tool("test/first_ritual_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture (#1665): stage one free-start ritual of the colony ideoligion with its finished required building and its cooldown passed. Requires Ideology.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (!ModsConfig.IdeologyActive) return Refuse("Ideology is not active.");
                var ideo = player.ideos?.PrimaryIdeo;
                if (ideo == null) return Refuse("The player faction has no primary ideoligion.");
                var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted)
                    .OrderBy(p => p.thingIDNumber).ToList();
                if (colonists.Count < 2) return Refuse("At least two existing colonists are required.");
                if (colonists.Any(p => p.Ideo != ideo)) return Refuse("Every colonist must hold the colony ideoligion.");
                if (map.listerBuildings.allBuildingsColonist.Count > 0) return Refuse("The disposable colony already has buildings; the fixture needs a blank lab.");

                // Free-start patterns with an obligation filter that names buildings.
                bool Qualifies(RitualPatternDef pattern) => pattern != null && (pattern.canStartAnytime || pattern.alwaysStartAnytime)
                    && pattern.ritualObligationTargetFilter?.thingDefs != null && pattern.ritualObligationTargetFilter.thingDefs.Count > 0;
                // Every required role bound to a role precept needs that precept held.
                bool RolesHeld(RitualPatternDef pattern) => (pattern.ritualBehavior?.roles ?? new List<RitualRole>())
                    .All(role => !role.required || role.precept == null || ideo.PreceptsListForReading.Any(p => p.def == role.precept));
                var held = ideo.PreceptsListForReading.OfType<Precept_Ritual>().OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal)
                    .Where(p => Qualifies(p.sourcePattern) && RolesHeld(p.sourcePattern)).ToList();
                var offered = held.Count > 0 ? new List<PreceptDef>() : DefDatabase<PreceptDef>.AllDefsListForReading
                    .Where(d => typeof(Precept_Ritual).IsAssignableFrom(d.preceptClass) && Qualifies(d.ritualPatternBase) && RolesHeld(d.ritualPatternBase)
                        && !ideo.PreceptsListForReading.Any(p => p.def == d))
                    .OrderBy(d => d.defName, StringComparer.Ordinal).ToList();
                var tried = new List<string>();
                var centre = map.Center;

                Precept_Ritual Choose(out ThingDef buildingDef, out Building building, out string refusal)
                {
                    buildingDef = null; building = null; refusal = null;
                    foreach (var candidate in held.Cast<Precept_Ritual>().Concat(offered.Select(def => AddRitual(ideo, def))).Where(r => r != null))
                    {
                        foreach (var def in candidate.sourcePattern.ritualObligationTargetFilter.thingDefs
                            .Where(d => d != null && d.BuildableByPlayer).OrderBy(d => d.defName, StringComparer.Ordinal))
                        {
                            var placed = PlaceBuilding(map, def, centre);
                            if (placed == null) { tried.Add(candidate.GetUniqueLoadID() + "@" + def.defName + ": no free cell"); continue; }
                            var why = BeginRefusal(candidate, placed);
                            if (why == null) { buildingDef = def; building = placed; return candidate; }
                            tried.Add(candidate.GetUniqueLoadID() + "@" + def.defName + ": " + why);
                            placed.Destroy(DestroyMode.Vanish);
                        }
                        // A precept the fixture added and could not stage leaves the ideoligion as it was.
                        if (!held.Contains(candidate)) ideo.RemovePrecept(candidate);
                    }
                    refusal = "No ritual of the colony ideoligion or the game's precept defs can be staged: " + string.Join("; ", tried);
                    return null;
                }
                var ritual = Choose(out var buildingDef, out var building, out var refusal);
                if (ritual == null) return Refuse(refusal);

                // The pattern's own cooldown passed, with the repeat penalty off.
                var pattern = ritual.sourcePattern;
                var cooldownTicks = (int)Math.Ceiling(pattern.ritualFreeStartIntervalDaysRange.min * GenDate.TicksPerDay);
                ritual.lastFinishedTick = Find.TickManager.TicksGame - cooldownTicks - GenDate.TicksPerDay;
                if (ritual.RepeatPenaltyActive) return Refuse("The repeat penalty of " + ritual.GetUniqueLoadID() + " is active after ageing its last finished tick.");

                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, center = new { x = centre.x, z = centre.z },
                    ideoId = ideo.GetUniqueLoadID(), pawnIds = colonists.Select(p => p.GetUniqueLoadID()).ToArray(),
                    ritualId = ritual.GetUniqueLoadID(), ritualDef = ritual.def.defName, pattern = pattern.defName,
                    added = !held.Contains(ritual), lastFinishedTick = ritual.lastFinishedTick,
                    buildingId = building.GetUniqueLoadID(), buildingDef = buildingDef.defName, spot = new { x = building.Position.x, z = building.Position.z },
                    requiredRoles = (pattern.ritualBehavior?.roles ?? new List<RitualRole>()).Where(r => r.required).Select(r => r.id).ToArray(),
                    rejected = tried.ToArray(),
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/first_ritual_inspect", Description = "Read-only ritual postcondition for the first-ritual fixture: whether a lord job of the ritual precept runs, its last finished tick and active obligations.")]
        public async Task<object> Inspect(IRimBridgeContext ctx, CancellationToken cancellationToken, string ritualId)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var ideo = Faction.OfPlayerSilentFail?.ideos?.PrimaryIdeo;
                if (ideo == null || !ModsConfig.IdeologyActive) return Refuse("An Ideology colony is required.");
                var ritual = ideo.PreceptsListForReading.OfType<Precept_Ritual>().FirstOrDefault(p => p.GetUniqueLoadID() == ritualId);
                if (ritual == null) return Refuse("The ideoligion holds no ritual precept " + ritualId + ".");
                var jobs = Find.Maps.SelectMany(map => map.lordManager.lords).Where(lord => lord.LordJob is LordJob_Ritual job && job.Ritual == ritual).ToList();
                return new {
                    success = true, tick = Find.TickManager.TicksGame, running = NativeRitualBegin.Running(ritual),
                    lordJobs = jobs.Count, participants = jobs.Sum(lord => lord.ownedPawns.Count),
                    lastFinishedTick = ritual.lastFinishedTick, activeObligations = ritual.activeObligations?.Count ?? 0,
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        // A ritual precept the game's def offers, added the way ideoligion
        // generation adds one; null when the game takes no pattern for it.
        private static Precept_Ritual AddRitual(Ideo ideo, PreceptDef def)
        {
            var precept = PreceptMaker.MakePrecept(def) as Precept_Ritual;
            if (precept == null) return null;
            ideo.AddPrecept(precept, true);
            return precept.sourcePattern == null ? null : precept;
        }

        // Researches def's prerequisites and places it finished, player-owned,
        // on the first cell near the centre where the game accepts it.
        private static Building PlaceBuilding(Map map, ThingDef def, IntVec3 centre)
        {
            foreach (var research in def.researchPrerequisites ?? new List<ResearchProjectDef>())
                Find.ResearchManager.FinishProject(research, false);
            for (var radius = 4; radius <= 16; radius += 2)
            {
                var cell = new IntVec3(centre.x + radius, 0, centre.z + radius);
                if (!GenConstruct.CanPlaceBlueprintAt(def, cell, Rot4.North, map).Accepted) continue;
                var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                thing.SetFaction(Faction.OfPlayer);
                return GenSpawn.Spawn(thing, cell, map, Rot4.North) as Building;
            }
            return null;
        }

        // Why the game offers no enabled begin command at the building (null
        // when it does): the command NativeRitualBegin takes.
        private static string BeginRefusal(Precept_Ritual ritual, Building building)
        {
            var commands = ritual.GetGizmoFor(new TargetInfo(building))?.OfType<Command_Ritual>().ToList() ?? new List<Command_Ritual>();
            if (commands.Count == 0) return "the game offers no begin command";
            foreach (var command in commands)
            {
                Traverse.Create(command).Method("ValidateDisabledState").GetValue();
                if (!command.Disabled) return null;
            }
            return commands[0].disabledReason ?? "the game disables the begin command";
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
