using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Disposable setup only; never exposed through the controller gameplay surface.
    public sealed class WasteFixture
    {
        [Tool("test/waste_fixture", Description = "Prepare disposable waste hauling acceptance inputs.")]
        public async Task<object> Create(IRimBridgeContext ctx, CancellationToken cancellationToken, bool burial = false)
            => await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                var pawn = map.mapPawns.FreeColonistsSpawned.First(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling));
                // A generous pool of candidate cells, not one fixed cell per
                // item: ThingPlaceMode.Direct placement of a fresh item was
                // observed live to fail outright (TryPlaceThing returns
                // false, the thing left unspawned) on some individual cells
                // for reasons this fixture does not need to fully diagnose
                // (e.g. terrain/roof affordances GetEdifice/Standable do not
                // capture). Each item independently walks the same candidate
                // pool until placement actually succeeds, rather than
                // committing to a single guessed cell.
                //
                // Candidate cells hold no item already: Direct placement
                // absorbs a fixture stack into an existing stack of the same
                // def at the cell (the debug start's own starting Steel),
                // leaving the fixture's Thing destroyed and its Position
                // off-map. The pool is filtered up front and the placement is
                // checked afterwards so the returned item is the one spawned.
                var candidates = GenRadial.RadialCellsAround(pawn.Position, 10, true).Where(c => c.InBounds(map)
                    && !c.Fogged(map) && c.Standable(map) && c.GetEdifice(map) == null
                    && !c.GetThingList(map).Any(t => t.def.category == ThingCategory.Item)).Distinct().ToList();
                if (candidates.Count < 8) throw new System.InvalidOperationException("Not enough empty standable cells near the fixture pawn; reroll the world.");
                Thing PlaceSomewhere(Thing thing)
                {
                    foreach (var cell in candidates)
                    {
                        if (cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Item)) continue;
                        if (!GenPlace.TryPlaceThing(thing, cell, map, ThingPlaceMode.Direct, out var placed)) continue;
                        if (placed != thing || !thing.Spawned || thing.Position != cell)
                            throw new System.InvalidOperationException($"Fixture {thing.def.defName} was absorbed or displaced at {cell} instead of spawning there.");
                        return thing;
                    }
                    throw new System.InvalidOperationException($"Could not place a fixture {thing.def.defName} on any of {candidates.Count} candidate cells.");
                }
                var source = candidates[0];
                var destination = GenRadial.RadialCellsAround(source, 35, true).First(c => c.InBounds(map)
                    && !c.Fogged(map) && c.Standable(map) && !c.Roofed(map) && c.GetZone(map) == null
                    && !map.areaManager.Home[c] && c.DistanceToSquared(source) > 225
                    && c.GetRoom(map)?.UsesOutdoorTemperature == true
                    && !map.listerBuildings.allBuildingsColonist.Any(b => b.Position.DistanceToSquared(c) < 196));
                var kind = burial ? PawnKindDefOf.Colonist : DefDatabase<PawnKindDef>.AllDefsListForReading.First(k => k.race.defName == "Squirrel");
                var animal = PawnGenerator.GeneratePawn(kind, burial ? Faction.OfPlayer : null);
                GenSpawn.Spawn(animal, source, map);
                animal.Kill(null);
                var corpse = animal.Corpse;
                // Pawn.Kill's own corpse placement can silently nudge the
                // corpse to a neighboring cell instead of exactly "source"
                // (observed live); re-place it exactly through the same pool.
                if (corpse.Spawned) corpse.DeSpawn(DestroyMode.Vanish);
                PlaceSomewhere(corpse);
                corpse.TryGetComp<CompRottable>().RotProgress = 200000f;
                corpse.SetForbidden(false, false);
                var protectedItem = (Thing)PlaceSomewhere(ThingMaker.MakeThing(ThingDefOf.Steel));
                protectedItem.SetForbidden(true, false);
                var protectedCell = protectedItem.Position;
                if (burial)
                {
                    var grave = ThingMaker.MakeThing(ThingDefOf.Grave);
                    grave.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(grave, destination, map);
                    ((Building_Grave)grave).GetStoreSettings().filter.SetAllow(corpse.def, true);
                }
                else
                {
                    var zone = new Zone_Stockpile(StorageSettingsPreset.DumpingStockpile, map.zoneManager);
                    map.zoneManager.RegisterZone(zone);
                    zone.AddCell(destination);
                    zone.AddCell(destination + IntVec3.East);
                    zone.GetStoreSettings().filter.SetDisallowAll();
                    zone.GetStoreSettings().filter.SetAllow(corpse.def, true);
                    foreach (var special in DefDatabase<SpecialThingFilterDef>.AllDefsListForReading.Where(d => d.configurable))
                        zone.GetStoreSettings().filter.SetAllow(special, true);
                    zone.GetStoreSettings().Priority = StoragePriority.Critical;
                    // Native stockpile creation may expand Home; this fixture
                    // deliberately models player-selected dirty storage outside it.
                    foreach (var cell in zone.Cells) map.areaManager.Home[cell] = false;
                }
                // Disable Hauling for every colonist (including the returned
                // pawn), not enable it: the dispatch under
                // test issues its own forced WorkGiver_Scanner job directly
                // (TryTakeOrderedJob), bypassing work settings entirely, so
                // no colonist's own priority needs to be enabled for it. Left
                // enabled, a live run observed the normal think-tree grab
                // this exact unforbidden corpse into the dumping
                // stockpile within the same handful of ticks fixture setup
                // itself consumes, before the acceptance tool ever issues its
                // own dispatch -- disabling it here removes that race.
                foreach (var colonist in map.mapPawns.FreeColonistsSpawned)
                    colonist.workSettings?.SetPriority(WorkTypeDefOf.Hauling, 0);
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return (object)new { success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken,
                    mapId = map.uniqueID, tick = Find.TickManager.TicksGame,
                    pawn = pawn.GetUniqueLoadID(), corpse = corpse.GetUniqueLoadID(),
                    protectedItem = protectedItem.GetUniqueLoadID(),
                    source = BridgeCommon.Pos(source), destination = BridgeCommon.Pos(destination),
                    // The forbidden Steel may land on a different candidate cell
                    // than "source" (see PlaceSomewhere above); report its actual
                    // cell so the acceptance tool scans exactly where it is.
                    protectedCell = BridgeCommon.Pos(protectedCell) };
            }, cancellationToken);

        // Burial ranking acceptance: a morgue zone at MorguePriority
        // holding a fresh colonist corpse and a fresh stranger corpse, one free
        // grave a few cells off, and a hauling colonist. Vanilla hauling must
        // carry the colonist into the grave (a higher storage priority than the
        // morgue) and leave the stranger, whom the grave refuses, in the morgue.
        [Tool("test/burial_stage", Description = "Disposable fixture (#2196): a morgue zone holding a colonist and a stranger corpse, a free grave and a hauler. Test builds only.")]
        public async Task<object> BurialStage(IRimBridgeContext ctx, CancellationToken cancellationToken, string priority = "Normal")
            => await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                var pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).First();
                var open = GenRadial.RadialCellsAround(pawn.Position, 20, true).Where(c => c.InBounds(map)
                    && !c.Fogged(map) && c.Standable(map) && !c.Roofed(map) && c.GetEdifice(map) == null && c.GetZone(map) == null
                    && !c.GetThingList(map).Any(t => t.def.category == ThingCategory.Item) && c.GetFirstBuilding(map) == null
                    && c.GetTerrain(map).passability == Traversability.Standable).Distinct().ToList();
                var origin = open.FirstOrDefault(c => Enumerable.Range(0, 3).All(dx => Enumerable.Range(0, 3).All(dz => open.Contains(c + new IntVec3(dx, 0, dz)))));
                if (origin == default) throw new System.InvalidOperationException("No open 3x3 near the first colonist for the morgue.");
                var zoneCells = Enumerable.Range(0, 3).SelectMany(dx => Enumerable.Range(0, 3).Select(dz => origin + new IntVec3(dx, 0, dz))).ToList();
                var graveCell = open.Where(c => !zoneCells.Contains(c) && zoneCells.All(z => z.DistanceToSquared(c) > 9)
                    && c.GetThingList(map).All(t => t.def.category != ThingCategory.Item)).OrderBy(c => c.DistanceToSquared(origin)).FirstOrDefault();
                if (graveCell == default) throw new System.InvalidOperationException("No free cell for the grave.");
                var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                foreach (var c in zoneCells) zone.AddCell(c);
                var settings = zone.GetStoreSettings();
                settings.filter.SetDisallowAll();
                settings.filter.SetAllow(ThingCategoryDefOf.CorpsesHumanlike, true);
                foreach (var special in DefDatabase<SpecialThingFilterDef>.AllDefsListForReading.Where(d => d.configurable))
                    settings.filter.SetAllow(special, true);
                settings.Priority = (StoragePriority)System.Enum.Parse(typeof(StoragePriority), priority);
                var grave = (Building_Grave)ThingMaker.MakeThing(ThingDefOf.Grave);
                grave.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(grave, graveCell, map);
                Corpse MakeCorpse(Faction faction, IntVec3 cell)
                {
                    var dead = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, faction);
                    GenSpawn.Spawn(dead, cell, map);
                    dead.Kill(null);
                    var corpse = dead.Corpse;
                    if (corpse.Spawned) corpse.DeSpawn(DestroyMode.Vanish);
                    GenPlace.TryPlaceThing(corpse, cell, map, ThingPlaceMode.Direct, out var placed);
                    if (placed != corpse || !corpse.Spawned || corpse.Position != cell)
                        throw new System.InvalidOperationException($"Fixture corpse displaced from {cell}.");
                    corpse.SetForbidden(false, false);
                    return corpse;
                }
                var colonist = MakeCorpse(Faction.OfPlayer, zoneCells[0]);
                var stranger = MakeCorpse(null, zoneCells[1]);
                foreach (var colonistPawn in map.mapPawns.FreeColonistsSpawned)
                    colonistPawn.workSettings?.SetPriority(WorkTypeDefOf.Hauling, 1);
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return (object)new { success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken,
                    mapId = map.uniqueID, tick = Find.TickManager.TicksGame,
                    colonistCorpse = colonist.GetUniqueLoadID(), strangerCorpse = stranger.GetUniqueLoadID(), grave = grave.GetUniqueLoadID(),
                    zone = zone.label };
            }, cancellationToken);

        [Tool("test/burial_read", Description = "Disposable fixture (#2196): where the staged corpses lie: in the grave, in the morgue zone or elsewhere. Test builds only.")]
        public async Task<object> BurialRead(IRimBridgeContext ctx, CancellationToken cancellationToken, string ids = "")
            => await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                var things = ids.Split(',').Where(id => id.Length > 0).Select(id =>
                {
                    var corpse = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>().FirstOrDefault(c => c.GetUniqueLoadID() == id);
                    var grave = corpse?.ParentHolder as Building_Grave;
                    if (corpse == null)
                        grave = map.listerBuildings.allBuildingsColonist.OfType<Building_Grave>().FirstOrDefault(g => g.HasAnyContents && g.Corpse?.GetUniqueLoadID() == id);
                    var held = corpse ?? grave?.Corpse;
                    return new { id, found = held != null, inGrave = grave != null, spawned = corpse?.Spawned ?? false,
                        zone = corpse != null && corpse.Spawned && corpse.Position.GetZone(map) is Zone_Stockpile,
                        x = corpse != null && corpse.Spawned ? corpse.Position.x : -1, z = corpse != null && corpse.Spawned ? corpse.Position.z : -1 };
                }).ToList();
                return (object)new { success = true, tick = Find.TickManager.TicksGame, things };
            }, cancellationToken);

        // The KnowBuriedInSarcophagus probe (#3242): one fresh Sarcophagus
        // accepting any humanlike corpse, a fresh stranger corpse (a humanlike
        // pawn of no faction) a few cells away, and every colonist hauling.
        [Tool("test/tomb_stage", Description = "Disposable fixture (#3242): a fresh sarcophagus and a fresh stranger corpse near the first colonist. Test builds only.")]
        public async Task<object> TombStage(IRimBridgeContext ctx, CancellationToken cancellationToken)
            => await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                var (sarcophagus, corpseCell) = FreshSarcophagus(map);
                var corpse = TombCorpse(map, corpseCell);
                foreach (var colonist in map.mapPawns.FreeColonistsSpawned)
                    colonist.workSettings?.SetPriority(WorkTypeDefOf.Hauling, 1);
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return (object)new { success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken,
                    mapId = map.uniqueID, tick = Find.TickManager.TicksGame,
                    sarcophagus = sarcophagus.GetUniqueLoadID(), corpse = corpse.GetUniqueLoadID(),
                    colonists = map.mapPawns.FreeColonistsSpawned.Count };
            }, cancellationToken);

        // A fresh (never filled) player sarcophagus on an open 1x2 near the
        // first colonist, accepting every humanlike corpse and special filter,
        // plus a free cell more than four cells off for the corpse it will
        // receive. Spawned, not accepted through TryAcceptThing, so its
        // thisIsFirstBodyEver is false (it is unsaved and only TryAcceptThing
        // sets it), as for every sarcophagus a colonist builds.
        private static (Building_Grave Sarcophagus, IntVec3 CorpseCell) FreshSarcophagus(Map map)
        {
            var pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).First();
            var open = GenRadial.RadialCellsAround(pawn.Position, 20, true).Where(c => c.InBounds(map)
                && !c.Fogged(map) && c.Standable(map) && !c.Roofed(map) && c.GetEdifice(map) == null && c.GetZone(map) == null
                && !c.GetThingList(map).Any(t => t.def.category == ThingCategory.Item) && c.GetFirstBuilding(map) == null
                && c.GetTerrain(map).passability == Traversability.Standable).Distinct().ToList();
            var sarcophagusCell = open.FirstOrDefault(c => open.Contains(c + IntVec3.North));
            if (sarcophagusCell == default) throw new System.InvalidOperationException("No open 1x2 near the first colonist for the sarcophagus.");
            var sarcophagusCells = new[] { sarcophagusCell, sarcophagusCell + IntVec3.North };
            var corpseCell = open.Where(c => !sarcophagusCells.Contains(c) && sarcophagusCells.All(s => s.DistanceToSquared(c) > 16))
                .OrderBy(c => c.DistanceToSquared(sarcophagusCell)).FirstOrDefault();
            if (corpseCell == default) throw new System.InvalidOperationException("No free cell for the corpse.");
            var def = DefDatabase<ThingDef>.GetNamed("Sarcophagus");
            var sarcophagus = (Building_Grave)ThingMaker.MakeThing(def, ThingDefOf.Steel);
            sarcophagus.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(sarcophagus, sarcophagusCell, map, Rot4.North);
            var filter = sarcophagus.GetStoreSettings().filter;
            filter.SetDisallowAll();
            filter.SetAllow(ThingCategoryDefOf.CorpsesHumanlike, true);
            foreach (var special in DefDatabase<SpecialThingFilterDef>.AllDefsListForReading.Where(d => d.configurable))
                filter.SetAllow(special, true);
            return (sarcophagus, corpseCell);
        }

        // A fresh stranger corpse exactly on cell, unforbidden.
        private static Corpse TombCorpse(Map map, IntVec3 cell)
        {
            var dead = PawnGenerator.GeneratePawn(PawnKindDefOf.Villager, null);
            GenSpawn.Spawn(dead, cell, map);
            dead.Kill(null);
            var corpse = dead.Corpse;
            if (corpse.Spawned) corpse.DeSpawn(DestroyMode.Vanish);
            GenPlace.TryPlaceThing(corpse, cell, map, ThingPlaceMode.Direct, out var placed);
            if (placed != corpse || !corpse.Spawned || corpse.Position != cell)
                throw new System.InvalidOperationException($"Fixture corpse displaced from {cell}.");
            corpse.SetForbidden(false, false);
            return corpse;
        }

        // Where the sarcophagus, each colonist's KnowBuriedInSarcophagus
        // memories and each listed corpse stand.
        [Tool("test/tomb_read", Description = "Disposable fixture (#3242): the sarcophagus, every colonist's KnowBuriedInSarcophagus memories and the listed corpses. Test builds only.")]
        public async Task<object> TombRead(IRimBridgeContext ctx, CancellationToken cancellationToken, string sarcophagus, string ids = "")
            => await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                var memory = DefDatabase<ThoughtDef>.GetNamed("KnowBuriedInSarcophagus");
                var grave = map.listerBuildings.allBuildingsColonist.OfType<Building_Grave>().FirstOrDefault(g => g.GetUniqueLoadID() == sarcophagus);
                var colonists = map.mapPawns.FreeColonistsSpawned.Select(p => new { id = p.GetUniqueLoadID(), mood = p.needs?.mood != null,
                    memories = p.needs?.mood?.thoughts?.memories?.Memories.Count(m => m.def == memory) ?? -1 }).ToList();
                var corpses = ids.Split(',').Where(id => id.Length > 0).Select(id =>
                {
                    var spawned = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>().FirstOrDefault(c => c.GetUniqueLoadID() == id);
                    var held = grave?.Corpse != null && grave.Corpse.GetUniqueLoadID() == id ? grave.Corpse : null;
                    var corpse = spawned ?? held;
                    return new { id, found = corpse != null, spawned = spawned != null, inSarcophagus = held != null,
                        everBuried = corpse?.everBuriedInSarcophagus ?? false };
                }).ToList();
                return (object)new { success = true, tick = Find.TickManager.TicksGame, present = grave != null && !grave.Destroyed,
                    holds = grave?.HasAnyContents ?? false, colonists, corpses };
            }, cancellationToken);

        // The permanent ColonyFacts equality probe: the snapshot read
        // with every read optimization off and then on, in one game-thread
        // call so no tick passes between them, must be byte-identical (row
        // order included). Add each new optimization's switch here.
        [Tool("test/colony_facts_equality", Description = "Compare ColonyFacts bytes with read optimizations off and on.")]
        public async Task<object> ColonyFactsEquality(IRimBridgeContext ctx, CancellationToken cancellationToken)
            => await ctx.MainThread.InvokeAsync(() => CompareColonyFacts(Find.CurrentMap), cancellationToken);

        // On the main thread: the equality probe's reply for map.
        private static object CompareColonyFacts(Map map)
        {
            {
                if (!ProtoBoundary.TryReadContext(map, out var context, out var unavailable))
                    throw new System.InvalidOperationException(unavailable.Detail);
                RimGovernor.Contracts.Wire.Observations.ColonyFactsSnapshot Capture(bool optimized)
                {
                    var previous = NativeUpkeepFacts.SharedPass;
                    NativeUpkeepFacts.SharedPass = optimized;
                    try
                    {
                        var request = new RimGovernor.Contracts.Wire.Observations.ColonyFactsRequest {
                            Scope = new RimGovernor.Contracts.Wire.Observations.ReadScope { ExpectedIdentity = context.Identity }, Planning = true };
                        return NativeColonyObservationTools.Read(map, request, context);
                    }
                    finally { NativeUpkeepFacts.SharedPass = previous; }
                }
                var off = Capture(false);
                var on = Capture(true);
                var offBytes = RimGovernor.Contracts.Wire.WireJson.SerializeToUtf8Bytes(off);
                var onBytes = RimGovernor.Contracts.Wire.WireJson.SerializeToUtf8Bytes(on);
                var upkeep = on.Upkeep?.Outcome?.Observed;
                // ResourceSources: each resource's reply with the
                // cheap census and support shortcut off and on. Plant yields
                // round at random per read, so non-mine yields are blanked.
                var resourcesEqual = true; long resourceRows = 0; double offMs = 0, onMs = 0;
                byte[] Sources(string resource, bool cheap, ref double ms)
                {
                    NativeResourceSourcesTool.Cheap = cheap; ExcavationSafety.Shortcut = cheap;
                    try
                    {
                        var began = System.Diagnostics.Stopwatch.GetTimestamp();
                        var reply = NativeResourceSourcesTool.Read(map, new RimGovernor.Contracts.Wire.Observations.ResourceSourcesRequest { Resource = resource }, context);
                        ms += (System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        var observed = reply.Outcome?.Observed;
                        if (observed != null)
                            reply = reply with { Outcome = observed with { Sources = [.. observed.Sources.Select(row =>
                                DefDatabase<ThingDef>.GetNamedSilentFail(row.DefName)?.mineable != true ? row with { Yield = 0 } : row)] } };
                        return RimGovernor.Contracts.Wire.WireJson.SerializeToUtf8Bytes(reply);
                    }
                    finally { NativeResourceSourcesTool.Cheap = true; ExcavationSafety.Shortcut = true; }
                }
                foreach (var resource in new[] { "Steel", "WoodLog", "ComponentIndustrial", "Silver", "Gold", "Plasteel", "Uranium", "Jade", "ChunkGranite", "ChunkSandstone", "ChunkLimestone", "ChunkSlate", "ChunkMarble" })
                    for (var i = 0; i < 3; i++)
                    {
                        var a = Sources(resource, false, ref offMs);
                        var b = Sources(resource, true, ref onMs);
                        if (!a.SequenceEqual(b)) resourcesEqual = false;
                        if (i == 0) resourceRows += RimGovernor.Contracts.Wire.WireJson.Deserialize<RimGovernor.Contracts.Wire.Observations.ResourceSourcesReply>(b).Outcome?.Observed?.Sources.Count ?? 0;
                    }
                return (object)new { equal = offBytes.SequenceEqual(onBytes) && resourcesEqual, resourcesEqual, resourceRows,
                    resourcesOffMs = System.Math.Round(offMs, 1), resourcesOnMs = System.Math.Round(onMs, 1), offBytes = offBytes.Length, onBytes = onBytes.Length,
                    upkeepItems = upkeep?.Items.Count ?? 0, upkeepStructures = upkeep?.Structures.Count ?? 0,
                    upkeepFilth = upkeep?.Filth.Count ?? 0, upkeepBeds = upkeep?.Beds.Count ?? 0,
                    wasteRows = on.Waste?.Outcome?.Observed?.Items.Count ?? 0 };
            }
        }

        // The snapshot capture profiling loop: SnapshotFrames.Capture
        // count times back to back on the game thread, paused, with the
        // stream's current subscription. Each capture's total and every
        // ObservationWork section (families and their Detail spans) come back
        // raw; `acceptance profile-capture` computes the percentiles.
        // equality also runs the ColonyFacts equality probe afterwards.
        [Tool("test/profile_capture", Description = "Time SnapshotFrames.Capture count times, paused, per family and detail span.")]
        public async Task<object> ProfileCapture(IRimBridgeContext ctx, CancellationToken cancellationToken, int count = 20, bool equality = false)
            => await ctx.MainThread.InvokeAsync(() =>
            {
                if (count < 1 || count > 1000) throw new System.ArgumentOutOfRangeException(nameof(count), "count must be 1..1000");
                var map = Find.CurrentMap;
                var tickManager = Find.TickManager;
                var speed = tickManager.CurTimeSpeed;
                tickManager.CurTimeSpeed = TimeSpeed.Paused;
                try
                {
                    var shape = SnapshotStream.Subscription;
                    var captures = new System.Collections.Generic.List<object>(count);
                    long bytes = 0;
                    for (int i = 0; i < count; i++)
                    {
                        var began = System.Diagnostics.Stopwatch.GetTimestamp();
                        var hop = ObservationWork.BeginCapture();
                        RimGovernor.Contracts.Wire.Observations.BundleSnapshot frame;
                        try { frame = SnapshotFrames.Capture(map, shape); }
                        finally { ObservationWork.End(); }
                        var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - began;
                        if (frame == null) throw new System.InvalidOperationException("SnapshotFrames.Capture read no context.");
                        if (i == 0) bytes = RimGovernor.Contracts.Wire.WireJson.SerializeToUtf8Bytes(frame).Length;
                        captures.Add(new { totalMs = Ms(elapsed), sections = hop.Sections.Select(s =>
                            (object)new { name = s.Name, ms = Ms(s.Ticks), rows = s.Rows, detail = s.Detail }).ToList() });
                    }
                    return (object)new { success = true, count, tick = tickManager.TicksGame, frameBytes = bytes,
                        pawns = map.mapPawns.AllPawnsSpawnedCount, things = map.listerThings.AllThings.Count,
                        captures, equality = equality ? CompareColonyFacts(map) : null };
                }
                finally { tickManager.CurTimeSpeed = speed; }
            }, cancellationToken);

        // The cell grid cache's equality probe: the cached whole-map
        // read must encode to the same bytes as a read from scratch after the
        // cache has been warmed and then each kind of map change is made and
        // undone (wall, item, door blueprint, terrain, roof).
        [Tool("test/grid_cache_equality", Description = "Compare the cached cell grid read with a fresh one across map changes.")]
        public async Task<object> GridCacheEquality(IRimBridgeContext ctx, CancellationToken cancellationToken)
            => await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                var steps = new System.Collections.Generic.List<object>();
                bool Same(string step)
                {
                    var cached = RimGovernor.Contracts.Wire.WireJson.SerializeToUtf8Bytes(CellGridEncoder.Encode(CellGridEncoder.Read(map), null)!);
                    var fresh = RimGovernor.Contracts.Wire.WireJson.SerializeToUtf8Bytes(CellGridEncoder.Encode(CellGridEncoder.ReadUncached(map), null)!);
                    var equal = cached.SequenceEqual(fresh);
                    steps.Add(new { step, equal, cachedBytes = cached.Length, freshBytes = fresh.Length });
                    return equal;
                }
                var free = GenRadial.RadialCellsAround(map.Center, 40f, true)
                    .Where(c => c.InBounds(map) && c.Standable(map) && !c.Roofed(map) && !c.Fogged(map) && c.GetThingList(map).Count == 0
                        && c.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)).Take(8).ToList();
                if (free.Count < 8) throw new System.InvalidOperationException("Too few open cells near the map centre.");
                Same("warm");
                Same("rewarm");
                var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel);
                wall.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(wall, free[0], map);
                Same("wall spawned");
                wall.Destroy();
                Same("wall removed");
                var steel = ThingMaker.MakeThing(ThingDefOf.Steel); steel.stackCount = 10;
                GenSpawn.Spawn(steel, free[1], map);
                Same("item spawned");
                steel.Destroy();
                Same("item removed");
                var blueprint = GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Door, free[2], map, Rot4.North, Faction.OfPlayer, ThingDefOf.Steel);
                Same("door blueprint placed");
                blueprint.Destroy();
                Same("door blueprint removed");
                var terrain = map.terrainGrid.TerrainAt(free[3]);
                map.terrainGrid.SetTerrain(free[3], TerrainDefOf.Concrete);
                Same("terrain changed");
                map.terrainGrid.SetTerrain(free[3], terrain);
                Same("terrain restored");
                map.roofGrid.SetRoof(free[4], RoofDefOf.RoofConstructed);
                Same("roof set");
                map.roofGrid.SetRoof(free[4], null);
                Same("roof cleared");
                // A closed ring of walls round one cell makes a room and then unmakes it.
                var ring = new System.Collections.Generic.List<Thing>();
                foreach (var cell in GenAdj.CellsAdjacent8Way(new TargetInfo(free[5], map)).Where(c => c.InBounds(map) && c.Standable(map) && c.GetThingList(map).Count == 0))
                {
                    var piece = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel);
                    piece.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(piece, cell, map);
                    ring.Add(piece);
                }
                Same("wall ring built");
                foreach (var piece in ring) piece.Destroy();
                Same("wall ring removed");
                return (object)new { success = true, equal = steps.All(s => (bool)s.GetType().GetProperty("equal")!.GetValue(s)!), steps };
            }, cancellationToken);

        // The gear census memo's equality probe: the colony's gear
        // snapshot read with the per-read memo on and then off, in one
        // game-thread call, must be byte-identical.
        [Tool("test/gear_memo_equality", Description = "Compare the gear census bytes with the read memo on and off.")]
        public async Task<object> GearMemoEquality(IRimBridgeContext ctx, CancellationToken cancellationToken)
            => await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                if (!ProtoBoundary.TryReadContext(map, out var context, out var unavailable))
                    throw new System.InvalidOperationException(unavailable.Detail);
                byte[] Read(bool off)
                {
                    CensusMemo.Off = off;
                    try { return RimGovernor.Contracts.Wire.WireJson.SerializeToUtf8Bytes(NativeGearFacts.Read(map, context)); }
                    finally { CensusMemo.Off = false; }
                }
                var on = Read(false);
                var fresh = Read(true);
                return (object)new { success = true, equal = on.SequenceEqual(fresh), onBytes = on.Length, freshBytes = fresh.Length };
            }, cancellationToken);

        private static double Ms(long stopwatchTicks) => stopwatchTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }
}
