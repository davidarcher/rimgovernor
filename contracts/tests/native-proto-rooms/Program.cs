using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

internal static class Program
{
    private static Assembly bridge = null!;
    private static Type tools = null!;
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
    private static object Wire(string name, string json)
    {
        var parser = bridge.GetType("RimGovernor.Protocol.Observations." + name, true)!.GetProperty("Parser")!.GetValue(null)!;
        return parser.GetType().GetMethod("ParseJson")!.Invoke(parser, new object[] { json })!;
    }
    private static object Get(object value, string property) => value.GetType().GetProperty(property)!.GetValue(value)!;
    private static object Call(string name, params object?[] values) => tools.GetMethod(name, Flags)!.Invoke(null, values)!;
    private static bool Valid(string json) => (bool)Call("Validate", Wire("ListRoomsRequest", json), null);
    private static void Refused(Action action, string label) { try { action(); } catch (TargetInvocationException) { Check(true, label); return; } throw new Exception(label); }
    private static bool FalseResult(ref bool __result) { __result = false; return false; }
    private static bool SkipOriginal() => false;
    // Mirrors RimWorld.PawnUtility.GetPosture's non-Biotech fallback (return p.jobs.posture)
    // without ever touching ModsConfig.BiotechActive -- see the Harmony patch site for why.
    private static bool GetPostureResult(object __0, ref object __result)
    {
        var jobs = __0.GetType().GetField("jobs", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(__0)!;
        __result = jobs.GetType().GetField("posture", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(jobs)!;
        return false;
    }
    // Mirrors Reachability.CanReach's own ReachabilityImmediate.CanReachImmediate short
    // circuit for the only shape this fixture ever needs: a pawn already standing on the
    // (1x1) target cell. See the Harmony patch site for why the real method can't run at all.
    private static bool CanReachResult(object __0, object __1, ref bool __result)
    {
        var pawnPos = __0.GetType().GetProperty("Position")!.GetValue(__0)!;
        var destCell = __1.GetType().GetProperty("Cell")!.GetValue(__1)!;
        __result = pawnPos.Equals(destCell);
        return false;
    }
    private static int Main(string[] args)
    {
        var directories = args.Skip(1).Concat(new[] { Path.GetDirectoryName(Path.GetFullPath(args[0]))! }).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => { var path = directories.Select(d => Path.Combine(d, new AssemblyName(e.Name).Name + ".dll")).FirstOrDefault(File.Exists); return path == null ? null : Assembly.LoadFrom(path); };
        bridge = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        foreach (var reference in bridge.GetReferencedAssemblies()) Assembly.Load(reference);
        tools = bridge.GetType("HomeBridge.BridgeTools.NativeRoomObservationTools", true)!;
        const string scope = "\"scope\":{\"expectedIdentity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0}}";
        Func<string, string> request = fields => "{" + scope + (fields.Length == 0 ? "" : "," + fields) + "}";
        // N01.03: frozen paging is no longer refused outright -- a cursor within the
        // byte bound is accepted (its actual freshness is checked at read time by the
        // shared NativeObservationSnapshot.Cursor helper, not by Validate).
        foreach (var fields in new[] { "", "\"includeOutdoors\":false,\"includeBoundary\":false,\"includeCells\":false", "\"roomIds\":[\"0\",\"roomα\"]", "\"page\":{\"limit\":256}", "\"region\":{\"minimum\":{\"x\":0,\"z\":0},\"maximum\":{\"x\":0,\"z\":0}}", "\"page\":{\"cursor\":\"stale\"}" }) Check(Valid(request(fields)), "Valid bounded request");
        foreach (var bad in new[] { "{}", request("\"page\":{\"limit\":0}"), request("\"page\":{\"limit\":257}"), request("\"page\":{\"cursor\":\"" + new string('x', 4097) + "\"}"), request("\"roomIds\":[\"x\",\"x\"]"), request("\"roomIds\":[\"\"]"), request("\"roomIds\":[\"bad\\u0000id\"]"), request("\"region\":{\"minimum\":{\"x\":1,\"z\":0},\"maximum\":{\"x\":0,\"z\":1}}"), request("\"region\":{\"minimum\":{\"x\":0},\"maximum\":{\"x\":0,\"z\":1}}") }) Check(!Valid(bad), "Malformed bounded request refused");
        var defaultRequest = Wire("ListRoomsRequest", request(""));
        Check((bool)Call("Selected", defaultRequest, "0", false, false), "Ordinary room selected by default");
        Check(!(bool)Call("Selected", defaultRequest, "0", true, false), "Psychological outdoors excluded");
        Check(!(bool)Call("Selected", defaultRequest, "0", false, true), "Doorways excluded");
        var exact = Wire("ListRoomsRequest", request("\"includeOutdoors\":true,\"roomIds\":[\"1\"]"));
        Check((bool)Call("Selected", exact, "1", true, true), "Explicit outdoors includes matching doorway");
        Check(!(bool)Call("Selected", exact, "2", false, false), "Filters intersect rather than union");
        var game = Assembly.Load("Assembly-CSharp");
        var roomType = game.GetType("Verse.Room", true)!;
        var districtType = game.GetType("Verse.District", true)!;
        var emptyRoom = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(roomType);
        roomType.GetField("districts", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(emptyRoom,
            Activator.CreateInstance(typeof(List<>).MakeGenericType(districtType)));
        Check(!(bool)Call("HasPhysicalRegions", emptyRoom), "Regionless empty room is not physical geometry");
        var district = Activator.CreateInstance(districtType)!;
        ((IList)roomType.GetField("districts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(emptyRoom)!).Add(district);
        Check(!(bool)Call("HasPhysicalRegions", emptyRoom), "Native retained empty district does not invalidate complete physical census");
        roomType.GetField("cachedCellCount", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(emptyRoom, 1);
        Refused(() => Call("HasPhysicalRegions", emptyRoom), "Regionless room with claimed physical cells remains unavailable");
        ((IList)districtType.GetField("regions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(district)!).Add(System.Runtime.Serialization.FormatterServices.GetUninitializedObject(game.GetType("Verse.Region", true)!));
        Check((bool)Call("HasPhysicalRegions", emptyRoom), "Physical room proceeds to full geometry validation");
        var cellType = Assembly.Load("Assembly-CSharp").GetType("Verse.IntVec3", true)!;
        Func<int, int, object> cell = (x,z) => Activator.CreateInstance(cellType, x, 0, z)!;
        var points = Array.CreateInstance(cellType, 3); points.SetValue(cell(0,0),0); points.SetValue(cell(2,0),1); points.SetValue(cell(0,2),2);
        var center = Call("Center", points);
        Check(points.Cast<object>().Contains(center), "Center stays within concave/disconnected input footprint");
        var rectangle = Wire("Rectangle", "{\"minimum\":{\"x\":1,\"z\":1},\"maximum\":{\"x\":2,\"z\":2}}");
        Check((bool)Call("Inside", rectangle, cell(2,2)), "Maximum edge inclusive");
        Check(!(bool)Call("Inside", rectangle, cell(0,2)), "Outside rectangle excluded");
        foreach (var value in new[] { -20d, 0d, 100d }) Check((double)Call("Finite", value) == value, "Negative temperature/cleanliness and known zero preserved");
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity }) Refused(() => Call("Finite", value), "Nonfinite native fact cannot be a number");
        Refused(() => Wire("ListRoomsRequest", "{\"set\":true}"), "Mutation-shaped unknown field rejected");
        var server = Assembly.LoadFrom(directories.Select(d => Path.Combine(d,"RimBridgeServer.dll")).First(File.Exists));
        var binder = server.GetType("RimBridgeServer.AnnotatedExtensionCapabilityProvider",true)!.GetMethod("BindArguments",Flags)!;
        foreach (var value in new object?[] { "{}", new Dictionary<string,object>(), new List<object>(), null, 1, false })
        {
            var bound = (object[])binder.Invoke(null,new object?[] { tools.GetMethod("ListRooms"),new Dictionary<string,object?> { ["request"]=value },null,CancellationToken.None })!;
            Check(ReferenceEquals(bound[2],value),"SDK preserves raw boundary type");
        }
        var reply = (IDictionary)Call("Encode", Wire("ListRoomsReply", "{\"observed\":{\"rooms\":[{\"id\":\"0\",\"temperatureC\":0,\"outdoors\":false,\"stats\":[{\"defName\":\"Wealth\",\"unavailable\":{\"reason\":\"UNAVAILABLE_REASON_READ_FAILED\"}}]}]}}"));
        var room = ((IList)Get(Get(Wire("ListRoomsReply",(string)reply["payload"]!),"Observed"),"Rooms"))[0]!;
        Check((bool)Get(room,"HasTemperatureC") && (double)Get(room,"TemperatureC")==0,"Known zero temperature survives encoding");
        Check((bool)Get(room,"HasOutdoors") && !(bool)Get(room,"Outdoors"),"Known indoor false survives encoding");
        var stat = ((IList)Get(room,"Stats"))[0]!; Check(!(bool)Get(stat,"HasValue") && Get(stat,"Unavailable")!=null,"Unavailable stat is not zero");
        var oversized=(IDictionary)Call("Encode",Wire("ListRoomsReply","{\"unavailable\":{\"detail\":\""+new string('x',1024*1024)+"\"}}"));
        Check(Get(Wire("ListRoomsReply",(string)oversized["payload"]!),"Unavailable")!=null,"Oversized result refuses instead of truncating facts");

        // ---- N01.03: Project() fixture -- bed/stockpile membership + CAS token ----
        // Builds a minimal but real Room/District/Region/Map graph via reflection so
        // Project() runs its actual native-facing code (bed ownership/use/access,
        // stockpile contents, and the row.Snapshot CAS token) end-to-end, instead of
        // only the pure helpers exercised above.
        {
            const BindingFlags NF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags SF = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            object New(string t) => System.Runtime.Serialization.FormatterServices.GetUninitializedObject(game.GetType(t, true)!);
            Type Ty(string t) => game.GetType(t, true)!;
            void Set(object o, string declaringType, string field, object? v)
            {
                var f = Ty(declaringType).GetField(field, NF | SF) ?? throw new Exception("Missing field " + declaringType + "." + field);
                if (v is int iv && f.FieldType.IsEnum) v = Enum.ToObject(f.FieldType, iv);
                f.SetValue(f.IsStatic ? null : o, v);
            }
            object ListOf(string elementType) => Activator.CreateInstance(typeof(List<>).MakeGenericType(Ty(elementType)))!;
            object WireCommon(string name, string json) { var parser = bridge.GetType("RimGovernor.Protocol.Common." + name, true)!.GetProperty("Parser")!.GetValue(null)!; return parser.GetType().GetMethod("ParseJson")!.Invoke(parser, new object[] { json })!; }
            var cell2Type = Ty("Verse.IntVec2");
            object cell2(int x, int z) => Activator.CreateInstance(cell2Type, x, z)!;
            string LoadId(object o) => (string)o.GetType().GetMethod("GetUniqueLoadID")!.Invoke(o, null)!;
            object GetField(object o, string declaringType, string field) => Ty(declaringType).GetField(field, NF)!.GetValue(o)!;

            // This headless host has no real Unity player loop. Several native methods
            // reached transitively below contain IL that *references* a Unity ECall (an
            // internal-call stub only registered inside a running Unity player) even on a
            // branch that is never actually taken by our fixture data. Under the classic
            // .NET Framework CLR (which this suite targets, matching the other native-proto-*
            // projects' standalone net472 design and their real net472 RimWorld/Harmony
            // assemblies), JIT-compiling such a method fails immediately with
            // "SecurityException: ECall methods must be packaged into a system module" the
            // moment that method is first invoked -- regardless of which branch runs at
            // runtime, because the whole method body is compiled as one unit. The fix is not
            // to avoid the branch (impossible, since it's JIT-time, not runtime) but to
            // prevent the offending method from ever being JIT-compiled at all: Harmony
            // patches install a *replacement* method and only invoke the original's compiled
            // body when a prefix allows it, so a prefix that unconditionally returns false
            // means the original (and its embedded ECall reference) is never JIT'd.
            // Loaded purely via reflection -- 0Harmony.dll is one of the directories this
            // suite already resolves against -- same technique as native-proto-supplies.
            var harmonyAsm = Assembly.Load("0Harmony");
            var harmonyType = harmonyAsm.GetType("HarmonyLib.Harmony", true)!;
            var harmony = Activator.CreateInstance(harmonyType, "test.native-proto-rooms.fixtures")!;
            var harmonyMethodType = harmonyAsm.GetType("HarmonyLib.HarmonyMethod", true)!;
            var patchMethod = harmonyType.GetMethod("Patch", new[] { typeof(MethodBase), harmonyMethodType, harmonyMethodType, harmonyMethodType, harmonyMethodType })!;

            // Verse.Log.Warning/Error/Message/*Once all eventually reach
            // UnityEngine.Debug/StackTraceUtility; native code (e.g. an uninitialized DefOf
            // via DefOfHelper.EnsureInitializedInCtor) can call these incidentally. Skip them
            // outright rather than relying on Log.logDisablers (a *runtime* branch inside
            // those same methods -- it can't prevent the JIT-time crash described above).
            void PatchDiag(MethodBase m, object prefix)
            {
                try { patchMethod.Invoke(harmony, new object?[] { m, prefix, null, null, null }); }
                catch (Exception ex) { throw new Exception("Failed patching " + m.DeclaringType!.FullName + "." + m.Name, ex); }
            }
            var skipOriginalHarmony = Activator.CreateInstance(harmonyMethodType, typeof(Program).GetMethod(nameof(SkipOriginal), Flags)!);
            var logType = Ty("Verse.Log");
            foreach (var name in new[] { "Warning", "Error", "Message", "ErrorOnce", "WarningOnce" })
                foreach (var m in logType.GetMethods(SF).Where(x => x.Name == name))
                    PatchDiag(m, skipOriginalHarmony);

            // RimWorld.RestUtility.CurrentBed -> PawnUtility.GetPosture reads
            // ModsConfig.BiotechActive, which (on first touch of the ModsConfig type, per
            // ordinary CLR static-init semantics) runs ModsConfig's static constructor, which
            // eventually calls Verse.GenFilePaths.get_SaveDataFolderPath -- whose own IL
            // directly calls the ECall UnityEngine.Application.get_persistentDataPath on an
            // unreachable branch. Unlike Verse.Log's methods (which only call *into* regular
            // managed UnityEngine methods, deferring any ECall several frames deeper), that
            // ECall is a direct callee of get_SaveDataFolderPath's own body.
            //
            // Two approaches were tried and rejected before this one: (1) patching
            // get_SaveDataFolderPath directly -- Harmony's patch installation itself needs to
            // JIT-prepare the original method it's redirecting, which throws the same ECall
            // SecurityException at patch-installation time, before it's ever invoked; and
            // (2) patching ModsConfig's own static constructor (whose IL, confirmed via Cecil,
            // has no direct ECall reference) -- but preparing/patching a method of a type
            // still forces the CLR to run that type's static constructor first as an ordinary
            // side effect of touching the type, so Harmony ends up running the very cctor it
            // was trying to intercept before the patch is even installed.
            //
            // Patching PawnUtility.GetPosture itself sidesteps both problems: its own IL never
            // directly calls an ECall (only ModsConfig.BiotechActive's getter, whose type-init
            // trigger only fires if that call actually executes), and preparing GetPosture
            // does not require preparing ModsConfig at all. The prefix below replicates
            // GetPosture's non-Biotech fallback -- return p.jobs.posture -- directly via
            // reflection, which is exactly correct for this fixture (no DLC-gated content is
            // involved), instead of running the real method body.
            var getPosture = Ty("RimWorld.PawnUtility").GetMethod("GetPosture", SF)!;
            var getPostureHarmony = Activator.CreateInstance(harmonyMethodType, typeof(Program).GetMethod(nameof(GetPostureResult), Flags)!);
            PatchDiag(getPosture, getPostureHarmony);

            // GameCondition_UnnaturalDarkness.AffectedByDarkness (reached from
            // TraverseParms.For inside CanReach, computing avoidDarknessDanger) reads
            // ModsConfig.AnomalyActive -- the same first-touch-triggers-the-crashing-cctor
            // hazard, under a different call path than GetPosture's.
            //
            // Patching ModsConfig's own members directly (its cctor, or any of its
            // *Active property getters) was also tried and rejected: preparing/patching ANY
            // method belonging to the ModsConfig type -- not just its static constructor --
            // forces the CLR to run ModsConfig's static constructor first, as an ordinary side
            // effect of Harmony obtaining that method's real entry point, regardless of which
            // member is being patched. The only viable approach is what worked for
            // GetPosture: patch the external caller instead, before it ever touches
            // ModsConfig. There is no active GameCondition in this headless fixture map, so
            // "not affected by darkness" is the correct answer here, not just a safe stand-in.
            var falseResultHarmony = Activator.CreateInstance(harmonyMethodType, typeof(Program).GetMethod(nameof(FalseResult), Flags)!);
            var affectedByDarkness = Ty("RimWorld.GameCondition_UnnaturalDarkness").GetMethod("AffectedByDarkness", SF)!;
            PatchDiag(affectedByDarkness, falseResultHarmony);

            // Verse.Reachability.CanReach(IntVec3, LocalTargetInfo, PathEndMode, TraverseParms)
            // -- the real region/pathgrid BFS entry point -- cannot even be *patched*, let
            // alone run, under net472: Harmony's own patch machinery (MethodCreator, via
            // MonoMod) needs to copy the original method's IL to build the replacement, which
            // requires resolving the FieldType of every field it touches -- and one of those
            // fields' types uses C# 8 default interface methods, which the classic .NET
            // Framework type loader cannot load at all (the same "non-abstract, non-.cctor
            // method in an interface" failure as Verse.FogGrid at the very start of this
            // investigation). This happens at Harmony's Patch() call itself, before the method
            // is ever invoked, so patching this exact method is not viable under net472 --
            // same class of problem as Verse.GenFilePaths.get_SaveDataFolderPath above.
            //
            // Instead, patch the public entry point our observation code actually calls --
            // Verse.ReachabilityUtility.CanReach(Pawn, LocalTargetInfo, PathEndMode, Danger,
            // bool, bool, TraverseMode), the extension method behind `p.CanReach(bed, ...)`
            // (bed implicitly converts to LocalTargetInfo) -- whose own IL only touches
            // simple types (Pawn, LocalTargetInfo, TraverseParms, enums) and patches cleanly. The
            // prefix replicates exactly the one shortcut this fixture ever needs:
            // ReachabilityImmediate.CanReachImmediate's short circuit, true whenever the
            // pawn's own cell already equals the (1x1) target's cell, without ever
            // constructing a TraverseParms or touching the real region/pathgrid graph.
            var canReachHarmony = Activator.CreateInstance(harmonyMethodType, typeof(Program).GetMethod(nameof(CanReachResult), Flags)!);
            var canReach = Ty("Verse.ReachabilityUtility").GetMethods(SF)
                .First(m => m.Name == "CanReach" && m.GetParameters().Length == 7 && m.GetParameters()[1].ParameterType == Ty("Verse.LocalTargetInfo"));
            PatchDiag(canReach, canReachHarmony);

            // Room.get_Fogged (which Project() reads unconditionally) walks the room's
            // regions down to FogGrid.IsFogged, which touches a Unity.Collections.NativeBitArray
            // field that is only ever allocated by the real Unity player -- constructing/using
            // a real FogGrid outside it is unreliable, and (per the JIT-time issue above)
            // merely resolving Verse.FogGrid's type outright throws under net472 regardless.
            // This is an environment-only gap (RimWorld's own Mono/Unity runtime initializes
            // that field fine), unrelated to the code under test, so skip Room.get_Fogged's
            // real body outright and return false -- FogGrid/NativeBitArray is never touched.
            var roomFogged = Ty("Verse.Room").GetProperty("Fogged", NF)!.GetGetMethod(true)!;
            PatchDiag(roomFogged, falseResultHarmony);

            const int mapSize = 10;
            var fixMap = New("Verse.Map");
            var mapInfo = New("Verse.MapInfo");
            Set(mapInfo, "Verse.MapInfo", "sizeInt", cell(mapSize, mapSize));
            Set(fixMap, "Verse.Map", "info", mapInfo);
            var cellIndices = Activator.CreateInstance(Ty("Verse.CellIndices"))!;
            Ty("Verse.CellIndices").GetField("sizeX", NF)!.SetValue(cellIndices, mapSize);
            Ty("Verse.CellIndices").GetField("sizeZ", NF)!.SetValue(cellIndices, mapSize);
            Set(fixMap, "Verse.Map", "cellIndices", cellIndices);
            var thingListType = typeof(List<>).MakeGenericType(Ty("Verse.Thing"));
            var thingGridArray = Array.CreateInstance(thingListType, mapSize * mapSize);
            for (var i = 0; i < thingGridArray.Length; i++) thingGridArray.SetValue(Activator.CreateInstance(thingListType), i);
            var fixThingGrid = New("Verse.ThingGrid");
            Set(fixThingGrid, "Verse.ThingGrid", "map", fixMap);
            Set(fixThingGrid, "Verse.ThingGrid", "thingGrid", thingGridArray);
            // EmptyThingList is a readonly static already initialized by ThingGrid's
            // own static/instance constructor; leave it as-is.
            Set(fixMap, "Verse.Map", "thingGrid", fixThingGrid);
            Set(fixMap, "Verse.Map", "uniqueID", 0);
            // fogGrid is deliberately left null: Room.get_Fogged is Harmony-patched above to
            // return false without running its real body, so FogGrid is never dereferenced.
            var regionDirtyer = New("Verse.RegionDirtyer");
            Set(regionDirtyer, "Verse.RegionDirtyer", "dirtyCells", Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(cellType)));
            Set(fixMap, "Verse.Map", "regionDirtyer", regionDirtyer);
            var rau = New("Verse.RegionAndRoomUpdater");
            Set(rau, "Verse.RegionAndRoomUpdater", "map", fixMap);
            Set(rau, "Verse.RegionAndRoomUpdater", "enabledInt", true);
            Set(rau, "Verse.RegionAndRoomUpdater", "initialized", true); // skip RebuildAllRegionsAndRooms's real region/roof/pathing pass.
            Set(fixMap, "Verse.Map", "regionAndRoomUpdater", rau);
            var regionArray = Array.CreateInstance(Ty("Verse.Region"), mapSize * mapSize);
            var fixRegionGrid = New("Verse.RegionGrid");
            Set(fixRegionGrid, "Verse.RegionGrid", "map", fixMap);
            Set(fixRegionGrid, "Verse.RegionGrid", "regionGrid", regionArray);
            Set(fixMap, "Verse.Map", "regionGrid", fixRegionGrid);
            var zoneArray = Array.CreateInstance(Ty("Verse.Zone"), mapSize * mapSize);
            var fixZoneManager = New("Verse.ZoneManager");
            Set(fixZoneManager, "Verse.ZoneManager", "map", fixMap);
            Set(fixZoneManager, "Verse.ZoneManager", "zoneGrid", zoneArray);
            Set(fixMap, "Verse.Map", "zoneManager", fixZoneManager);
            var fixMapPawns = New("Verse.MapPawns");
            Set(fixMapPawns, "Verse.MapPawns", "map", fixMap);
            var pawnsSpawnedList = ListOf("Verse.Pawn");
            Set(fixMapPawns, "Verse.MapPawns", "pawnsSpawned", pawnsSpawnedList);
            Set(fixMap, "Verse.Map", "mapPawns", fixMapPawns);
            // AccessibleTo's CanReach check only needs to succeed for a pawn standing directly
            // on the bed's (1x1) cell: Reachability.CanReach short-circuits via
            // ReachabilityImmediate.CanReachImmediate (root cell == target cell) before ever
            // touching the real region/pathgrid BFS machinery, so a minimal Reachability with
            // just its map field populated is sufficient.
            var fixReachability = New("Verse.Reachability");
            Set(fixReachability, "Verse.Reachability", "map", fixMap);
            Set(fixReachability, "Verse.Reachability", "working", false);
            Set(fixMap, "Verse.Map", "reachability", fixReachability);

            // Register the fixture map so Thing.Map/Spawned (which resolve through
            // Find.Maps[mapIndexOrState]) see it.
            var fixGame = New("Verse.Game");
            var mapsList = ListOf("Verse.Map"); ((IList)mapsList).Add(fixMap);
            Set(fixGame, "Verse.Game", "maps", mapsList);
            Ty("Verse.Current").GetField("gameInt", SF)!.SetValue(null, fixGame);

            int CellIndex(int x, int z) => z * mapSize + x;
            void PutThingAt(object thing, int x, int z) => ((IList)thingGridArray.GetValue(CellIndex(x, z))!).Add(thing);

            var factionDef = New("RimWorld.FactionDef");
            Set(factionDef, "RimWorld.FactionDef", "isPlayer", true);
            var playerFaction = New("RimWorld.Faction");
            Set(playerFaction, "RimWorld.Faction", "def", factionDef);
            var humanRace = New("Verse.RaceProperties");
            Set(humanRace, "Verse.RaceProperties", "intelligence", 2 /* Humanlike */);
            var colonistDef = New("Verse.ThingDef");
            Set(colonistDef, "Verse.ThingDef", "race", humanRace);
            Set(colonistDef, "Verse.ThingDef", "defName", "N0103TestColonist");
            var nextThingId = 1;

            object NewColonist(int x, int z, bool spawned)
            {
                var pawn = New("Verse.Pawn");
                Set(pawn, "Verse.Thing", "def", colonistDef);
                Set(pawn, "Verse.Thing", "thingIDNumber", nextThingId++);
                Set(pawn, "Verse.Thing", "positionInt", cell(x, z));
                Set(pawn, "Verse.Thing", "mapIndexOrState", spawned ? (sbyte)0 : (sbyte)-1);
                Set(pawn, "Verse.Thing", "factionInt", playerFaction);
                var health = New("Verse.Pawn_HealthTracker");
                Set(health, "Verse.Pawn_HealthTracker", "healthState", 2 /* Mobile */);
                Set(pawn, "Verse.Pawn", "health", health);
                var mindState = New("Verse.AI.Pawn_MindState");
                Set(mindState, "Verse.AI.Pawn_MindState", "mentalStateHandler", New("Verse.AI.MentalStateHandler"));
                Set(pawn, "Verse.Pawn", "mindState", mindState);
                var drafter = New("RimWorld.Pawn_DraftController");
                Set(drafter, "RimWorld.Pawn_DraftController", "draftedInt", true); // bypasses ForbidUtility.CaresAboutForbidden's Lord-loop entirely.
                Set(pawn, "Verse.Pawn", "drafter", drafter);
                var jobs = New("Verse.AI.Pawn_JobTracker");
                Set(pawn, "Verse.Pawn", "jobs", jobs);
                return pawn;
            }

            // Bed: a 1x1 built Building_Bed so ReachabilityImmediate's same-cell
            // shortcut applies without needing real pathfinding/region infrastructure.
            var bedDef = New("Verse.ThingDef");
            Set(bedDef, "Verse.ThingDef", "size", cell2(1, 1));
            Set(bedDef, "Verse.ThingDef", "thingClass", Ty("RimWorld.Building_Bed"));
            Set(bedDef, "Verse.ThingDef", "defName", "N0103TestBed");
            const int bedX = 5, bedZ = 5;
            var bed = New("RimWorld.Building_Bed");
            Set(bed, "Verse.Thing", "def", bedDef);
            Set(bed, "Verse.Thing", "thingIDNumber", nextThingId++);
            Set(bed, "Verse.Thing", "positionInt", cell(bedX, bedZ));
            Set(bed, "Verse.Thing", "mapIndexOrState", (sbyte)0);
            // Owner: spawned but elsewhere, so Entity() (which needs thing.Map) is safe
            // while CanReach's same-cell shortcut still fails it -- an owner need not be
            // a user or accessible.
            var ownerPawn = NewColonist(2, 2, spawned: true);
            var assignedPawns = ListOf("Verse.Pawn"); ((IList)assignedPawns).Add(ownerPawn);
            var compAssign = New("RimWorld.CompAssignableToPawn");
            Set(compAssign, "RimWorld.CompAssignableToPawn", "assignedPawns", assignedPawns);
            var compsList = ListOf("Verse.ThingComp"); ((IList)compsList).Add(compAssign);
            Set(bed, "Verse.ThingWithComps", "comps", compsList);
            PutThingAt(bed, bedX, bedZ);

            // NativeBuildingObservationTools.Project(bed) computes bed.LabelCap, which
            // unconditionally reads Thing.MaxHitPoints (RimWorld's GenLabel.LabelExtras
            // calls it before checking def.useHitPoints). Thing.MaxHitPoints resolves
            // through the real RimWorld.StatDefOf.MaxHitPoints StatDef/StatWorker chain,
            // which this headless fixture never bootstraps via a live DefDatabase. Make
            // that one stat immutable with a pre-seeded cache entry so StatWorker.GetValue
            // returns immediately without ever touching Find.TickManager or any other
            // uninitialized native singleton.
            var maxHpStat = New("RimWorld.StatDef");
            Set(maxHpStat, "RimWorld.StatDef", "immutable", true);
            var maxHpWorker = New("RimWorld.StatWorker");
            Set(maxHpWorker, "RimWorld.StatWorker", "stat", maxHpStat);
            var immutableStatCacheType = typeof(System.Collections.Concurrent.ConcurrentDictionary<,>).MakeGenericType(Ty("Verse.Thing"), typeof(float));
            var immutableStatCache = Activator.CreateInstance(immutableStatCacheType)!;
            Set(maxHpWorker, "RimWorld.StatWorker", "immutableStatCache", immutableStatCache);
            Set(maxHpStat, "RimWorld.StatDef", "workerInt", maxHpWorker);
            Ty("RimWorld.StatDefOf").GetField("MaxHitPoints", SF)!.SetValue(null, maxHpStat);
            immutableStatCacheType.GetMethod("TryAdd")!.Invoke(immutableStatCache, new object[] { bed, 100f });

            var userPawn = NewColonist(bedX, bedZ, spawned: true); // sleeping in the bed: CurrentBed() == bed, and reachable/not-forbidden.
            var userJob = New("Verse.AI.Job");
            // CanReach's TraverseParms.For reads pawn.CurJob.GetCachedDriver(pawn), which
            // calls Job.MakeDriver (needs a real JobDef.driverClass) unless a cachedDriver
            // is already present and already bound to this pawn. Pre-seed a trivial driver
            // so that lookup short-circuits instead of trying to construct one.
            var userJobDriver = New("Verse.AI.JobDriver_Wait");
            Set(userJobDriver, "Verse.AI.JobDriver", "pawn", userPawn);
            Set(userJobDriver, "Verse.AI.JobDriver", "job", userJob);
            Set(userJob, "Verse.AI.Job", "cachedDriver", userJobDriver);
            Set(GetField(userPawn, "Verse.Pawn", "jobs"), "Verse.AI.Pawn_JobTracker", "curJob", userJob);
            Set(GetField(userPawn, "Verse.Pawn", "jobs"), "Verse.AI.Pawn_JobTracker", "posture", 5 /* LayingInBed */);
            PutThingAt(userPawn, bedX, bedZ);
            var excludedPawn = NewColonist(9, 9, spawned: false); // present in the colonist census, but neither a user nor reachable (unspawned).
            ((IList)pawnsSpawnedList).Add(userPawn); ((IList)pawnsSpawnedList).Add(excludedPawn);
            var roomPawns = ListOf("Verse.Pawn"); ((IList)roomPawns).Add(userPawn); // only the truly-spawned in-room occupant.

            // Region/District/Room wiring: one region whose ListerThings carries the
            // bed (so Room.ContainedAndAdjacentThings finds it) and whose extentsClose
            // covers the bed's own cell. (Room.Fogged itself is Harmony-patched to skip
            // its real body entirely -- see the patch installed above.)
            var listerThings = New("Verse.ListerThings");
            var groupArray = Array.CreateInstance(thingListType, 3);
            for (var i = 0; i < 3; i++) groupArray.SetValue(Activator.CreateInstance(thingListType), i);
            ((IList)groupArray.GetValue(2)!).Add(bed);
            Set(listerThings, "Verse.ListerThings", "listsByGroup", groupArray);
            var region = New("Verse.Region");
            Set(region, "Verse.Region", "listerThings", listerThings);
            Set(region, "Verse.Region", "type", 2 /* Normal */);
            Set(region, "Verse.Region", "extentsClose", Activator.CreateInstance(Ty("Verse.CellRect"), bedX, bedZ, 1, 1));
            regionArray.SetValue(region, CellIndex(bedX, bedZ));
            var fixDistrict = New("Verse.District");
            var regionsInDistrict = ListOf("Verse.Region"); ((IList)regionsInDistrict).Add(region);
            Set(fixDistrict, "Verse.District", "regions", regionsInDistrict);

            const int zoneX = 7, zoneZ = 7;
            var steelDef = New("Verse.ThingDef");
            Set(steelDef, "Verse.ThingDef", "thingClass", Ty("Verse.Thing"));
            Set(steelDef, "Verse.ThingDef", "thingCategories", (object)(((Func<object>)(() => { var l = ListOf("Verse.ThingCategoryDef"); ((IList)l).Add(New("Verse.ThingCategoryDef")); return l; }))()));
            Set(steelDef, "Verse.ThingDef", "category", 2); // ThingCategory.Item -- required for ThingDef.EverStorable, which SlotGroup.HeldThings checks.
            Set(steelDef, "Verse.ThingDef", "defName", "N0103TestSteel");
            var woodDef = New("Verse.ThingDef");
            Set(woodDef, "Verse.ThingDef", "thingClass", Ty("Verse.Thing"));
            Set(woodDef, "Verse.ThingDef", "thingCategories", (object)(((Func<object>)(() => { var l = ListOf("Verse.ThingCategoryDef"); ((IList)l).Add(New("Verse.ThingCategoryDef")); return l; }))()));
            Set(woodDef, "Verse.ThingDef", "category", 2); // ThingCategory.Item
            Set(woodDef, "Verse.ThingDef", "defName", "N0103TestWood");
            object NewStack(object def, int stackCount)
            {
                var thing = New("Verse.Thing");
                Set(thing, "Verse.Thing", "def", def);
                Set(thing, "Verse.Thing", "thingIDNumber", nextThingId++);
                Set(thing, "Verse.Thing", "positionInt", cell(zoneX, zoneZ));
                Set(thing, "Verse.Thing", "mapIndexOrState", (sbyte)0);
                Set(thing, "Verse.Thing", "stackCount", stackCount);
                return thing;
            }
            var steelA = NewStack(steelDef, 25); var steelB = NewStack(steelDef, 25); var wood = NewStack(woodDef, 10);
            PutThingAt(steelA, zoneX, zoneZ); PutThingAt(steelB, zoneX, zoneZ); PutThingAt(wood, zoneX, zoneZ);
            var zone = New("RimWorld.Zone_Stockpile");
            Set(zone, "Verse.Zone", "zoneManager", fixZoneManager);
            Set(zone, "Verse.Zone", "ID", 42);
            var zoneCells = Activator.CreateInstance(typeof(List<>).MakeGenericType(cellType))!; ((IList)zoneCells).Add(cell(zoneX, zoneZ));
            Set(zone, "Verse.Zone", "cells", zoneCells);
            var slotGroup = New("RimWorld.SlotGroup");
            Set(slotGroup, "RimWorld.SlotGroup", "parent", zone);
            Set(zone, "RimWorld.Zone_Stockpile", "slotGroup", slotGroup);
            zoneArray.SetValue(zone, CellIndex(zoneX, zoneZ));

            var fixRoom = New("Verse.Room");
            var districtsInRoom = ListOf("Verse.District"); ((IList)districtsInRoom).Add(fixDistrict);
            Set(fixRoom, "Verse.Room", "districts", districtsInRoom);
            Set(fixRoom, "Verse.Room", "ID", 900001);
            Set(fixRoom, "Verse.Room", "cachedCellCount", 2);
            Set(fixRoom, "Verse.Room", "cachedOpenRoofCount", 0);
            var tempTracker = New("Verse.RoomTempTracker");
            Set(tempTracker, "Verse.RoomTempTracker", "temperatureInt", 20f);
            Set(fixRoom, "Verse.Room", "tempTracker", tempTracker);
            Set(fixRoom, "Verse.Room", "uniqueContainedThingsSet", Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(Ty("Verse.Thing"))));
            Set(fixRoom, "Verse.Room", "uniqueContainedThings", ListOf("Verse.Thing"));
            Set(fixRoom, "Verse.Room", "tmpRegions", ListOf("Verse.Region"));

            var roomCells = new List<object> { cell(bedX, bedZ), cell(zoneX, zoneZ) };
            var cellsListTyped = Activator.CreateInstance(typeof(List<>).MakeGenericType(cellType))!;
            foreach (var c in roomCells) ((IList)cellsListTyped).Add(c);

            var projectRequest = Wire("ListRoomsRequest", request("\"includeBoundary\":true"));
            var context = WireCommon("ObservationContext", "{\"identity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0}}");

            object ProjectRow() => tools.GetMethod("Project", Flags)!.Invoke(null, new[] { fixRoom, fixMap, cellsListTyped, roomPawns, projectRequest, context })!;

            var rowFirst = ProjectRow();
            var bedMemberships = (IList)Get(rowFirst, "BedMemberships");
            Check(bedMemberships.Count == 1, "Bed membership recorded for the room's single bed");
            var membership = bedMemberships[0]!;
            var owners = (IList)Get(membership, "Owners"); var users = (IList)Get(membership, "Users"); var accessible = (IList)Get(membership, "AccessibleTo");
            Check(owners.Count == 1 && (string)Get(owners[0]!, "Id") == LoadId(ownerPawn), "Bed owner reflects OwnersForReading");
            Check(users.Count == 1 && (string)Get(users[0]!, "Id") == LoadId(userPawn), "Only the pawn actually resting in the bed counts as a user");
            Check(accessible.Count == 1 && (string)Get(accessible[0]!, "Id") == LoadId(userPawn), "Only the reachable (same-cell) colonist counts as accessible; unspawned colonist excluded");

            var stockpileMemberships = (IList)Get(rowFirst, "StockpileMemberships");
            Check(stockpileMemberships.Count == 1, "Stockpile membership recorded for the room's single zone");
            var stockMembership = stockpileMemberships[0]!;
            Check((string)Get(stockMembership, "ZoneId") == "Zone_42", "Stockpile membership carries the zone's unique load ID");
            var contents = (IList)Get(stockMembership, "Contents");
            Check(contents.Count == 2, "Stockpile contents grouped by def: steel and wood");
            var steelStock = contents[0]!; var woodStock = contents[1]!;
            Check((string)Get(Get(steelStock, "Definition"), "DefName") == "N0103TestSteel" && (long)Get(steelStock, "Units") == 50, "Multiple steel stacks in a stockpile sum into one grouped total");
            Check((string)Get(Get(woodStock, "Definition"), "DefName") == "N0103TestWood" && (long)Get(woodStock, "Units") == 10, "Distinct def stacked separately in the same stockpile");

            // CAS token: identical inputs must be deterministic, and a change that
            // affects bed ownership must invalidate the room's token.
            var rowSecond = ProjectRow();
            var tokenFirst = (string)Get(Get(rowFirst, "Snapshot"), "Token");
            var tokenSecond = (string)Get(Get(rowSecond, "Snapshot"), "Token");
            Check(tokenFirst == tokenSecond, "Identical Project() inputs produce an identical CAS token");

            var ownerPawn2 = NewColonist(3, 3, spawned: true);
            ((IList)assignedPawns).Add(ownerPawn2); // mutate bed ownership -- should change the room's layout-derived token.
            var rowThird = ProjectRow();
            var tokenThird = (string)Get(Get(rowThird, "Snapshot"), "Token");
            Check(tokenThird != tokenFirst, "Changed bed ownership invalidates the room's CAS token");

            Console.WriteLine(checks+" compiled room boundary assertions passed; native room acceptance is separate.");return 0;
        }
    }
}
