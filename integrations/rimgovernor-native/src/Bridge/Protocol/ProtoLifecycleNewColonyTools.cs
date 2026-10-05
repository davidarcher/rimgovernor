#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Lifecycle = RimGovernor.Protocol.Lifecycle;

namespace HomeBridge.BridgeTools
{
    // New-colony start (#2019/#2021): from a fresh main menu, run RimWorld's
    // own init sequence directly (no Harmony on Root_Play.SetupForQuickTestPlay)
    // up to a live map. The tool returns NewColonyPending at once; the work
    // runs as consecutive synchronous long events, one per phase, so the poll
    // is answered between phases (a phase itself blocks the main thread):
    //   GeneratingWorld -> ChoosingTile -> RollingColonists -> GeneratingMap
    // and then the live-map hand-off (below). Tile choice and starting-pawn
    // generation run under Rand.PushState(StableStringHash(seed)), one push per
    // phase, so the same spec on the same install gives the same world, tile
    // and colonists (per-install determinism only: DLC and mods shift draws).
    //
    // Live map (#2022): OnMapLive pauses; polls then confirm the colony-naming
    // dialog (FINISHING), report SAVING, and save through the same native save
    // path as lifecycle_save into the game's Saves folder under the spec's save
    // name, ending the entry with NewColonyCompleted. A phase exception, a failed
    // save or the request's timeout ends it with a failure.
    public sealed class ProtoLifecycleNewColonyTools
    {
        private const string NewColonyToolName = "rimgovernor/lifecycle_new_colony";
        private const string ReadNewColonyToolName = "rimgovernor/lifecycle_read_new_colony";
        private const int MaxEntries = 32;

        private static readonly object Lock = new object();
        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();
        private static string? activeRequestId;

        private sealed class Refusal : Exception
        {
            public readonly Common.FailureCode Code;
            public Refusal(Common.FailureCode code, string message) : base(message) { Code = code; }
        }

        private sealed class Entry
        {
            public readonly string RequestId;
            public readonly Lifecycle.NewColonySpec Spec;
            public readonly string Seed; // The seed actually used (random when the spec's is empty).
            public readonly DateTime StartedUtc = DateTime.UtcNow;
            public readonly uint? TimeoutMs;
            public readonly ResolvedSpec Resolved;
            public Lifecycle.NewColonyPhase Phase = Lifecycle.NewColonyPhase.GeneratingWorld;
            public string Detail = "";
            public uint Rerolls;
            public bool Aborted; // A phase failed; later queued phases do nothing.
            public bool MapIsLive;
            public DateTime LiveUtc;
            public Lifecycle.NewColonyReply? Reply; // Terminal outcome; null while pending.
            public Entry(string requestId, Lifecycle.NewColonySpec spec, string seed, uint? timeoutMs, ResolvedSpec resolved)
            {
                RequestId = requestId; Spec = spec; Seed = seed; TimeoutMs = timeoutMs; Resolved = resolved;
            }
        }

        private sealed class ResolvedSpec
        {
            public ScenarioDef Scenario = null!;
            public DifficultyDef Difficulty = null!;
            public StorytellerDef Storyteller = null!;
            public OverallTemperature WorldTemperature;
            public float? MinTemperature, MaxTemperature;
            public List<BiomeDef> Biomes = new List<BiomeDef>();
        }

        [Tool(NewColonyToolName, Title = "Start a new colony",
            Description = "Start generating a colony from a spec on the main menu. Returns NewColonyPending; poll rimgovernor/lifecycle_read_new_colony.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.lifecycle.v1.NewColonyReply.", Always = true)]
        public async Task<object> NewColony(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official lifecycle NewColonyRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, NewColonyToolName, request, Lifecycle.NewColonyRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Lifecycle.NewColonyReply { Failure = failure });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => StartNewColony(parsed), cancellationToken).ConfigureAwait(false);
        }

        [Tool(ReadNewColonyToolName, Title = "Read a new colony's progress",
            Description = "Poll a request_id from rimgovernor/lifecycle_new_colony for NewColonyCompleted/NewColonyPending/NewColonySuperseded/Failure.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.lifecycle.v1.NewColonyReply.", Always = true)]
        public async Task<object> ReadNewColony(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official lifecycle RequestStatus ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ReadNewColonyToolName, request, Lifecycle.RequestStatus.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Lifecycle.NewColonyReply { Failure = failure });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => PollNewColony(parsed), cancellationToken).ConfigureAwait(false);
        }

        private static Lifecycle.NewColonyReply Fail(Common.FailureCode code, string detail) =>
            new Lifecycle.NewColonyReply { Failure = ProtoBoundary.Fail(code, detail) };

        // Call only on the game thread.
        internal static Lifecycle.NewColonyReply StartNewColony(Lifecycle.NewColonyRequest request)
        {
            if (request == null || !request.HasRequestId || !ProtoBoundary.IsIdentifier(request.RequestId) || request.Spec == null)
                return Fail(Common.FailureCode.InvalidRequest, "New colony requires a request id and a spec.");

            lock (Lock)
            {
                Prune();
                if (activeRequestId != null && Entries.TryGetValue(activeRequestId, out var previous) && previous.Reply == null)
                {
                    // A retry of the request already running answers its status.
                    if (activeRequestId == request.RequestId) return PendingReply(previous);
                    return Fail(Common.FailureCode.Unavailable, "A new colony (" + previous.RequestId + ") is already being generated.");
                }
            }

            // From a fresh main menu only.
            if (Current.ProgramState != ProgramState.Entry || Current.Game != null || Find.CurrentMap != null)
                return Fail(Common.FailureCode.Unavailable, "A new colony starts only from a fresh main menu: a game is already loaded or starting.");
            if (LongEventHandler.AnyEventNowOrWaiting)
                return Fail(Common.FailureCode.Unavailable, "The game is busy with another long event.");

            ResolvedSpec resolved;
            try { resolved = Resolve(request.Spec); }
            catch (Refusal refusal) { return Fail(refusal.Code, refusal.Message); }

            var seed = request.Spec.Seed.Trim();
            if (seed.Length == 0) seed = GenText.RandomSeedString(); // Defensive: Go requires a non-empty seed.
            var entry = new Entry(request.RequestId, request.Spec, seed, request.HasTimeoutMs ? (uint?)request.TimeoutMs : null, resolved);
            lock (Lock)
            {
                Entries[entry.RequestId] = entry;
                activeRequestId = entry.RequestId;
            }

            QueuePhase(entry, Lifecycle.NewColonyPhase.GeneratingWorld, "GeneratingMap", "Generating the world", GenerateWorld);
            QueuePhase(entry, Lifecycle.NewColonyPhase.ChoosingTile, "GeneratingMap", "Choosing the settlement tile", ChooseTile);
            QueuePhase(entry, Lifecycle.NewColonyPhase.RollingColonists, "GeneratingMap", "Rolling the starting colonists", RollColonists);
            // Vanilla's own continuation after the starting-pawn page: loads the
            // Play scene, where Root_Play.Start runs Game.InitNewGame.
            QueuePhase(entry, Lifecycle.NewColonyPhase.GeneratingMap, "GeneratingMap", "Generating the map", _ => PageUtility.InitGameStart());
            return PendingReply(entry);
        }

        private static void QueuePhase(Entry entry, Lifecycle.NewColonyPhase phase, string textKey, string detail, Action<Entry> work)
        {
            LongEventHandler.QueueLongEvent(() =>
            {
                if (entry.Aborted) return;
                entry.Phase = phase; entry.Detail = detail;
                try { work(entry); }
                catch (Exception error) { Abort(entry, error); }
            }, textKey, doAsynchronously: false, exceptionHandler: error => Abort(entry, error));
        }

        private static void Abort(Entry entry, Exception error)
        {
            entry.Aborted = true;
            var refusal = error as Refusal;
            Log.Warning("[RimGovernor] new colony " + entry.RequestId + " failed in " + entry.Phase + ": " + error);
            // Back to a clean main menu so a later request can run.
            try { Current.Game = null; Current.ProgramState = ProgramState.Entry; } catch (Exception) { }
            Complete(entry, Fail(refusal?.Code ?? Common.FailureCode.NativeFailure,
                refusal != null ? refusal.Message : "New colony failed in " + entry.Phase + ": " + error.GetType().Name + ": " + error.Message));
        }

        // The spec resolved to defs; an unknown name lists the valid ones.
        private static ResolvedSpec Resolve(Lifecycle.NewColonySpec spec)
        {
            var resolved = new ResolvedSpec();
            if (!spec.HasColonistCount || spec.ColonistCount < 1 || spec.ColonistCount > 10)
                throw new Refusal(Common.FailureCode.InvalidRequest, "colonist_count must be within 1..10.");
            if (!spec.HasMapSize || spec.MapSize < 100 || spec.MapSize > 400)
                throw new Refusal(Common.FailureCode.InvalidRequest, "map_size must be within 100..400.");
            if (!spec.HasPlanetCoverage || float.IsNaN(spec.PlanetCoverage) || spec.PlanetCoverage < 0.05f || spec.PlanetCoverage > 1f)
                throw new Refusal(Common.FailureCode.InvalidRequest, "planet_coverage must be within 0.05..1.");
            resolved.Scenario = Named(DefDatabase<ScenarioDef>.AllDefsListForReading, spec.Scenario, "scenario");
            resolved.Difficulty = Named(DefDatabase<DifficultyDef>.AllDefsListForReading, spec.Difficulty, "difficulty");
            resolved.Storyteller = Named(DefDatabase<StorytellerDef>.AllDefsListForReading, spec.Storyteller, "storyteller");
            if (resolved.Scenario.scenario.AllParts.OfType<ScenPart_ConfigPage_ConfigureStartingPawns>().SingleOrDefault() == null)
                throw new Refusal(Common.FailureCode.InvalidRequest, "Scenario " + spec.Scenario + " has no editable starting-pawn count.");
            var settleable = DefDatabase<BiomeDef>.AllDefsListForReading.Where(b => b.canBuildBase).ToList();
            foreach (var name in spec.Biomes)
                resolved.Biomes.Add(Named(settleable, name, "biome"));
            var temperatureName = spec.HasWorldTemperature ? spec.WorldTemperature : nameof(OverallTemperature.Normal);
            if (!Enum.TryParse(temperatureName, out OverallTemperature temperature) || !Enum.IsDefined(typeof(OverallTemperature), temperature))
                throw new Refusal(Common.FailureCode.InvalidRequest, "Unknown world_temperature " + temperatureName + "; valid: "
                    + string.Join(", ", Enum.GetNames(typeof(OverallTemperature))) + ".");
            resolved.WorldTemperature = temperature;
            if (spec.HasMinTemperature) resolved.MinTemperature = spec.MinTemperature;
            if (spec.HasMaxTemperature) resolved.MaxTemperature = spec.MaxTemperature;
            if (resolved.MinTemperature > resolved.MaxTemperature)
                throw new Refusal(Common.FailureCode.InvalidRequest, "min_temperature exceeds max_temperature.");
            return resolved;
        }

        private static T Named<T>(List<T> defs, string name, string field) where T : Def
        {
            var found = defs.FirstOrDefault(d => d.defName == name);
            if (found == null)
                throw new Refusal(Common.FailureCode.InvalidRequest, "Unknown " + field + " \"" + name + "\"; valid: "
                    + string.Join(", ", defs.Select(d => d.defName).OrderBy(n => n, StringComparer.Ordinal)) + ".");
            return found;
        }

        // Phase 1: Game, scenario copy with the colonist count, storyteller, world.
        private static void GenerateWorld(Entry entry)
        {
            var spec = entry.Spec; var resolved = entry.Resolved;
            Current.ProgramState = ProgramState.Entry;
            Game.ClearCaches();
            Current.Game = new Game();
            Current.Game.InitData = new GameInitData();
            var scenario = resolved.Scenario.scenario.CopyForEditing();
            var part = scenario.AllParts.OfType<ScenPart_ConfigPage_ConfigureStartingPawns>().Single();
            part.pawnCount = (int)spec.ColonistCount;
            part.pawnChoiceCount = Math.Max((int)spec.ColonistCount, part.pawnChoiceCount);
            Current.Game.Scenario = scenario;
            Find.Scenario.PreConfigure();
            Current.Game.storyteller = new Storyteller(resolved.Storyteller, resolved.Difficulty);
            Current.Game.World = WorldGenerator.GenerateWorld(spec.PlanetCoverage, entry.Seed,
                OverallRainfall.Normal, resolved.WorldTemperature, OverallPopulation.Normal, LandmarkDensity.Normal);
        }

        // Phase 2: the seeded pick among valid tiles (DebugStart's rule), then the map size.
        private static void ChooseTile(Entry entry)
        {
            var resolved = entry.Resolved; var spec = entry.Spec;
            Rand.PushState(GenText.StableStringHash(entry.Seed));
            try
            {
                var constrained = resolved.Biomes.Count > 0 || spec.FlatTile || resolved.MinTemperature.HasValue || resolved.MaxTemperature.HasValue;
                if (!constrained)
                    Find.GameInitData.ChooseRandomStartingTile();
                else
                {
                    var surface = Find.WorldGrid.Surface;
                    var valid = Enumerable.Range(0, surface.TilesCount).Select(i => surface[i])
                        .Where(t => TileFinder.IsValidTileForNewSettlement(t.tile)).ToList();
                    if (valid.Count == 0)
                        throw new Refusal(Common.FailureCode.InvalidRequest, "This planet has no valid settlement tile.");
                    var band = valid.Where(t => (!resolved.MinTemperature.HasValue || GenTemperature.MinTemperatureAtTile(t.tile) >= resolved.MinTemperature.Value)
                        && (!resolved.MaxTemperature.HasValue || GenTemperature.MaxTemperatureAtTile(t.tile) <= resolved.MaxTemperature.Value)).ToList();
                    var bandText = "seasonal temperature " + (resolved.MinTemperature?.ToString("0.#") ?? "-inf") + ".." + (resolved.MaxTemperature?.ToString("0.#") ?? "+inf") + " C";
                    if (band.Count == 0)
                        throw new Refusal(Common.FailureCode.InvalidRequest, "No valid settlement tile on this planet meets " + bandText + ".");
                    var chosen = band;
                    if (resolved.Biomes.Count > 0)
                    {
                        chosen = resolved.Biomes.Select(b => band.Where(t => t.PrimaryBiome == b).ToList()).FirstOrDefault(c => c.Count > 0)!;
                        if (chosen == null)
                            throw new Refusal(Common.FailureCode.InvalidRequest, "No valid settlement tile in any requested biome ("
                                + string.Join(", ", resolved.Biomes.Select(b => b.defName)) + ") meets " + bandText + " on this planet.");
                    }
                    if (spec.FlatTile)
                    {
                        // Flat, river-free, road-free, mutator-free where the planet offers one;
                        // otherwise drop the mutator requirement, then the river and road one.
                        var plain = chosen.Where(t => t.hilliness == Hilliness.Flat && t.Rivers.NullOrEmpty() && t.Roads.NullOrEmpty()).ToList();
                        var bare = plain.Where(t => t.Mutators.Count == 0).ToList();
                        var pick = bare.Count > 0 ? bare : plain.Count > 0 ? plain : chosen;
                        if (pick != bare)
                            Log.Warning("[RimGovernor] new colony: no " + (pick == plain ? "mutator-free flat" : "flat river-free") + " tile on this planet; settling a "
                                + (pick == plain ? "flat tile with mutators" : "tile of the roll's own terrain") + ".");
                        chosen = pick;
                    }
                    Find.GameInitData.startingTile = chosen.RandomElement().tile;
                }
                Find.GameInitData.mapSize = (int)spec.MapSize;
            }
            finally { Rand.PopState(); }
        }

        // Phase 3: the starting pawns (generated by the scenario's PostIdeoChosen).
        private static void RollColonists(Entry entry)
        {
            Rand.PushState(GenText.StableStringHash(entry.Seed));
            try
            {
                Find.Scenario.PostIdeoChosen();
                TeamPolicy(entry);
            }
            finally { Rand.PopState(); }
        }

        // Reroll hook for #2024's team-composition policy: runs on the freshly
        // generated starting pawns, inside the seeded Rand scope, and counts
        // rerolls into the reported reroll_count. #2024 replaces the body.
        // Interim minimal policy (the old DebugStart.EnsureCapableColonists, so
        // the colony is usable until #2024): every colonist can Construct and Haul.
        private const int MaxRerollsPerPawn = 40;

        private static bool CapableColonist(Pawn p) =>
            p != null && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)
            && (p.skills?.GetSkill(SkillDefOf.Construction).Level ?? 0) >= ThingDefOf.Wall.constructionSkillPrerequisite;

        private static void TeamPolicy(Entry entry)
        {
            var pawns = Find.GameInitData.startingAndOptionalPawns;
            for (var i = 0; i < Find.GameInitData.startingPawnCount && i < pawns.Count; i++)
            {
                var tries = 0;
                while (!CapableColonist(pawns[i]))
                {
                    if (++tries > MaxRerollsPerPawn)
                        throw new Refusal(Common.FailureCode.NativeFailure, "Starting pawn " + i + " was incapable of Construction or Hauling after " + MaxRerollsPerPawn + " rerolls.");
                    StartingPawnUtility.RandomizeInPlace(pawns[i]);
                    entry.Rerolls++;
                }
            }
        }

        // Call only on the game thread.
        internal static Lifecycle.NewColonyReply PollNewColony(Lifecycle.RequestStatus status)
        {
            if (status == null || !status.HasRequestId || !ProtoBoundary.IsIdentifier(status.RequestId))
                return Fail(Common.FailureCode.InvalidRequest, "ReadNewColony requires a request id.");
            Entry entry;
            lock (Lock)
            {
                if (!Entries.TryGetValue(status.RequestId, out entry))
                    return Fail(Common.FailureCode.NotFound, "Unknown or expired new colony request id.");
                if (entry.Reply != null) return entry.Reply;
            }

            // The timeout bounds the whole start, saving included (a save is one
            // synchronous call, so it is never cut mid-write).
            if (entry.TimeoutMs.HasValue && DateTime.UtcNow - entry.StartedUtc > TimeSpan.FromMilliseconds(entry.TimeoutMs.Value))
            {
                entry.Aborted = true;
                return Complete(entry, Fail(Common.FailureCode.NativeFailure,
                    "New colony did not finish within the requested timeout (last phase " + entry.Phase + ")."));
            }
            if (entry.Phase == Lifecycle.NewColonyPhase.GeneratingMap || entry.Phase == Lifecycle.NewColonyPhase.Finishing
                || entry.Phase == Lifecycle.NewColonyPhase.Saving)
            {
                if (!LongEventHandler.AnyEventNowOrWaiting)
                {
                    if (Current.Game == null)
                        return Complete(entry, Fail(Common.FailureCode.NativeFailure, "Map generation failed: the game returned to the main menu."));
                    if (!entry.MapIsLive && ProtoBoundary.TryReadContext(Find.CurrentMap, out _, out _))
                        OnMapLive(entry);
                    if (entry.MapIsLive)
                    {
                        try
                        {
                            var done = Finish(entry);
                            if (done != null) return done;
                        }
                        catch (Exception error)
                        {
                            entry.Aborted = true;
                            Log.Warning("[RimGovernor] new colony " + entry.RequestId + " failed in " + entry.Phase + ": " + error);
                            return Complete(entry, Fail(Common.FailureCode.NativeFailure,
                                "New colony failed in " + entry.Phase + ": " + error.GetType().Name + ": " + error.Message));
                        }
                    }
                }
            }
            return PendingReply(entry);
        }

        // The live-map hand-off: the map exists, the colonists are spawned and
        // the long events are done. Pause explicitly (nothing else does: the
        // start is not a load).
        private static void OnMapLive(Entry entry)
        {
            entry.MapIsLive = true;
            entry.LiveUtc = DateTime.UtcNow;
            entry.Phase = Lifecycle.NewColonyPhase.Finishing;
            entry.Detail = "The map is live; pausing and confirming the colony names";
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
        }

        // How long the live map waits for the colony-naming dialog to open
        // before concluding there is none (the harness treats it as optional).
        private static readonly TimeSpan NamingDialogGrace = TimeSpan.FromSeconds(3);

        // Finishing -> Saving -> done, one step per poll so the SAVING phase is
        // reported before the (blocking) save runs. Null while pending.
        private static Lifecycle.NewColonyReply? Finish(Entry entry)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            if (entry.Phase == Lifecycle.NewColonyPhase.Finishing)
            {
                var dialog = ColonyNamingTools.Pending();
                if (dialog != null)
                {
                    ColonyNamingTools.Confirm(dialog, ColonyNamingTools.Name(dialog, "curName") ?? "", ColonyNamingTools.Name(dialog, "curSecondName") ?? "");
                }
                else if (DateTime.UtcNow - entry.LiveUtc < NamingDialogGrace)
                    return null;
                entry.Phase = Lifecycle.NewColonyPhase.Saving;
                entry.Detail = "Saving " + entry.Spec.SaveName;
                return null;
            }
            // Saving: same native save path as lifecycle_save.
            var length = ProtoLifecycleSaveTools.WriteSave(entry.Spec.SaveName);
            if (!ProtoBoundary.TryReadContext(Find.CurrentMap, out var context, out var unavailable))
                return Complete(entry, Fail(Common.FailureCode.NativeFailure, "Colony state could not be re-observed after the save: " + unavailable.Detail));
            return Complete(entry, new Lifecycle.NewColonyReply { Completed = new Lifecycle.NewColonyCompleted
            {
                RequestId = entry.RequestId, SaveName = entry.Spec.SaveName, Context = context,
                Paused = Find.TickManager.Paused, ByteLength = (ulong)length, Seed = entry.Seed,
            } });
        }

        private static Lifecycle.NewColonyReply PendingReply(Entry entry) =>
            new Lifecycle.NewColonyReply { Pending = new Lifecycle.NewColonyPending
            {
                RequestId = entry.RequestId, Phase = entry.Phase, Detail = entry.Detail,
                ElapsedMs = (ulong)Math.Max(0, (DateTime.UtcNow - entry.StartedUtc).TotalMilliseconds),
                RerollCount = entry.Rerolls,
            } };

        private static Lifecycle.NewColonyReply Complete(Entry entry, Lifecycle.NewColonyReply reply)
        {
            lock (Lock) { entry.Reply = reply; }
            return reply;
        }

        private static void Prune()
        {
            if (Entries.Count <= MaxEntries) return;
            string? oldest = null;
            var oldestTime = DateTime.MaxValue;
            foreach (var pair in Entries)
                if (pair.Value.Reply != null && pair.Value.StartedUtc < oldestTime && pair.Key != activeRequestId)
                {
                    oldest = pair.Key; oldestTime = pair.Value.StartedUtc;
                }
            if (oldest != null) Entries.Remove(oldest);
        }
    }
}
