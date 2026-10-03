using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

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
                // def at the cell (the debug start's own starting Steel, #185),
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
                var unwanted = (Thing)PlaceSomewhere(ThingMaker.MakeThing(ThingDefOf.WoodLog));
                unwanted.SetForbidden(false, false);
                var protectedItem = (Thing)PlaceSomewhere(ThingMaker.MakeThing(ThingDefOf.Steel));
                protectedItem.SetForbidden(true, false);
                var unwantedCell = unwanted.Position;
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
                    zone.GetStoreSettings().filter.SetAllow(unwanted.def, true);
                    foreach (var special in DefDatabase<SpecialThingFilterDef>.AllDefsListForReading.Where(d => d.configurable))
                        zone.GetStoreSettings().filter.SetAllow(special, true);
                    zone.GetStoreSettings().Priority = StoragePriority.Critical;
                    // Native stockpile creation may expand Home; this fixture
                    // deliberately models player-selected dirty storage outside it.
                    foreach (var cell in zone.Cells) map.areaManager.Home[cell] = false;
                }
                // Disable Hauling for every colonist (including the returned
                // pawn), not enable it: the native ManageWaste dispatch under
                // test issues its own forced WorkGiver_Scanner job directly
                // (TryTakeOrderedJob), bypassing work settings entirely, so
                // no colonist's own priority needs to be enabled for it. Left
                // enabled, a live run observed the normal think-tree grab
                // this exact unforbidden corpse/WoodLog into the dumping
                // stockpile within the same handful of ticks fixture setup
                // itself consumes, before the acceptance tool ever issues its
                // own dispatch -- disabling it here removes that race.
                foreach (var colonist in map.mapPawns.FreeColonistsSpawned)
                    colonist.workSettings?.SetPriority(WorkTypeDefOf.Hauling, 0);
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return (object)new { success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken,
                    mapId = map.uniqueID, tick = Find.TickManager.TicksGame,
                    pawn = pawn.GetUniqueLoadID(), corpse = corpse.GetUniqueLoadID(),
                    unwanted = unwanted.GetUniqueLoadID(), protectedItem = protectedItem.GetUniqueLoadID(),
                    source = BridgeCommon.Pos(source), destination = BridgeCommon.Pos(destination),
                    // The corpse, the unwanted WoodLog and the forbidden
                    // Steel can each land on a different candidate cell (see
                    // PlaceSomewhere above); report the WoodLog's and Steel's
                    // actual resulting cells so the acceptance tool scans
                    // exactly where they really are, not a guessed "source".
                    unwantedCell = BridgeCommon.Pos(unwantedCell), protectedCell = BridgeCommon.Pos(protectedCell) };
            }, cancellationToken);

        // Corpse disposal acceptance (#1817), staged on the tribal baseline
        // colony: a rotten animal corpse, a rotten and a fresh stranger
        // corpse, one worn apparel and stone blocks for the walls, each on its
        // own free open cell near the first colonist. Strangers are humanlike
        // corpses of no faction.
        [Tool("test/disposal_stage", Description = "Disposable fixture (#1817): stage rotten and fresh corpses, a worn apparel and stone blocks near the first colonist. Test builds only.")]
        public async Task<object> DisposalStage(IRimBridgeContext ctx, CancellationToken cancellationToken, int blocks = 225)
            => await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                var pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).First();
                var candidates = GenRadial.RadialCellsAround(pawn.Position, 16, true).Where(c => c.InBounds(map)
                    && !c.Fogged(map) && c.Standable(map) && !c.Roofed(map) && c.GetEdifice(map) == null && c.GetZone(map) == null
                    && !c.GetThingList(map).Any(t => t.def.category == ThingCategory.Item) && c.GetFirstBuilding(map) == null
                    && c.GetTerrain(map).passability == Traversability.Standable).Distinct().ToList();
                if (candidates.Count < 16) throw new System.InvalidOperationException("Not enough empty open cells near the first colonist.");
                var next = 0;
                Thing Place(Thing thing)
                {
                    while (next < candidates.Count)
                    {
                        var cell = candidates[next++];
                        if (cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Item)) continue;
                        if (!GenPlace.TryPlaceThing(thing, cell, map, ThingPlaceMode.Direct, out var placed)) continue;
                        if (placed != thing || !thing.Spawned || thing.Position != cell)
                            throw new System.InvalidOperationException($"Fixture {thing.def.defName} was absorbed or displaced at {cell}.");
                        thing.SetForbidden(false, false);
                        return thing;
                    }
                    throw new System.InvalidOperationException($"Could not place a fixture {thing.def.defName} on any open cell.");
                }
                Corpse MakeCorpse(PawnKindDef kind, float rotProgress)
                {
                    var dead = PawnGenerator.GeneratePawn(kind, null);
                    GenSpawn.Spawn(dead, pawn.Position, map);
                    dead.Kill(null);
                    var corpse = dead.Corpse;
                    if (corpse.Spawned) corpse.DeSpawn(DestroyMode.Vanish);
                    Place(corpse);
                    corpse.GetComp<CompRottable>().RotProgress = rotProgress;
                    return corpse;
                }
                var squirrel = DefDatabase<PawnKindDef>.AllDefsListForReading.First(k => k.race.defName == "Squirrel");
                var animal = MakeCorpse(squirrel, 200000f);
                var rottenStranger = MakeCorpse(PawnKindDefOf.Villager, 200000f);
                var freshStranger = MakeCorpse(PawnKindDefOf.Villager, 0f);
                var pants = DefDatabase<ThingDef>.GetNamed("Apparel_Pants");
                var worn = Place(ThingMaker.MakeThing(pants, GenStuff.DefaultStuffFor(pants)));
                worn.HitPoints = worn.MaxHitPoints / 5;
                var stone = DefDatabase<ThingDef>.GetNamed("BlocksGranite");
                for (var left = blocks; left > 0; left -= stone.stackLimit)
                {
                    var stack = ThingMaker.MakeThing(stone);
                    stack.stackCount = System.Math.Min(left, stone.stackLimit);
                    Place(stack);
                }
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return (object)new { success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken,
                    mapId = map.uniqueID, tick = Find.TickManager.TicksGame,
                    animal = animal.GetUniqueLoadID(), animalDef = animal.def.defName, rottenStranger = rottenStranger.GetUniqueLoadID(),
                    freshStranger = freshStranger.GetUniqueLoadID(), worn = worn.GetUniqueLoadID(), wornDef = worn.def.defName };
            }, cancellationToken);

        // Worn apparel (a fifth of its hit points) on each cell ("x,z;x,z"):
        // what the incinerator zone takes, enough to fill a batch.
        [Tool("test/disposal_seed", Description = "Disposable fixture (#1817): spawn one worn apparel on each listed free cell and molotovs loose near the first colonist. Test builds only.")]
        public async Task<object> DisposalSeed(IRimBridgeContext ctx, CancellationToken cancellationToken, string cells, int molotovs = 0)
            => await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                var parsed = (cells ?? "").Split(new[] { ';' }, System.StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split(','))
                    .Select(p => new IntVec3(int.Parse(p[0]), 0, int.Parse(p[1]))).ToList();
                var pants = DefDatabase<ThingDef>.GetNamed("Apparel_Pants");
                var ids = new System.Collections.Generic.List<string>();
                foreach (var cell in parsed)
                {
                    if (!cell.InBounds(map) || cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Item))
                        throw new System.InvalidOperationException($"Cell {cell} is out of bounds or already holds an item.");
                    var thing = ThingMaker.MakeThing(pants, GenStuff.DefaultStuffFor(pants));
                    thing.HitPoints = thing.MaxHitPoints / 5;
                    GenSpawn.Spawn(thing, cell, map);
                    thing.SetForbidden(false, false);
                    ids.Add(thing.GetUniqueLoadID());
                }
                // Loose molotovs near the first colonist: the burner's weapon.
                var near = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).First().Position;
                for (var i = 0; i < molotovs; i++)
                {
                    var molotov = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Weapon_GrenadeMolotov"));
                    if (!GenPlace.TryPlaceThing(molotov, near, map, ThingPlaceMode.Near)) throw new System.InvalidOperationException("Could not place a molotov.");
                    molotov.SetForbidden(false, false);
                    ids.Add(molotov.GetUniqueLoadID());
                }
                return (object)new { success = true, ids };
            }, cancellationToken);

        // The incinerator as the game sees it (#1817): each listed thing, the
        // interior cells with the room cell.GetRoom resolves them to, the
        // walled ring's edifices with their flammability, the ash and the
        // fires. door is the ring's door cell; the cell beyond it is outside.
        [Tool("test/disposal_read", Description = "Disposable fixture (#1817): read the staged things and the incinerator room, ring, ash and fires. Test builds only.")]
        public async Task<object> DisposalRead(IRimBridgeContext ctx, CancellationToken cancellationToken, string ids, int minX, int minZ, int maxX, int maxZ, int doorX, int doorZ)
            => await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                var rect = CellRect.FromLimits(minX, minZ, maxX, maxZ);
                var things = (ids ?? "").Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries).Select(id =>
                {
                    var thing = map.listerThings.AllThings.FirstOrDefault(t => t.GetUniqueLoadID() == id);
                    var carried = map.mapPawns.AllPawns.Any(p => p.carryTracker?.CarriedThing?.GetUniqueLoadID() == id);
                    return (object)new { id, spawned = thing != null && thing.Spawned, carried,
                        x = thing?.Position.x ?? -1, z = thing?.Position.z ?? -1,
                        rot = thing?.TryGetComp<CompRottable>()?.Stage.ToString() ?? "",
                        inside = thing != null && thing.Spawned && rect.Contains(thing.Position) };
                }).ToList();
                var interior = rect.Cells.Select(c =>
                {
                    var room = c.GetRoom(map);
                    return new { x = c.x, z = c.z, room = room?.ID ?? -1, roofed = c.Roofed(map),
                        items = c.GetThingList(map).Where(t => t.def.category == ThingCategory.Item).Select(t => t.def.defName).ToList() };
                }).ToList();
                var first = rect.CenterCell.GetRoom(map);
                var door = new IntVec3(doorX, 0, doorZ);
                var step = new IntVec3(System.Math.Sign(doorX - rect.CenterCell.x), 0, System.Math.Sign(doorZ - rect.CenterCell.z));
                var beyond = door + step;
                var ring = rect.ExpandedBy(1).EdgeCells.Select(c =>
                {
                    var edifice = c.GetEdifice(map);
                    return new { x = c.x, z = c.z, def = edifice?.def.defName ?? "", stuff = edifice?.Stuff?.defName ?? "",
                        door = edifice is Building_Door, flammability = edifice == null ? 0f : edifice.GetStatValue(StatDefOf.Flammability) };
                }).ToList();
                var ash = rect.Cells.SelectMany(c => c.GetThingList(map)).Count(t => t.def.defName == "Filth_Ash");
                var fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count;
                return (object)new { success = true, tick = Find.TickManager.TicksGame, things, interior, ring, ash, fires,
                    room = new { id = first?.ID ?? -1, cellCount = first?.CellCount ?? 0, outdoorTemperature = first?.UsesOutdoorTemperature ?? false, properRoom = first?.ProperRoom ?? false },
                    beyondDoorRoom = beyond.InBounds(map) ? (beyond.GetRoom(map)?.ID ?? -1) : -1, doorRoom = door.GetRoom(map)?.ID ?? -1 };
            }, cancellationToken);

        // The permanent ColonyFacts equality probe (#1296): the snapshot read
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
                RimGovernor.Protocol.Observations.ColonyFactsSnapshot Capture(bool optimized)
                {
                    var previous = NativeUpkeepFacts.SharedPass;
                    NativeUpkeepFacts.SharedPass = optimized;
                    try
                    {
                        var request = new RimGovernor.Protocol.Observations.ColonyFactsRequest {
                            Scope = new RimGovernor.Protocol.Observations.ReadScope { ExpectedIdentity = context.Identity.Clone() }, Planning = true };
                        if (!NativeColonyObservationTools.TryRead(map, request, context, out var snapshot))
                            throw new System.InvalidOperationException("ColonyFacts read failed.");
                        return snapshot;
                    }
                    finally { NativeUpkeepFacts.SharedPass = previous; }
                }
                var off = Capture(false);
                var on = Capture(true);
                var offBytes = Google.Protobuf.MessageExtensions.ToByteArray(off);
                var onBytes = Google.Protobuf.MessageExtensions.ToByteArray(on);
                var upkeep = on.Upkeep?.Observed;
                // ResourceSources (#1295): each resource's reply with the
                // cheap census and support shortcut off and on. Plant yields
                // round at random per read, so non-mine yields are blanked.
                var resourcesEqual = true; long resourceRows = 0; double offMs = 0, onMs = 0;
                byte[] Sources(string resource, bool cheap, ref double ms)
                {
                    NativeResourceSourcesTool.Cheap = cheap; ExcavationSafety.Shortcut = cheap;
                    try
                    {
                        var began = System.Diagnostics.Stopwatch.GetTimestamp();
                        var reply = NativeResourceSourcesTool.Read(map, new RimGovernor.Protocol.Observations.ResourceSourcesRequest { Resource = resource }, context);
                        ms += (System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        foreach (var row in reply.Observed?.Sources ?? new Google.Protobuf.Collections.RepeatedField<RimGovernor.Protocol.Observations.ResourceSource>())
                            if (row.Method != "mine") row.Yield = 0;
                        return Google.Protobuf.MessageExtensions.ToByteArray(reply);
                    }
                    finally { NativeResourceSourcesTool.Cheap = true; ExcavationSafety.Shortcut = true; }
                }
                foreach (var resource in new[] { "Steel", "WoodLog", "ComponentIndustrial", "Silver", "Gold", "Plasteel", "Uranium", "Jade", "ChunkGranite", "ChunkSandstone", "ChunkLimestone", "ChunkSlate", "ChunkMarble" })
                    for (var i = 0; i < 3; i++)
                    {
                        var a = Sources(resource, false, ref offMs);
                        var b = Sources(resource, true, ref onMs);
                        if (!a.SequenceEqual(b)) resourcesEqual = false;
                        if (i == 0) resourceRows += RimGovernor.Protocol.Observations.ResourceSourcesReply.Parser.ParseFrom(b).Observed?.Sources.Count ?? 0;
                    }
                return (object)new { equal = offBytes.SequenceEqual(onBytes) && resourcesEqual, resourcesEqual, resourceRows,
                    resourcesOffMs = System.Math.Round(offMs, 1), resourcesOnMs = System.Math.Round(onMs, 1), offBytes = offBytes.Length, onBytes = onBytes.Length,
                    upkeepItems = upkeep?.Items.Count ?? 0, upkeepStructures = upkeep?.Structures.Count ?? 0,
                    upkeepFilth = upkeep?.Filth.Count ?? 0, upkeepBeds = upkeep?.Beds.Count ?? 0,
                    wasteRows = on.Waste?.Observed?.Items.Count ?? 0 };
            }
        }

        // The snapshot capture profiling loop (#1320): SnapshotFrames.Capture
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
                        RimGovernor.Protocol.Observations.BundleSnapshot frame;
                        try { frame = SnapshotFrames.Capture(map, shape); }
                        finally { ObservationWork.End(); }
                        var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - began;
                        if (frame == null) throw new System.InvalidOperationException("SnapshotFrames.Capture read no context.");
                        if (i == 0) bytes = frame.CalculateSize();
                        captures.Add(new { totalMs = Ms(elapsed), sections = hop.Sections.Select(s =>
                            (object)new { name = s.Name, ms = Ms(s.Ticks), rows = s.Rows, detail = s.Detail }).ToList() });
                    }
                    return (object)new { success = true, count, tick = tickManager.TicksGame, frameBytes = bytes,
                        pawns = map.mapPawns.AllPawnsSpawnedCount, things = map.listerThings.AllThings.Count,
                        captures, equality = equality ? CompareColonyFacts(map) : null };
                }
                finally { tickManager.CurTimeSpeed = speed; }
            }, cancellationToken);

        private static double Ms(long stopwatchTicks) => stopwatchTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }
}
